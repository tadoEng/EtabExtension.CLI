using System.Diagnostics;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// The one-way lifecycle of a single managed launch attempt (card v3.1 W1). Every change is
/// an <c>Interlocked.CompareExchange</c> in <see cref="EtabsLaunchState"/>, so commit and
/// shutdown can never both win: exactly one of them owns the next step.
/// </summary>
public enum LaunchLifecycle
{
    Launching = 0,
    Committing = 1,
    Ready = 2,
    Terminating = 3,
    Ended = 4,

    /// <summary>
    /// A detach won <c>Ready → Detaching</c> (card C1): the single arbiter against
    /// <c>Ready → Terminating</c>. It can still roll back to Ready until its durable write has
    /// started.
    /// </summary>
    Detaching = 5,

    /// <summary>The durable detach write committed and the daemon no longer owns the instance.</summary>
    Detached = 6,

    /// <summary>
    /// The detach's durable write started and its outcome is unknown (card v2.3 §3): no
    /// rollback, and nothing destructive ever follows.
    /// </summary>
    DetachUncertain = 7,

    /// <summary>
    /// The EOF path decided to leave this instance running (review round 3, P1). It is a lifecycle
    /// state, so <c>Ready -> Terminating</c> and <c>Ready -> Suppressed</c> compete on the SAME
    /// compare-exchange: exactly one of "end it" and "never end it" can win, and there is no
    /// separate flag to read before acting.
    /// </summary>
    Suppressed = 8
}

/// <summary>
/// What every observer of the launching process may know without taking any lock: the
/// identity (once <c>Process.Start</c> produced it) and the authoritative handle lease.
/// Readiness is not stored here - it is the lifecycle reaching
/// <see cref="LaunchLifecycle.Ready"/>, so it can never disagree with the state machine.
/// </summary>
public sealed record LaunchPublication(ManagedProcessIdentity? Identity, HandleLease? Lease)
{
    public static readonly LaunchPublication None = new(null, null);
}

/// <summary>
/// The lock-free cell the session writes and the inspector, the watchdog and the shutdown
/// path read (card I3). A volatile reference to one immutable publication.
/// </summary>
public sealed class LaunchPublicationCell
{
    private LaunchPublication _current = LaunchPublication.None;

    public LaunchPublication Current => Volatile.Read(ref _current);

    public ManagedProcessIdentity? Identity => Current.Identity;

    private nint _mainWindow;
    private int _blockerArmed;

    internal void Set(LaunchPublication publication) => Volatile.Write(ref _current, publication);

    internal void Clear()
    {
        Volatile.Write(ref _current, LaunchPublication.None);
        Volatile.Write(ref _mainWindow, nint.Zero);
        Volatile.Write(ref _blockerArmed, 0);
    }

    /// <summary>The main window of the launching or running ETABS; zero until it exists.</summary>
    public nint MainWindow => Volatile.Read(ref _mainWindow);

    internal void SetMainWindow(nint window) => Volatile.Write(ref _mainWindow, window);

    /// <summary>
    /// True from Ready until the first watched command completes cleanly (card P2-3): the
    /// startup-blocker rule stays armed because the ETABS licence sign-in window was seen
    /// live about 13 s AFTER the attach, when nothing else was watching.
    /// </summary>
    public bool StartupBlockerArmed => Volatile.Read(ref _blockerArmed) == 1;

    internal void ArmStartupBlocker() => Volatile.Write(ref _blockerArmed, 1);

    internal void DisarmStartupBlocker() => Volatile.Write(ref _blockerArmed, 0);
}

/// <summary>
/// One detach attempt's claim on its own commit point (card v2.3 §3). The write is claimed
/// <c>0 → started</c> by the worker, or a rollback is claimed <c>0 → rolled back</c> by the
/// supervisor (or the worker's own pre-write failure): whichever wins decides, so a rollback
/// can never be followed by a write, and a write that started can never be rolled back. A
/// fresh attempt per detach keeps a worker that wakes late from touching a LATER attempt.
/// </summary>
public sealed class DetachAttempt
{
    private int _claim;

