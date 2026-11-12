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
    /// <summary>Window-guard teardown joins its monitor for at most this long.</summary>
    public static readonly TimeSpan GuardTeardownBudget = TimeSpan.FromSeconds(2);

    /// <summary>Record operations and reference cleanup inside the blocked sequence.</summary>
    public static readonly TimeSpan BlockedRecordOperationsMargin = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The backstop on the session's own bounded blocked sequence: the gate wait, the
    /// bounded graceful exit, the forced-exit wait, the guard teardown and record
    /// operations (card I5, A7).
    /// </summary>
    public static readonly TimeSpan BlockedShutdownEnvelope =
        EtabsSession.BlockedShutdownGateWait
        + ManagedEtabsShutdownMachine.BlockedGracefulExitTimeout
        + ManagedEtabsShutdownMachine.ForcedExitTimeout
        + GuardTeardownBudget
        + BlockedRecordOperationsMargin;

    /// <summary>
    /// The end-to-end worst case of a blocked shutdown RESPONSE: the envelope plus the
    /// bounded join of the stuck STA worker, which runs inside the same budget and never on
    /// top of it. Rust's 30 s response budget is this plus an 8 s margin (card V5).
    /// </summary>
    public static readonly TimeSpan BlockedShutdownWorstCase =
        BlockedShutdownEnvelope + StaExecutionWorker.DefaultDisposeJoinTimeout;

    /// <summary>
    /// The contractual ceiling the blocked-shutdown response is held to (card I5, V5). The
    /// configured worst case above stays under it with scheduling slack, and Rust's 30 s
    /// response budget is this plus an 8 s margin.
    /// </summary>
    public static readonly TimeSpan BlockedShutdownCeiling = TimeSpan.FromSeconds(22);

    /// <summary>
    /// The shared contract constants line (<c>docs/contracts/sidecar-v1/shutdown-timing.txt</c>
    /// in the Rust repository). Rust parses the worst case from it and asserts its response
    /// budget exceeds it by the margin; a C# test pins this string to the literal there.
    /// </summary>
    public static string ContractConstantsLine =>
        $"etab-cli-blocked-shutdown gate-wait-ms={Ms(EtabsSession.BlockedShutdownGateWait)} " +
        $"graceful-ms={Ms(ManagedEtabsShutdownMachine.BlockedGracefulExitTimeout)} " +
        $"forced-ms={Ms(ManagedEtabsShutdownMachine.ForcedExitTimeout)} " +
        $"guard-teardown-ms={Ms(GuardTeardownBudget)} " +
        $"record-ops-ms={Ms(BlockedRecordOperationsMargin)} " +
        $"worker-join-ms={Ms(StaExecutionWorker.DefaultDisposeJoinTimeout)} " +
        $"worst-case-ms={Ms(BlockedShutdownWorstCase)} " +
        $"ceiling-ms={Ms(BlockedShutdownCeiling)}";

    private static long Ms(TimeSpan value) => (long)value.TotalMilliseconds;

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
    /// The session's pid. The read is the lock-free publication (card I3, I5): it cannot
    /// block on a gate a stuck command holds, so no pool thread is spent waiting on it.
    /// </summary>
    private int? SafeProcessId()
    {
        try
        {
            return _session.ProcessId;
        }
#pragma warning disable CA1031 // a pid for the diagnostic only; unknown is an honest answer
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
