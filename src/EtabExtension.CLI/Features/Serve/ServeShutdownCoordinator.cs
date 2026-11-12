using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

public interface IServeShutdownCoordinator : IAsyncDisposable
{
    Task<Result<ManagedEtabsShutdownData>> ShutdownAsync();
}

public sealed class ServeShutdownCoordinator : IServeShutdownCoordinator
{
    /// <summary>
    /// The most a shutdown of a blocked session may take: the bounded exit attempt, the
    /// forced-exit wait, and a margin. Inside the Rust client's 30 s shutdown budget, so the
    /// daemon answers <c>shutdown</c> itself instead of being killed with ETABS left behind.
    /// </summary>
    public static readonly TimeSpan BlockedShutdownEnvelope =
        ManagedEtabsShutdownMachine.GracefulExitTimeout
        + ManagedEtabsShutdownMachine.ForcedExitTimeout
        + TimeSpan.FromSeconds(5);

    private readonly IStaExecutionWorker _worker;
    private readonly IEtabsSession _session;
    private readonly IEtabsModalDialogWatchdog _watchdog;
    private readonly TimeSpan _blockedEnvelope;
    private readonly object _gate = new();
    private Task<Result<ManagedEtabsShutdownData>>? _shutdown;

    public ServeShutdownCoordinator(
        IStaExecutionWorker worker,
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog)
        : this(worker, session, watchdog, BlockedShutdownEnvelope)
    {
    }

    internal ServeShutdownCoordinator(
        IStaExecutionWorker worker,
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        TimeSpan blockedEnvelope)
    {
        _worker = worker;
        _session = session;
        _watchdog = watchdog;
        _blockedEnvelope = blockedEnvelope;
    }

    public Task<Result<ManagedEtabsShutdownData>> ShutdownAsync()
    {
        lock (_gate)
        {
            return _shutdown ??= RunShutdownAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task<Result<ManagedEtabsShutdownData>> RunShutdownAsync()
    {
        try
        {
            // While a command is abandoned to an unanswered ETABS dialog, the STA worker is
            // stuck inside that CSI call: a shutdown queued behind it would never run, ETABS
            // would stay up (hidden, with the dialog), and the client would have to kill the
            // daemon. So the bounded shutdown runs on its own thread instead (P2-1).
            var terminal = _watchdog.IsBlocked
                ? await ShutdownBlockedSessionAsync().ConfigureAwait(false)
                : await _worker.ExecuteAsync(
                    () => Task.FromResult(_session.Shutdown())).ConfigureAwait(false);
            if (terminal.Success)
            {
                return Result.Ok(terminal.Data);
            }

            var errorCode = terminal.ErrorCode
                ?? ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed;
            var diagnostic = terminal.Error
                ?? "Managed ETABS shutdown returned failure without a diagnostic.";
            return new(
                false,
                terminal.Data,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    diagnostic,
                    $"code={errorCode}"));
        }
        catch (Exception exception)
        {
            var errorCode = ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed;
            var data = Unconfirmed();
            return new(
                false,
                data,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IEtabsSession.Shutdown",
                        exception),
                    $"code={errorCode}"));
        }
        finally
        {
            // Joins with a timeout: a worker still stuck behind the dialog is abandoned.
            _worker.Dispose();
        }
    }

    /// <summary>
    /// Runs <see cref="IEtabsSession.ShutdownWhileWorkerBlocked"/> on a fresh dedicated STA
    /// thread and waits at most <see cref="BlockedShutdownEnvelope"/>. The session bounds its
    /// own exit attempt; the envelope is the backstop against anything else in that path
    /// blocking, so the answer to <c>shutdown</c> is always bounded.
    /// </summary>
    private async Task<ManagedEtabsShutdownResult> ShutdownBlockedSessionAsync()
    {
        Console.Error.WriteLine(
            "⚠ Shutdown while a command is blocked on an ETABS dialog; " +
            "shutting the session down from a dedicated thread.");
        var completion = new TaskCompletionSource<ManagedEtabsShutdownResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(_session.ShutdownWhileWorkerBlocked());
            }
#pragma warning disable CA1031 // surfaced to the caller through the completion
            catch (Exception exception)
#pragma warning restore CA1031
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "etab-cli-serve-blocked-shutdown"
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        try
        {
            return await completion.Task.WaitAsync(_blockedEnvelope).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ManagedEtabsShutdownResult(
                false,
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                "Shutdown of the blocked ETABS session did not finish within " +
                $"{_blockedEnvelope.TotalSeconds:0.###}s; the exact owned process exit is unconfirmed.",
                Unconfirmed());
        }
    }

    private ManagedEtabsShutdownData Unconfirmed() => new(
        ManagedEtabsShutdownState.ProcessExitUnconfirmed,
        ProcessExitConfirmed: false,
        Forced: false,
        RecordRetained: true,
        ApplicationExitReturnCode: null,
        OwnedPid: SafeProcessId());

    /// <summary>
    /// The session's pid, without risking the hang this path exists to avoid: the read takes
    /// the session lock, which a stuck shutdown thread may still hold.
    /// </summary>
    private int? SafeProcessId()
    {
        try
        {
            var read = Task.Run(() => _session.ProcessId);
            return read.Wait(TimeSpan.FromMilliseconds(250)) ? read.Result : null;
        }
#pragma warning disable CA1031 // a pid for the diagnostic only; unknown is an honest answer
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