    /// <summary>The worker's claim on the durable write. False once a rollback was chosen.</summary>
    public bool TryStartWrite() => Interlocked.CompareExchange(ref _claim, 1, 0) == 0;

    /// <summary>The claim on a rollback. False once the write has started.</summary>
    public bool TryChooseRollback() => Interlocked.CompareExchange(ref _claim, 2, 0) == 0;

    /// <summary>True once the durable write was claimed.</summary>
    public bool WriteStarted => Volatile.Read(ref _claim) == 1;

    private int _committed;

    /// <summary>The durable write returned: the commit point has passed (card v2.3 §3).</summary>
    public void MarkCommitted() => Volatile.Write(ref _committed, 1);

    /// <summary>True once the durable write returned. After it, a timeout is Detached, never Uncertain.</summary>
    public bool Committed => Volatile.Read(ref _committed) == 1;
}

/// <summary>The first visible non-main window that was blocking a startup.</summary>
public sealed record StartupBlockerEvidence(string Title, string ClassName);

/// <summary>The cleanup-relevant surface of an authoritative process handle.</summary>
public interface IProcessHandle : IDisposable
{
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(TimeSpan timeout);
}

/// <summary>
/// A ref-counted lease over the authoritative process handle (card V4). The fallback kill
/// and the normal shutdown both take a lease and the handle is disposed only when the last
/// reference goes, so neither can ever use a disposed handle.
/// </summary>
public sealed class HandleLease
{
    private readonly IProcessHandle _process;
    private int _refs = 1;
    private int _ownerReleased;
    private int _exitConfirmed;
    private int _handedOff;

    public HandleLease(IProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
    }

    /// <summary>True once every reference was released and the handle was disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _refs) <= 0;

    /// <summary>Takes a reference. False when the handle is already gone.</summary>
    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refs);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _refs, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Drops a reference taken by <see cref="TryAcquire"/>.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            _process.Dispose();
        }
    }

    /// <summary>
    /// Drops the one owner reference. Idempotent: the slot's taker, the managed application
    /// and a failing worker may all believe they own the cleanup, and only the first counts.
    /// </summary>
    public void ReleaseOwner()
    {
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
        {
            Release();
        }
    }

    /// <summary>
    /// True only after an exit was actually OBSERVED through this lease (<c>HasExited</c> or
    /// <c>WaitForExit</c> returned true). A disposed handle is not evidence: record clearing
    /// and the retained-record rule depend on this flag, never on a convention.
    /// </summary>
    public bool ExitConfirmed => Volatile.Read(ref _exitConfirmed) == 1;

    /// <summary>
    /// True once the instance was handed to the user (card C1, step 4). A handed-off lease is
    /// released, never "exit confirmed": the process is alive and no longer ours.
    /// </summary>
    public bool HandedOff => Volatile.Read(ref _handedOff) == 1;

    /// <summary>Marks the lease handed off. Never sets <see cref="ExitConfirmed"/>.</summary>
    public void MarkHandedOff() => Volatile.Write(ref _handedOff, 1);

    /// <summary>Whether the process is known to have exited; a gone handle answers the flag.</summary>
    public bool HasExited
    {
        get
        {
            if (!TryAcquire())
            {
                return ExitConfirmed;
            }

            try
            {
                var exited = _process.HasExited;
                if (exited)
                {
                    Volatile.Write(ref _exitConfirmed, 1);
                }

                return exited;
            }
            finally
            {
                Release();
            }
        }
    }

    public void Kill()
    {
        if (!TryAcquire())
        {
            return;
        }

        try
        {
            _process.Kill();
        }
        finally
        {
            Release();
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (!TryAcquire())
        {
            return ExitConfirmed;
        }

        try
        {
            var exited = _process.WaitForExit(timeout);
            if (exited)
            {
                Volatile.Write(ref _exitConfirmed, 1);
            }

            return exited;
        }
        finally
        {
            Release();
        }
    }
}

