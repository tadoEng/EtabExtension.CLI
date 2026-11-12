using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;

namespace EtabExtension.CLI.Tests;

// Fakes for the #747-D control-plane tests (C7a, C8, C10). Everything here is ETABS-free.

internal sealed record ControlEcho(string Value);

internal static class Shutdowns
{
    public static ManagedEtabsShutdownResult Ok(bool forced = false) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: false,
            ApplicationExitReturnCode: 0,
            OwnedPid: Kit.Identity.Pid));

    public static ManagedEtabsShutdownResult Unconfirmed() => new(
        false,
        ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
        "unconfirmed",
        new(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            ProcessExitConfirmed: false,
            Forced: true,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: Kit.Identity.Pid));
}

/// <summary>An <see cref="IEtabsSession"/> whose every destructive call is counted.</summary>
internal sealed class ProbeSession : IEtabsSession
{
    private int _normal;
    private int _blocked;
    private int _detaches;

    public int NormalShutdowns => Volatile.Read(ref _normal);

    public int BlockedShutdowns => Volatile.Read(ref _blocked);

    public int Detaches => Volatile.Read(ref _detaches);

    public TimeSpan? GateWaitPassed { get; private set; }

    public Func<ManagedEtabsShutdownResult> NormalShutdown { get; set; } = () => Shutdowns.Ok();

    public Func<ManagedEtabsShutdownResult> BlockedShutdown { get; set; } = () => Shutdowns.Ok(forced: true);

    public Func<DetachResult> DetachBehavior { get; set; } = () => new(
        DetachOutcome.Detached,
        null,
        null,
        Kit.Identity,
        Guid.Parse("11111111-1111-1111-1111-111111111111"));

    public bool Started { get; set; }

    public bool UserVisible { get; set; }

    public int? Pid { get; set; } = Kit.Identity.Pid;

    public EtabsDisposition DispositionValue { get; set; } = EtabsDisposition.None;

    public Guid? LaunchId { get; set; }

    public bool IsStarted => Started;

    public int? ProcessId => Pid;

    public bool IsUserVisible => UserVisible;

    public EtabsDisposition Disposition => DispositionValue;

    public Guid? LaunchRecordId => LaunchId;

    public ETABSApplication GetOrStart() => null!;

    public IManagedEtabsApplication GetOrStartOwned() => throw new NotSupportedException();

    public Result RevealForExplicitUserRequest() => Result.Ok();

    public Result CertifyNoUnconsentedExposure() => Result.Ok();

    public void MarkVisibilityStage(string stage)
    {
    }

    public ManagedEtabsShutdownResult Shutdown()
    {
        _ = Interlocked.Increment(ref _normal);
        return NormalShutdown();
    }

    public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked()
    {
        _ = Interlocked.Increment(ref _blocked);
        return BlockedShutdown();
    }

    public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked(TimeSpan gateWait)
    {
        GateWaitPassed = gateWait;
        return ShutdownWhileWorkerBlocked();
    }

    public DetachResult Detach()
    {
        _ = Interlocked.Increment(ref _detaches);
        return DetachBehavior();
    }

    public void Dispose()
    {
    }
}

/// <summary>A worker that is either busy for ever (never runs its queue) or runs each item at once.</summary>
internal sealed class ProbeWorker(bool busy) : IStaExecutionWorker
{
    private int _disposed;

    public int Disposed => Volatile.Read(ref _disposed);

    public Task<T> ExecuteAsync<T>(Func<Task<T>> action) =>
        busy
            ? new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously).Task
            : Kit.OnDedicatedThread(() => action().GetAwaiter().GetResult());

    public void Dispose() => Interlocked.Increment(ref _disposed);
}

internal sealed class ProbeWatchdog : IEtabsModalDialogWatchdog
{
    public bool IsBlocked { get; set; }

    public EtabsDialogCheck Check { get; set; } = EtabsDialogCheck.Clean;

    public Result BlockedFailure() => Result.Fail("blocked");

    public Task<object> GuardAsync(Func<int?> ownedPid, Func<Task<object>> work, CancellationToken ct) => work();

    public EtabsDialogCheck CheckNow(Func<int?> ownedPid) => Check;
}

/// <summary>A reader the test feeds line by line and closes to signal EOF.</summary>
internal sealed class ScriptedLines : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private int _read;

    public int LinesRead => Volatile.Read(ref _read);

    public void Send(string line) => _lines.Writer.TryWrite(line);

    public void Close() => _lines.Writer.TryComplete();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            var line = await _lines.Reader.ReadAsync(cancellationToken);
            _ = Interlocked.Increment(ref _read);
            return line;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public override string? ReadLine() => throw new NotSupportedException();
}

