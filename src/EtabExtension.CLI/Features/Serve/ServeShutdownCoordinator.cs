using System.Diagnostics;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

public interface IServeShutdownCoordinator : IAsyncDisposable
{
    Task<Result<ManagedEtabsShutdownData>> ShutdownAsync();

    /// <summary>
    /// Hands the instance to the user (card C1). Bounded; never exits, never kills.
    /// </summary>
    Task<Result<DetachData>> DetachAsync() => Task.FromResult(Result.Fail<DetachData>(
        $"[{ManagedEtabsShutdownErrorCodes.DetachFailed}] This coordinator does not support detach."));

    /// <summary>
    /// What stdin ending (no prior <c>shutdown</c>) does (card C2). The default is the exit.
    /// </summary>
    Task<Result<ManagedEtabsShutdownData>> EndOfInputAsync(ServeEofPolicy policy) => ShutdownAsync();
}

/// <summary>How a terminal session answer is written on the wire.</summary>
internal static class ServeShutdownWire
{
    public static Result<ManagedEtabsShutdownData> ToResult(ManagedEtabsShutdownResult terminal)
    {
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
}

public sealed class ServeShutdownCoordinator : IServeShutdownCoordinator
{
    /// <summary>Window-guard teardown joins its monitor for at most this long.</summary>
    public static readonly TimeSpan GuardTeardownBudget = EtabsSession.GuardTeardownBudget;

    /// <summary>Record operations and reference cleanup inside the blocked sequence.</summary>
    public static readonly TimeSpan BlockedRecordOperationsMargin = EtabsSession.BlockedRecordOperationsMargin;

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
    /// How long a shutdown waits for a BUSY STA to start the queued exit before it takes the
    /// blocked sequence (card C7a). It is the SAME 2 s as the blocked gate wait, not a second
    /// one on top of it: the coordinator measures the envelope from the start of the shutdown,
    /// and the session is told its gate wait is already spent.
    /// </summary>
    public static readonly TimeSpan StaBusyWait = EtabsSession.BlockedShutdownGateWait;

    /// <summary>
    /// How long a shutdown whose exit the STA STARTED waits for it to finish before it takes the
    /// bounded cleanup (review P2-6): the graceful interval the Rust 20 s envelope assumes.
    /// </summary>
    public static readonly TimeSpan StartedExitGrace = ManagedEtabsShutdownMachine.GracefulExitTimeout;

    /// <summary>
    /// The overall deadline of a shutdown whose exit started (review P2-6): the graceful and the
    /// forced interval of the ordinary exit, which is what Rust's 20 s envelope covers. After it
    /// the answer is "unconfirmed" and the host observes.
    /// </summary>
    public static readonly TimeSpan StartedExitDeadline =
        ManagedEtabsShutdownMachine.GracefulExitTimeout + ManagedEtabsShutdownMachine.ForcedExitTimeout;

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
    private readonly ShutdownIfUnchangedRegistry? _c10;
    private readonly SaveAndExitRegistry? _c11;
    private readonly TimeSpan _blockedEnvelope;
    private readonly TimeSpan _staBusyWait;
    private readonly TimeSpan _startedGrace;
    private readonly TimeSpan _startedDeadline;
    private readonly object _gate = new();
    private Task<Result<ManagedEtabsShutdownData>>? _shutdown;
    private Task<Result<DetachData>>? _detach;
    private bool _detachFinal;

    public ServeShutdownCoordinator(
        IStaExecutionWorker worker,
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        ShutdownIfUnchangedRegistry? c10 = null,
        SaveAndExitRegistry? c11 = null)
        : this(worker, session, watchdog, BlockedShutdownEnvelope, StaBusyWait, c10, c11: c11)
    {
    }