/// <summary>
/// The owned-process view the window guard needs, over a lease. Disposing it releases
/// nothing: the guard only reads, and the owner reference belongs to the application.
/// </summary>
internal sealed class LeasedOwnedProcess(HandleLease lease, ManagedProcessIdentity identity)
    : IOwnedEtabsProcess
{
    public ManagedProcessIdentity Identity { get; } = identity;

    public bool HasExited => lease.HasExited;

    public void Kill() => lease.Kill();

    public bool WaitForExit(TimeSpan timeout) => lease.WaitForExit(timeout);

    public void Dispose()
    {
    }
}

/// <summary>
/// Everything one launch attempt shares between the launching thread, the startup
/// supervisor and any shutdown path (card W1, W2, X1, X2, Y1, Y2).
///
/// <para><b>Rules.</b> The lifecycle changes only by compare-exchange. The handle slot is
/// Empty, a lease, or CLOSED, and every handle has exactly one cleanup owner: whoever closes
/// a slot that holds a lease kills it; a worker whose store finds the slot CLOSED kills its
/// own handle. A record is cleared only after the exit of the process it describes was
/// confirmed, and only when it is still this attempt's record.</para>
/// </summary>
public sealed class EtabsLaunchState
{
    /// <summary>How long a shutdown waits for a short commit to finish (card W1/X1).</summary>
    public static readonly TimeSpan CommitWait = TimeSpan.FromSeconds(2);

    private static readonly object Closed = new();

    private readonly ISessionRecordStore _records;
    private readonly LaunchPublicationCell _cell;
    private readonly TaskCompletionSource<ManagedEtabsShutdownResult> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lifecycle;
    private int _launchPhaseTermination;
    private int _timedOut;
    private int _began;
    private int _workerStarted;
    private readonly TaskCompletionSource<WorkerOutcome> _worker =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StartupBlockerEvidence? _blockerEvidence;
    private object? _slot;