/// <summary>
/// Collects whole frames and detects two writes overlapping in time: a frame written without
/// the loop's write gate would interleave with another.
/// </summary>
internal sealed class FrameCollector : TextWriter
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private int _active;
    private int _maxActive;

    public override Encoding Encoding => Encoding.UTF8;

    public int MaxConcurrentWrites => Volatile.Read(ref _maxActive);

    /// <summary>When set, each write lingers this long, so an unguarded overlap is certain to show.</summary>
    public TimeSpan WriteLinger { get; set; }

    public override async Task WriteLineAsync(string? value)
    {
        var active = Interlocked.Increment(ref _active);
        lock (_gate)
        {
            _maxActive = Math.Max(_maxActive, active);
        }

        if (WriteLinger > TimeSpan.Zero)
        {
            await Task.Delay(WriteLinger);
        }

        lock (_gate)
        {
            if (value is not null)
            {
                _lines.Add(value);
            }
        }

        _ = Interlocked.Decrement(ref _active);
    }

    public override void Write(char value) => throw new NotSupportedException();

    public override Task FlushAsync() => Task.CompletedTask;

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Correlated frames only (the handshake carries no id). Throws if a frame is not one whole JSON object.</summary>
    public IReadOnlyList<JsonElement> Frames()
    {
        lock (_gate)
        {
            return _lines
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(element => element.TryGetProperty("id", out _))
                .ToList();
        }
    }

    public JsonElement? Frame(long id) =>
        Frames().Cast<JsonElement?>().FirstOrDefault(frame => frame!.Value.GetProperty("id").GetInt64() == id);
}

/// <summary>A coordinator that records what the loop asked of it.</summary>
internal sealed class RecordingCoordinator : IServeShutdownCoordinator
{
    private int _shutdown;
    private int _detach;
    private int _eof;

    public int ShutdownCount => Volatile.Read(ref _shutdown);

    public int DetachCount => Volatile.Read(ref _detach);

    public int EofCount => Volatile.Read(ref _eof);

    public ServeEofPolicy? EofPolicy { get; private set; }

    public Result<DetachData> DetachAnswer { get; set; } = Result.Ok(new DetachData(
        true,
        new(Kit.Identity.Pid, Kit.Identity.ProcessStartTimeUtc, Kit.Identity.ExecutablePath),
        Guid.Parse("22222222-2222-2222-2222-222222222222")));

    public Task<Result<ManagedEtabsShutdownData>> ShutdownAsync()
    {
        _ = Interlocked.Increment(ref _shutdown);
        return Task.FromResult(Result.Ok(Shutdowns.Ok().Data));
    }

    public Task<Result<DetachData>> DetachAsync()
    {
        _ = Interlocked.Increment(ref _detach);
        return Task.FromResult(DetachAnswer);
    }

    public Task<Result<ManagedEtabsShutdownData>> EndOfInputAsync(ServeEofPolicy policy)
    {
        EofPolicy = policy;
        _ = Interlocked.Increment(ref _eof);
        return Task.FromResult(Result.Ok(Shutdowns.Ok().Data));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An operation manager that runs synchronous work at once and has no operation.</summary>
internal sealed class PassThroughOperations : IOperationManager
{
    public bool HasActiveOperation => false;

    public Result<StartOperationData> Start(string kind, JsonElement payload) => throw new NotSupportedException();

    public Result<OperationStatusData> GetStatus(string operationId) => throw new NotSupportedException();

    public Result<GetOperationEventsData> GetEvents(string operationId, long sinceSequence) =>
        throw new NotSupportedException();

    public Result<CancelOperationData> Cancel(string operationId) =>
        Result.Ok(new CancelOperationData(operationId, OperationCancellationState.Requested));

    public Task<object> WaitAsync(string operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<T> ExecuteSynchronousAsync<T>(Func<Task<T>> action) => Task.Run(action);

    public void Dispose()
    {
    }
}

internal sealed class FixedVersion : IEtabsInstalledVersion
{
    public string Read() => "23.3.0";
}

/// <summary>Builds a real <see cref="ServeDispatcher"/> over fakes; only the services a test names are real.</summary>
internal static class DispatcherRig
{
    public static ServeDispatcher Create(
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        ShutdownIfUnchangedRegistry? c10 = null,
        ShutdownIfUnchangedService? service = null,
        EtabExtension.CLI.Features.SaveModel.ISaveModelService? save = null,
        IOperationManager? operations = null,
        AbandonedRequests? abandoned = null,
        OrphanDispositionCell? orphan = null,
        IProcessInspector? processes = null)
    {
        var declared = new ManagedEtabsStartIntentScope();
        _ = declared.Publish(ManagedEtabsStartIntent.VisibleByConsent);
        return new ServeDispatcher(
            session,
            null!,
            null!,
            null!,
            null!,
            null!,
            save!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            operations ?? new PassThroughOperations(),
            new CachedSessionStatus(),
            processes ?? new FakeCensus(),
            null!,
            new EtabsWorkEnvelope(declared, new EtabsWorkScope(), session),
            watchdog,
            new FixedVersion(),
            abandoned,
            orphan,
            service,
            c10);
    }
}

/// <summary>
/// Synchronous work runs one item at a time, like the single STA: an item behind a blocked one
/// never starts. It is what makes "get-status never queues behind a running command" provable.
/// </summary>
internal sealed class SerialOperations : IOperationManager
{
    private readonly SemaphoreSlim _sta = new(1, 1);

    public bool HasActiveOperation => false;

    public Result<StartOperationData> Start(string kind, JsonElement payload) => throw new NotSupportedException();

    public Result<OperationStatusData> GetStatus(string operationId) => throw new NotSupportedException();

    public Result<GetOperationEventsData> GetEvents(string operationId, long sinceSequence) =>
        throw new NotSupportedException();

    public Result<CancelOperationData> Cancel(string operationId) => throw new NotSupportedException();

    public Task<object> WaitAsync(string operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async Task<T> ExecuteSynchronousAsync<T>(Func<Task<T>> action)
    {
        await _sta.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _ = _sta.Release();
        }
    }

    public void Dispose() => _sta.Dispose();
}