    internal ServeShutdownCoordinator(
        IStaExecutionWorker worker,
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        TimeSpan blockedEnvelope,
        TimeSpan? staBusyWait = null,
        ShutdownIfUnchangedRegistry? c10 = null,
        TimeSpan? startedGrace = null,
        TimeSpan? startedDeadline = null,
        SaveAndExitRegistry? c11 = null)
    {
        _c11 = c11;
        _startedGrace = startedGrace ?? StartedExitGrace;
        _startedDeadline = startedDeadline ?? StartedExitDeadline;
        _worker = worker;
        _session = session;
        _watchdog = watchdog;
        _blockedEnvelope = blockedEnvelope;
        _staBusyWait = staBusyWait ?? StaBusyWait;
        _c10 = c10;
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

    // ── detach (card C1) ────────────────────────────────────────────────────

    public Task<Result<DetachData>> DetachAsync()
    {
        lock (_gate)
        {
            // A detach in flight, or one that reached a final outcome (Detached, Uncertain),
            // is shared. A rolled-back detach (DETACH_FAILED) may be asked for again.
            if (_detach is not null && (!_detach.IsCompleted || _detachFinal))
            {
                return _detach;
            }

            return _detach = RunDetachAsync();
        }
    }

    private async Task<Result<DetachData>> RunDetachAsync()
    {
        // A pending C10 or C11 can no longer exit once a detach is asked for (card v2.4 §2,
        // v2.5a A4).
        _ = _c10?.AbandonCurrent();
        _ = _c11?.AbandonCurrent();

        DetachResult detached;
        try
        {
            // Never on the STA, and never on a pool thread for up to its whole budget.
            detached = await Task.Factory.StartNew(
                _session.Detach,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // an unexpected fault is reported, and is never a rollback claim
        catch (Exception exception)
#pragma warning restore CA1031
        {
            lock (_gate)
            {
                _detachFinal = true;
            }

            return Result.Fail<DetachData>(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                $"[{ManagedEtabsShutdownErrorCodes.DetachUncertain}] " +
                EtabsApiDiagnosticFormatter.InfrastructureException("IEtabsSession.Detach", exception),
                "nothing was exited or killed"));
        }

        lock (_gate)
        {
            _detachFinal = detached.Outcome != DetachOutcome.Failed;
        }

        return detached.Outcome switch
        {
            DetachOutcome.Detached => Result.Ok(new DetachData(
                true,
                detached.Identity is { } identity
                    ? new DetachedIdentity(identity.Pid, identity.ProcessStartTimeUtc, identity.ExecutablePath)
                    : null,
                detached.LaunchRecordId)),
            _ => Result.Fail<DetachData>(
                $"[{detached.ErrorCode ?? ManagedEtabsShutdownErrorCodes.DetachFailed}] " +
                EtabsApiDiagnosticFormatter.Bounded(detached.Error ?? "The detach did not complete."))
        };
    }

    // ── end of input (card C2) ──────────────────────────────────────────────

    public Task<Result<ManagedEtabsShutdownData>> EndOfInputAsync(ServeEofPolicy policy)
    {
        lock (_gate)
        {
            return _shutdown ??= RunEndOfInputAsync(policy);
        }
    }

    private async Task<Result<ManagedEtabsShutdownData>> RunEndOfInputAsync(ServeEofPolicy policy)
    {
        // Whatever EOF decides (a detach, a suppression after a failed detach, or the exit), a
        // save-and-exit still Saving can no longer exit on its own account (card v2.5a A4).
        _ = _c11?.AbandonCurrent();

        // A detach the host already asked for decides EOF too: nothing destructive follows a
        // detached or uncertain instance.
        Task<Result<DetachData>>? prior;
        bool priorFinal;
        lock (_gate)
        {
            prior = _detach;
            priorFinal = _detachFinal;
        }

        if (prior is not null)
        {
            var result = await prior.ConfigureAwait(false);
            lock (_gate)
            {
                priorFinal = _detachFinal;
            }

            if (priorFinal)
            {
                return DetachAnswer(result);
            }
        }

        if (policy == ServeEofPolicy.DetachVisible && _session.IsUserVisible)
        {
            // A failed detach ends the daemon with the userVisible record intact and exits
            // NOTHING: the next recovery detaches it (card C2, C3). The session is told, so the
            // disposal of the DI scope that follows cannot exit what this path left running
            // (review P1-1).
            var detached = await DetachAsync().ConfigureAwait(false);
            if (!detached.Success)
            {
                _session.SuppressFurtherExit();
            }

            return DetachAnswer(detached);
        }

        return await RunShutdownAsync().ConfigureAwait(false);
    }

    private Result<ManagedEtabsShutdownData> DetachAnswer(Result<DetachData> detach)
    {
        var pid = detach.Data?.Identity?.Pid;
        if (detach.Success)
        {
            return Result.Ok(new ManagedEtabsShutdownData(
                ManagedEtabsShutdownState.Succeeded,
                ProcessExitConfirmed: false,
                Forced: false,
                RecordRetained: false,
                ApplicationExitReturnCode: null,
                OwnedPid: pid)
            {
                Disposition = EtabsDisposition.Detached
            });
        }

        var uncertain = detach.Error?.Contains(
            ManagedEtabsShutdownErrorCodes.DetachUncertain,
            StringComparison.Ordinal) == true;
        return new(
            false,
            new ManagedEtabsShutdownData(
                ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                ProcessExitConfirmed: false,
                Forced: false,
                RecordRetained: true,
                ApplicationExitReturnCode: null,
                OwnedPid: _session.ProcessId)
            {
                Disposition = uncertain ? EtabsDisposition.Detaching : EtabsDisposition.Owned
            },
            detach.Error);
    }

    // ── exit (card C7a) ─────────────────────────────────────────────────────

    private async Task<Result<ManagedEtabsShutdownData>> RunShutdownAsync()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            // An explicit shutdown (or EOF) ends the process anyway, so a pending
            // shutdown-if-unchanged can no longer exit on its own account (card v2.4 §2).
            _ = _c10?.AbandonCurrent();
            _ = _c11?.AbandonCurrent();

            // While a command is abandoned to an unanswered ETABS dialog, the STA worker is
            // stuck inside that CSI call: a shutdown queued behind it would never run, ETABS
            // would stay up (hidden, with the dialog), and the client would have to kill the
            // daemon. So the bounded shutdown runs on its own thread instead (P2-1).
            var terminal = _watchdog.IsBlocked
                ? await ShutdownBlockedSessionAsync(null, clock).ConfigureAwait(false)
                : await ShutdownOrTakeTheBlockedSequenceAsync(clock).ConfigureAwait(false);
            return ServeShutdownWire.ToResult(terminal);
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
    /// Queues the ordinary exit on the STA and waits at most <see cref="StaBusyWait"/> for it to
    /// START (card C7a). A worker that started it owns a bounded, graceful exit that must not be
    /// cut short: a healthy ETABS may take several seconds to close. A worker that never starts
    /// it is busy (a long command, a launch, a dialog) and the blocked sequence takes over -
    /// never the deferred drain, never an unbounded queue.
    /// </summary>
    private async Task<ManagedEtabsShutdownResult> ShutdownOrTakeTheBlockedSequenceAsync(Stopwatch clock)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _worker.ExecuteAsync(() =>
        {
            started.TrySetResult();
            return Task.FromResult(_session.Shutdown());
        });
        using var giveUp = new CancellationTokenSource();
        var first = await Task.WhenAny(
            started.Task,
            queued,
            Task.Delay(_staBusyWait, giveUp.Token)).ConfigureAwait(false);
        if (first == queued)
        {
            await giveUp.CancelAsync().ConfigureAwait(false);
            return await queued.ConfigureAwait(false);
        }

        if (first == started.Task)
        {
            // The exit STARTED. It keeps its graceful interval, but it stays supervised (review
            // P2-6): ExitWithoutSaving is a synchronous COM call that a hung exit or a modal
            // that appears now would never return from. Past the grace, the bounded cleanup
            // takes over, and the whole shutdown is answered by the started-exit deadline.
            await giveUp.CancelAsync().ConfigureAwait(false);
            if (await Task.WhenAny(queued, Task.Delay(_startedGrace)).ConfigureAwait(false) == queued)
            {
                return await queued.ConfigureAwait(false);
            }

            _ = queued.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            Console.Error.WriteLine(
                "⚠ The ordinary ETABS exit did not return within its grace; taking the bounded cleanup.");
            return await ShutdownBlockedSessionAsync(TimeSpan.Zero, clock, _startedDeadline).ConfigureAwait(false);
        }

        // The STA is busy. The queued exit is left in place: if it ever runs, the session
        // answers with the terminal state the blocked sequence already reached.
        _ = queued.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        Console.Error.WriteLine(
            "⚠ Shutdown while the ETABS worker is busy; taking the blocked sequence from a dedicated thread.");
        return await ShutdownBlockedSessionAsync(TimeSpan.Zero, clock).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <see cref="IEtabsSession.ShutdownWhileWorkerBlocked()"/> on a fresh dedicated STA
    /// thread and waits at most what is left of <see cref="BlockedShutdownEnvelope"/>, counted
    /// from the START of the shutdown. The session bounds its own exit attempt; the envelope is
    /// the backstop against anything else in that path blocking, so the answer to
    /// <c>shutdown</c> is always bounded.
    /// </summary>
    private async Task<ManagedEtabsShutdownResult> ShutdownBlockedSessionAsync(
        TimeSpan? gateWaitSpent,
        Stopwatch clock,
        TimeSpan? limit = null)
    {
        var overall = limit ?? _blockedEnvelope;
        Console.Error.WriteLine(
            "⚠ Shutdown while a command is blocked on an ETABS dialog; " +
            "shutting the session down from a dedicated thread.");
        var completion = new TaskCompletionSource<ManagedEtabsShutdownResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(gateWaitSpent is { } gateWait
                    ? _session.ShutdownWhileWorkerBlocked(gateWait)
                    : _session.ShutdownWhileWorkerBlocked());
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
        var remaining = overall - clock.Elapsed;
        try
        {
            return await completion.Task
                .WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ManagedEtabsShutdownResult(
                false,
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                "Shutdown of the blocked ETABS session did not finish within " +
                $"{overall.TotalSeconds:0.###}s; the exact owned process exit is unconfirmed.",
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