    public EtabsLaunchState(ISessionRecordStore records, LaunchPublicationCell cell)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(cell);
        _records = records;
        _cell = cell;
    }

    /// <summary>The generation of every record this attempt writes (card A4).</summary>
    public Guid LaunchRecordId { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// A launcher that is not ours (a test double) builds the application with its own
    /// record id; the session adopts it so every record of the attempt carries one id. The
    /// real launcher builds the application with this state's id, so this is a no-op there.
    /// </summary>
    public void AdoptRecordId(Guid id) => LaunchRecordId = id;

    public LaunchLifecycle Lifecycle => (LaunchLifecycle)Volatile.Read(ref _lifecycle);

    public bool IsReady => Lifecycle == LaunchLifecycle.Ready;

    public LaunchPublication Publication => _cell.Current;

    /// <summary>True when the startup supervisor (not a shutdown) ended this attempt.</summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) == 1;

    public bool TryTransition(LaunchLifecycle from, LaunchLifecycle to) =>
        Interlocked.CompareExchange(ref _lifecycle, (int)to, (int)from) == (int)from;

    // ── detach arbitration (card C1, v2.3 §3) ───────────────────────────────

    private ManagedProcessIdentity? _detachedIdentity;

    /// <summary>The suppression arbiter: <c>Ready -> Suppressed</c>, the rival of Terminating.</summary>
    public bool TrySuppress() => TryTransition(LaunchLifecycle.Ready, LaunchLifecycle.Suppressed);

    /// <summary>The detach arbiter: <c>Ready → Detaching</c>, the single rival of Terminating.</summary>
    public bool TryBeginDetach() => TryTransition(LaunchLifecycle.Ready, LaunchLifecycle.Detaching);

    /// <summary>True in Detaching, Detached or DetachUncertain: nothing destructive may follow.</summary>
    public bool IsDetachDisposition =>
        Lifecycle is LaunchLifecycle.Detaching or LaunchLifecycle.Detached or LaunchLifecycle.DetachUncertain;

    /// <summary>The identity handed to the user, kept after the publication is cleared.</summary>
    public ManagedProcessIdentity? DetachedIdentity
    {
        get => Volatile.Read(ref _detachedIdentity);
        set => Volatile.Write(ref _detachedIdentity, value);
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for a Detaching launch to settle (card C1: a
    /// concurrent shutdown or EOF waits at most 5 s for the outcome). Returns the lifecycle
    /// it settled on, or Detaching when it did not.
    /// </summary>
    public LaunchLifecycle WaitForDetachOutcome(TimeSpan wait)
    {
        var deadline = Stopwatch.StartNew();
        while (Lifecycle == LaunchLifecycle.Detaching && deadline.Elapsed < wait)
        {
            Thread.Sleep(5);
        }

        return Lifecycle;
    }

    /// <summary>The atomic terminal fence for the Ready state (card W1).</summary>
    public bool TryBeginReadyTermination() =>
        TryTransition(LaunchLifecycle.Ready, LaunchLifecycle.Terminating);

    /// <summary>Marks an attempt whose exit is confirmed as finished.</summary>
    public void MarkEnded() => Volatile.Write(ref _lifecycle, (int)LaunchLifecycle.Ended);

    // ── handle slot (card Y2) ──────────────────────────────────────────────

    /// <summary>Worker side: false means the slot is CLOSED and the caller owns the cleanup.</summary>
    public bool TryStoreHandle(HandleLease lease) =>
        Interlocked.CompareExchange(ref _slot, lease, null) is null;

    /// <summary>Taker side: closes the slot and returns the lease it held, if any.</summary>
    public HandleLease? CloseSlot()
    {
        var lease = Interlocked.Exchange(ref _slot, Closed) as HandleLease;
        SlotClosedHook?.Invoke();
        return lease;
    }

    /// <summary>The lease currently held by the slot, without closing it.</summary>
    public HandleLease? SlotLease => Volatile.Read(ref _slot) as HandleLease;

    // ── publication ─────────────────────────────────────────────────────────

    public void Publish(ManagedProcessIdentity identity, HandleLease? lease) =>
        _cell.Set(new LaunchPublication(identity, lease));

    /// <summary>Publishes the identity when the launcher did not (a test double).</summary>
    public void PublishIfAbsent(ManagedProcessIdentity identity)
    {
        if (_cell.Identity is null)
        {
            _cell.Set(new LaunchPublication(identity, null));
        }
    }

    /// <summary>What the startup worker did with a process it created after the slot was CLOSED.</summary>
    public enum WorkerOutcome
    {
        NoProcess,
        KilledConfirmed,
        KillUnconfirmed
    }

    /// <summary>The launcher started a startup worker; a terminator that finds the slot empty awaits it.</summary>
    public void MarkWorkerStarted() => Volatile.Write(ref _workerStarted, 1);

    /// <summary>The worker's report once the slot was CLOSED (or nothing started) - card P2-1.</summary>
    public void ReportWorkerOutcome(WorkerOutcome outcome) => _ = _worker.TrySetResult(outcome);

    /// <summary>Hook for tests: runs right after <see cref="CloseSlot"/> closed the slot.</summary>
    internal Action? SlotClosedHook { get; set; }

    /// <summary>The blocking window seen before the main window existed, captured before any kill.</summary>
    public StartupBlockerEvidence? BlockerEvidence => Volatile.Read(ref _blockerEvidence);

    public void RecordBlockerEvidence(StartupBlockerEvidence evidence) =>
        Volatile.Write(ref _blockerEvidence, evidence);

    public void SetMainWindow(nint window) => _cell.SetMainWindow(window);

    public nint MainWindow => _cell.MainWindow;

    /// <summary>Arms the post-Ready startup-blocker watch (card P2-3).</summary>
    public void ArmStartupBlocker() => _cell.ArmStartupBlocker();

    /// <summary>Disarms the watch; the explicit reveal does it (review P2-A).</summary>
    public void DisarmStartupBlocker() => _cell.DisarmStartupBlocker();

    /// <summary>True once the session started the launch this state describes.</summary>
    public bool Began => Volatile.Read(ref _began) == 1;

    public void MarkBegun() => Volatile.Write(ref _began, 1);

    /// <summary>Records the answer of a terminator that acted through the handle alone.</summary>
    public void RecordTerminationResult(ManagedEtabsShutdownResult result) =>
        _ = _termination.TrySetResult(result);

    /// <summary>The answer of the terminator that won a launch-phase termination, once given.</summary>
    public ManagedEtabsShutdownResult? TerminationResult =>
        _termination.Task.IsCompletedSuccessfully ? _termination.Task.Result : null;

    public void ClearPublication() => _cell.Clear();

    // ── records ─────────────────────────────────────────────────────────────

    public void WriteProvisionalRecord(ManagedProcessIdentity identity) =>
        _records.Write(ToRecord(identity, ManagedEtabsRecordPhase.Provisional));

    public void WriteFinalRecord(ManagedProcessIdentity identity) =>
        _records.Write(ToRecord(identity, ManagedEtabsRecordPhase.Ready));

    /// <summary>
    /// The detach commit point (card v2.3 §3): the durable write of <c>phase: detached</c>.
    /// Carries the existing <c>userVisible</c> flag so the evidence stays truthful.
    /// </summary>
    public void WriteDetachedRecord(ManagedProcessIdentity identity, bool? userVisible)
    {
        BeforeDetachRecordGate?.Invoke();
        lock (_recordGate)
        {
            _records.Write(ToRecord(identity, ManagedEtabsRecordPhase.Detached) with { UserVisible = userVisible });
        }
    }

    // Serialises every read-modify-write of the instance record with the detach's write and its
    // clear (review P1): a writer that read Ready can never restore phase:ready after the
    // detach committed and cleared. Writers that are not the detach wait a bounded time.
    private readonly object _recordGate = new();

    internal static readonly TimeSpan RecordGateWait = TimeSpan.FromSeconds(2);

    /// <summary>Test seam: runs on the thread that writes the detach record, just before it takes the record gate.</summary>
    internal Action? BeforeDetachRecordGate { get; set; }

    /// <summary>Test seam: runs inside the record gate right after the document writer's read.</summary>
    internal Action? AfterDocumentRead { get; set; }

    private void EnterRecordGate()
    {
        if (!Monitor.TryEnter(_recordGate, RecordGateWait))
        {
            throw new IOException("The instance record is busy (a detach is writing it); nothing was written.");
        }
    }

    /// <summary>
    /// Persists <c>userVisible</c> durably (card C3), BEFORE the reveal is issued. Returns false
    /// when the record on disk is not this attempt's, which means nothing may be revealed.
    /// </summary>
    public bool WriteUserVisibleRecord(ManagedProcessIdentity identity)
    {
        EnterRecordGate();
        try
        {
            var existing = _records.Read();
            if (existing is null
                || existing.ManagedLaunchRecordId != LaunchRecordId
                || existing.Phase == ManagedEtabsRecordPhase.Detached)
            {
                return false;
            }

            _records.Write(ToRecord(identity, existing.Phase ?? ManagedEtabsRecordPhase.Ready) with { UserVisible = true });
            return true;
        }
        finally
        {
            Monitor.Exit(_recordGate);
        }
    }

    // The model this attempt's record names (card v2.5a A1). Kept in memory as well, so every
    // later record write (detach, userVisible) carries it forward instead of dropping it.
    private EtabsDocumentRecord? _document;

    /// <summary>
    /// Persists the opened model's identity durably (card v2.5a A1). Returns false when the
    /// record on disk is not this attempt's. A write that throws leaves the in-memory copy as
    /// it was, so the record and memory never disagree about what was persisted.
    /// </summary>
    public bool WriteDocumentRecord(ManagedProcessIdentity identity, EtabsDocumentRecord document)
    {
        EnterRecordGate();
        try
        {
            var existing = _records.Read();
            if (existing is null
                || existing.ManagedLaunchRecordId != LaunchRecordId
                || existing.Phase == ManagedEtabsRecordPhase.Detached)
            {
                return false;
            }

            AfterDocumentRead?.Invoke();
            var previous = Volatile.Read(ref _document);
            Volatile.Write(ref _document, document);
            try
            {
                _records.Write(ToRecord(identity, existing.Phase ?? ManagedEtabsRecordPhase.Ready)
                    with { UserVisible = existing.UserVisible });
            }
            catch
            {
                Volatile.Write(ref _document, previous);
                throw;
            }

            return true;
        }
        finally
        {
            Monitor.Exit(_recordGate);
        }
    }

    /// <summary>The <c>document</c> of this attempt's record as persisted, or null when absent.</summary>
    public EtabsDocumentRecord? ReadDocument()
    {
        var existing = _records.Read();
        return existing is not null && existing.ManagedLaunchRecordId == LaunchRecordId
            ? existing.Document
            : null;
    }

    /// <summary>The <c>userVisible</c> flag of this attempt's record, or null when absent.</summary>
    public bool? ReadUserVisible()
    {
        var existing = _records.Read();
        return existing is not null && existing.ManagedLaunchRecordId == LaunchRecordId
            ? existing.UserVisible
            : null;
    }

    /// <summary>
    /// Whether the record on disk is this attempt's generation. The detach checks it BEFORE
    /// its write starts, so a record that is gone or replaced rolls the detach back.
    /// </summary>
    public bool OwnsTheRecord()
    {
        var existing = _records.Read();
        return existing is not null && existing.ManagedLaunchRecordId == LaunchRecordId;
    }

    /// <summary>
    /// Clears this attempt's record after a committed detach (generation-matched). The process
    /// is alive, so unlike <see cref="ClearRecordAfterConfirmedExit(Func{bool})"/> no exit is
    /// required: the detached record has done its job and the instance is the user's.
    /// </summary>
    public void ClearRecordAfterHandoff()
    {
        lock (_recordGate)
        {
            var record = _records.Read();
            if (record is not null && record.ManagedLaunchRecordId == LaunchRecordId)
            {
                _records.Clear();
            }
        }
    }

    /// <summary>
    /// Writes the provisional record only when the attempt's record is not already there, so
    /// a launcher that wrote it at process start is not asked twice.
    /// </summary>
    public void EnsureProvisionalRecord(ManagedProcessIdentity identity)
    {
        var existing = _records.Read();
        if (existing is not null && existing.ManagedLaunchRecordId == LaunchRecordId)
        {
            return;
        }

        WriteProvisionalRecord(identity);
    }

    /// <summary>
    /// Clears this attempt's record, generation-matched, and only when the exit of the
    /// process behind <paramref name="lease"/> is confirmed (card Y1). Returns whether a
    /// record is still retained.
    /// </summary>
    public bool ClearRecordAfterConfirmedExit(HandleLease? lease) =>
        ClearRecordAfterConfirmedExit(() => lease is not null && lease.ExitConfirmed);

    /// <inheritdoc cref="ClearRecordAfterConfirmedExit(HandleLease?)" />
    public bool ClearRecordAfterConfirmedExit(Func<bool> exitConfirmed)
    {
        ManagedEtabsSessionRecord? record;
        try
        {
            record = _records.Read();
        }
#pragma warning disable CA1031 // an unreadable record is retained, never assumed clear
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }

        if (record is null)
        {
            return false;
        }

        if (record.ManagedLaunchRecordId != LaunchRecordId)
        {
            // Another generation's evidence: not ours to clear, and still retained.
            return true;
        }

        if (!exitConfirmed())
        {
            return true;
        }

        _records.Clear();
        return false;
    }

    private ManagedEtabsSessionRecord ToRecord(
        ManagedProcessIdentity identity,
        ManagedEtabsRecordPhase phase) => new(
        1,
        identity.Pid,
        identity.ProcessStartTimeUtc,
        Path.GetFullPath(identity.ExecutablePath),
        LaunchRecordId,
        DateTimeOffset.UtcNow)
    {
        Phase = phase,
        Document = Volatile.Read(ref _document)
    };

    // ── launch-phase termination (card W1, X1, X2, Y1, Y2, Y3) ─────────────

    /// <summary>
    /// Ends a launch that is not Ready: wins <c>Terminating</c> from Launching or
    /// Committing, closes the slot, and - when the slot held a lease - kills that handle and
    /// clears the record after the confirmed exit. A caller that loses to another
    /// terminator waits for that terminator's answer. Returns null when the attempt is
    /// Ready (the normal shutdown owns it) or was terminated from Ready.
    /// </summary>
    public ManagedEtabsShutdownResult? TerminateLaunch(
        TimeSpan killWait,
        TimeSpan gracefulWait = default,
        bool timedOut = false)
    {
        while (true)
        {
            var state = Lifecycle;
            if (state == LaunchLifecycle.Ready || state == LaunchLifecycle.Suppressed || IsDetachDisposition)
            {
                // Ready belongs to the normal shutdown; a detach disposition is never
                // terminated at all (card C1).
                return null;
            }

            if (state is LaunchLifecycle.Terminating or LaunchLifecycle.Ended)
            {
                return AwaitLaunchTermination(killWait);
            }

            if (TryTransition(state, LaunchLifecycle.Terminating))
            {
                if (timedOut)
                {
                    Volatile.Write(ref _timedOut, 1);
                }

                Volatile.Write(ref _launchPhaseTermination, 1);
                break;
            }
        }

        ManagedEtabsShutdownResult result;
        try
        {
            result = KillSlotLease(killWait, gracefulWait);
        }
#pragma warning disable CA1031 // the answer must be produced whatever happened inside the kill
        catch (Exception exception)
#pragma warning restore CA1031
        {
            result = Failed(
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "EtabsLaunchState.TerminateLaunch",
                    exception),
                Publication.Identity?.Pid,
                forced: true,
                recordRetained: true);
        }

        _termination.TrySetResult(result);
        return result;
    }

    /// <summary>
    /// Shutdown from Committing: wait the bounded commit window for Ready, then take
    /// Terminating if the commit is stalled (card X1).
    /// </summary>
    public bool WaitForCommitOrRelease(TimeSpan wait)
    {
        var deadline = Stopwatch.StartNew();
        while (Lifecycle == LaunchLifecycle.Committing && deadline.Elapsed < wait)
        {
            Thread.Sleep(5);
        }

        return Lifecycle == LaunchLifecycle.Ready;
    }

    private ManagedEtabsShutdownResult? AwaitLaunchTermination(TimeSpan killWait)
    {
        if (Volatile.Read(ref _launchPhaseTermination) == 0)
        {
            return null;
        }

        return _termination.Task.Wait(killWait)
            ? _termination.Task.Result
            : Failed(
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                "Another terminator is still ending this launch; exit unconfirmed.",
                Publication.Identity?.Pid,
                forced: false,
                recordRetained: true);
    }

    private ManagedEtabsShutdownResult KillSlotLease(TimeSpan killWait, TimeSpan gracefulWait)
    {
        var lease = CloseSlot();
        if (lease is null)
        {
            return AnswerForAnEmptySlot(killWait);
        }

        var pid = Publication.Identity?.Pid;
        var forced = false;
        var confirmed = false;
        string? error = null;
        if (lease.TryAcquire())
        {
            try
            {
                if (gracefulWait > TimeSpan.Zero && !lease.HasExited)
                {
                    _ = lease.WaitForExit(gracefulWait);
                }

                if (!lease.HasExited)
                {
                    forced = true;
                    lease.Kill();
                }

                confirmed = lease.HasExited || lease.WaitForExit(killWait);
            }
#pragma warning disable CA1031 // an unconfirmed exit is reported, never thrown
            catch (Exception exception)
#pragma warning restore CA1031
            {
                error = EtabsApiDiagnosticFormatter.InfrastructureException(
                    "Process.Kill(authoritative-owned-handle)",
                    exception);
                confirmed = SafeHasExited(lease);
            }
            finally
            {
                lease.Release();
            }
        }
        else
        {
            confirmed = lease.ExitConfirmed;
        }

        if (!confirmed)
        {
            return Failed(
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                error ?? "The exact owned ETABS process did not confirm exit after forced termination.",
                pid,
                forced: true,
                recordRetained: _records.Read() is not null);
        }

        var recordRetained = ClearRecordAfterConfirmedExit(lease);
        ClearPublication();
        lease.ReleaseOwner();
        MarkEnded();
        return Succeeded(pid, forced, recordRetained);
    }

    /// <summary>
    /// The slot was empty at termination. If a startup worker is running it owns the cleanup
    /// of any process it creates, and nothing here is proof that no process exists, so the
    /// answer is UNCONFIRMED unless the worker reports - within <paramref name="killWait"/> -
    /// that it killed a late handle with a confirmed exit, or that it created none (card
    /// P2-1). A late kill that is not confirmed makes the worker write the provisional
    /// record, so the orphan stays described.
    /// </summary>
    private ManagedEtabsShutdownResult AnswerForAnEmptySlot(TimeSpan killWait)
    {
        var retained = _records.Read() is not null;
        if (Volatile.Read(ref _workerStarted) == 0)
        {
            return retained
                ? Failed(
                    ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                    "The launch ended with no process of its own, but a record exists and is retained.",
                    Publication.Identity?.Pid,
                    forced: false,
                    recordRetained: true)
                : Succeeded(null, forced: false, recordRetained: false);
        }

        if (_worker.Task.Wait(killWait))
        {
            switch (_worker.Task.Result)
            {
                case WorkerOutcome.NoProcess:
                    return Succeeded(null, forced: false, recordRetained: _records.Read() is not null);
                case WorkerOutcome.KilledConfirmed:
                    return Succeeded(null, forced: true, recordRetained: _records.Read() is not null);
            }
        }

        return Failed(
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            "The launch was ended before its process handle was handed over; the startup worker " +
            "owns that handle and its exit is unconfirmed.",
            Publication.Identity?.Pid,
            forced: false,
            recordRetained: _records.Read() is not null);
    }

    private static bool SafeHasExited(HandleLease lease)
    {
        try
        {
            return lease.HasExited;
        }
#pragma warning disable CA1031 // unknown means unconfirmed
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    internal static ManagedEtabsShutdownResult Succeeded(int? ownedPid, bool forced, bool recordRetained) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: recordRetained,
            ApplicationExitReturnCode: null,
            OwnedPid: ownedPid));

    internal static ManagedEtabsShutdownResult Failed(
        string errorCode,
        string error,
        int? ownedPid,
        bool forced,
        bool recordRetained) => new(
        false,
        errorCode,
        error,
        new(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            ProcessExitConfirmed: false,
            Forced: forced,
            RecordRetained: recordRetained,
            ApplicationExitReturnCode: null,
            OwnedPid: ownedPid));
}
