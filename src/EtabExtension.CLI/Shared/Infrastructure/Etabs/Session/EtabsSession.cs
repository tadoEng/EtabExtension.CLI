using System.Diagnostics.CodeAnalysis;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public interface IEtabsSession : IDisposable
{
    ETABSApplication GetOrStart();
    IManagedEtabsApplication GetOrStartOwned();
    bool IsStarted { get; }
    int? ProcessId { get; }

    /// <summary>
    /// Shows the managed ETABS window because the USER asked to see it.
    ///
    /// <para>The session is created hidden and stays hidden for every background
    /// command, so this is the only way it ever reaches the screen. Callers must invoke
    /// it only after the requested model is confirmed open — revealing before that is
    /// exactly the blank <c>(Untitled)</c> window CLI #22 exists to remove.</para>
    /// </summary>
    Result RevealForExplicitUserRequest();

    /// <summary>
    /// CLI #24: fails the CURRENT request if the session was materially on screen during
    /// background work. Called after every ETABS-backed command and before its response is
    /// written, because a sticky exposure that only surfaced at session creation would let
    /// the very request that caused it answer success.
    /// </summary>
    Result CertifyNoUnconsentedExposure();

    /// <summary>Labels what the session is doing, for CLI #24's exposure evidence.</summary>
    void MarkVisibilityStage(string stage);

    ManagedEtabsShutdownResult Shutdown();

    /// <summary>
    /// <see cref="Shutdown"/> for when the session's STA worker is stuck behind a modal
    /// ETABS dialog and can never run the normal shutdown (review finding P2-1). Called from
    /// a dedicated STA thread, never the worker: the CSI exit is attempted bounded, then the
    /// exact owned process is killed through its authoritative handle.
    /// </summary>
    ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked();

    /// <summary>
    /// <see cref="ShutdownWhileWorkerBlocked()"/> after the caller already spent
    /// <paramref name="gateWait"/> waiting for a busy STA (card C7a): the session's own gate
    /// wait is that same wait, not a second one on top of it.
    /// </summary>
    ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked(TimeSpan gateWait) => ShutdownWhileWorkerBlocked();

    /// <summary>
    /// Hands the instance to the user (card C1). Bounded, never needs the STA, never exits and
    /// never kills.
    /// </summary>
    DetachResult Detach() => DetachResult.Failed(
        ManagedEtabsShutdownErrorCodes.DetachFailed,
        "This session does not support detach.");

    /// <summary>
    /// The existing, identity-verified instance, or null with the reason (review round 2, P1-2).
    /// NEVER starts, shuts down or ends anything; the read-only path for commands that must not
    /// destroy what they inspect.
    /// </summary>
    IManagedEtabsApplication? InspectExisting(out string? refusal)
    {
        refusal = "This session cannot inspect an existing instance.";
        return null;
    }

    /// <summary>
    /// After a failed EOF detach left ETABS running on purpose: no later exit, kill or disposal
    /// of this session may end it (review P1-1).
    /// </summary>
    void SuppressFurtherExit()
    {
    }

    /// <summary>
    /// Persists the model the daemon just opened, durably, in the instance record (card v2.5a
    /// A1). A failure means the identity is NOT recorded, and <c>save-and-exit</c> will refuse.
    /// </summary>
    Result RecordOpenedDocument(EtabsDocumentRecord document) => Result.Ok();

    /// <summary>
    /// The model identity in the instance record as persisted, or null when none was written
    /// (or the record is not this launch's). Reads the durable record, not memory.
    /// </summary>
    EtabsDocumentRecord? ReadRecordedDocument() => null;

    /// <summary>True once the user was shown ETABS and this daemon still owns it (card C2).</summary>
    bool IsUserVisible => false;

    /// <summary>Who holds the instance (card C5).</summary>
    EtabsDisposition Disposition => EtabsDisposition.None;

    /// <summary>The launch generation of the instance this daemon holds, or null (card C5).</summary>
    Guid? LaunchRecordId => null;
}

/// <summary>How a detach ended (card v2.3 §3).</summary>
public enum DetachOutcome
{
    /// <summary>The durable detach write committed; the user owns the instance.</summary>
    Detached,

    /// <summary>Failed before the durable write started and rolled back: still owned, nothing destroyed.</summary>
    Failed,

    /// <summary>The write started and its outcome is unknown: no rollback, nothing destructive.</summary>
    Uncertain
}

public sealed record DetachResult(
    DetachOutcome Outcome,
    string? ErrorCode,
    string? Error,
    ManagedProcessIdentity? Identity,
    Guid? LaunchRecordId)
{
    public bool Success => Outcome == DetachOutcome.Detached;

    public static DetachResult Failed(string code, string error) =>
        new(DetachOutcome.Failed, code, error, null, null);
}

public sealed class EtabsSession : IEtabsSession
{
    /// <summary>
    /// A detach is bounded to this end to end (card C1): it never needs the STA, so nothing
    /// outside its own steps can stretch it.
    /// </summary>
    public static readonly TimeSpan DetachBudgetDefault = TimeSpan.FromSeconds(5);

    /// <summary>A concurrent shutdown or EOF waits at most this long for a Detaching outcome.</summary>
    public static readonly TimeSpan DetachSettleWaitDefault = TimeSpan.FromSeconds(5);

    /// <summary>How long a blocked-worker shutdown waits for the session gate (card I6).</summary>
    public static readonly TimeSpan BlockedShutdownGateWait = TimeSpan.FromSeconds(2);

    /// <summary>Window-guard teardown joins its monitor for at most this long.</summary>
    public static readonly TimeSpan GuardTeardownBudget = TimeSpan.FromSeconds(2);

    /// <summary>Record operations and reference cleanup inside the blocked sequence.</summary>
    public static readonly TimeSpan BlockedRecordOperationsMargin = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The ONE deadline of a blocked shutdown inside the session (card P3-D): the commit
    /// wait, the gate wait, the bounded graceful exit, the forced-exit confirmation, the
    /// guard teardown and record operations all draw from it and are never added on top of
    /// each other.
    /// </summary>
    public static readonly TimeSpan BlockedShutdownBudget =
        BlockedShutdownGateWait
        + ManagedEtabsShutdownMachine.BlockedGracefulExitTimeout
        + ManagedEtabsShutdownMachine.ForcedExitTimeout
        + GuardTeardownBudget
        + BlockedRecordOperationsMargin;

    private readonly IManagedEtabsLauncher _launcher;
    private readonly IProcessInspector _processes;
    private readonly ISessionRecordStore _records;
    private readonly IManagedEtabsShutdownMachine _shutdownMachine;
    private readonly IEtabsWorkScope _work;
    private readonly LaunchPublicationCell _cell;
    private readonly TimeSpan _gateWait;
    private readonly TimeSpan _blockedBudget;
    private string? _commandStage;

    // Borrowed, never owned: this is Console.Error in production and a test's buffer under
    // test, so the session must not close it when it disposes.
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The diagnostics writer is owned by the caller, not by the session.")]
    private readonly TextWriter _diagnostics;

    // Guards the fields below, and ONLY them. Nothing that must stay observable while a
    // launch or a stuck command holds it (the pid, readiness, the lifecycle) lives behind it.
    private readonly object _gate = new();
    private IManagedEtabsApplication? _owned;
    private EtabsLaunchException? _launchFailure;
    private ManagedEtabsShutdownResult? _shutdownResult;

    // The current attempt's shared state. Written under the gate, read lock-free.
    private EtabsLaunchState? _launch;

    // Lock-free: set once the explicit reveal committed (card C2).
    private int _userVisible;

    /// <summary>Total bound of one detach. Tests shorten it.</summary>
    internal TimeSpan DetachBudget { get; set; } = DetachBudgetDefault;

    /// <summary>How long a shutdown waits for a Detaching outcome. Tests shorten it.</summary>
    internal TimeSpan DetachSettleWait { get; set; } = DetachSettleWaitDefault;

    /// <summary>Test seam: runs on the detach worker BEFORE its durable write is claimed.</summary>
    internal Action? BeforeDetachWrite { get; set; }

    /// <summary>Test seam: runs on the detach worker right after the durable write returned.</summary>
    internal Action? AfterDetachWrite { get; set; }

    public EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IEtabsWorkScope work)
        : this(launcher, processes, records, new ManagedEtabsShutdownMachine(records), work)
    {
    }

    public EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IEtabsWorkScope work,
        LaunchPublicationCell cell)
        : this(
            launcher,
            processes,
            records,
            new ManagedEtabsShutdownMachine(records),
            work,
            Console.Error,
            cell,
            BlockedShutdownGateWait)
    {
    }

    internal EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IManagedEtabsShutdownMachine shutdownMachine,
        IEtabsWorkScope work)
        : this(launcher, processes, records, shutdownMachine, work, Console.Error)
    {
    }

    internal EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IManagedEtabsShutdownMachine shutdownMachine,
        IEtabsWorkScope work,
        TextWriter diagnostics)
        : this(
            launcher,
            processes,
            records,
            shutdownMachine,
            work,
            diagnostics,
            new LaunchPublicationCell(),
            BlockedShutdownGateWait)
    {
    }

    internal EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IManagedEtabsShutdownMachine shutdownMachine,
        IEtabsWorkScope work,
        TextWriter diagnostics,
        LaunchPublicationCell cell,
        TimeSpan gateWait,
        TimeSpan? blockedBudget = null)
    {
        _blockedBudget = blockedBudget ?? BlockedShutdownBudget;
        _launcher = launcher;
        _processes = processes;
        _records = records;
        _shutdownMachine = shutdownMachine;
        _work = work;
        _diagnostics = diagnostics;
        _cell = cell;
        _gateWait = gateWait;
    }

    /// <summary>
    /// Owned and committed. Lock-free (card I3): a launch holds the gate for its whole
    /// duration, and a reader of this must never wait for it.
    /// </summary>
    public bool IsStarted =>
        Volatile.Read(ref _launch) is { IsReady: true } launch
        && launch.Publication.Identity is not null;

    /// <summary>
    /// The pid of the launching or running ETABS, published the moment its identity is
    /// known and cleared only after a confirmed exit. Lock-free: the watchdog reads it while
    /// the launch still holds the gate.
    /// </summary>
    public int? ProcessId => _cell.Identity?.Pid;

    /// <inheritdoc />
    public bool IsUserVisible =>
        Volatile.Read(ref _userVisible) == 1 && Disposition == EtabsDisposition.Owned;

    /// <inheritdoc />
    public EtabsDisposition Disposition
    {
        get
        {
            var launch = Volatile.Read(ref _launch);
            if (launch is null || !launch.Began)
            {
                return EtabsDisposition.None;
            }

            return launch.Lifecycle switch
            {
                LaunchLifecycle.Detached => EtabsDisposition.Detached,
                LaunchLifecycle.Detaching or LaunchLifecycle.DetachUncertain => EtabsDisposition.Detaching,
                LaunchLifecycle.Ended => EtabsDisposition.None,
                _ => EtabsDisposition.Owned
            };
        }
    }

    /// <inheritdoc />
    public Guid? LaunchRecordId =>
        Disposition == EtabsDisposition.None ? null : Volatile.Read(ref _launch)?.LaunchRecordId;

    /// <summary>
    /// After the terminal fence is attempted, may this path go on to end the process? False
    /// only when a detach won: the instance is then the user's, and no rival path may exit or
    /// kill it (card C1, I11).
    /// </summary>
    private bool MayTerminate(EtabsLaunchState? launch)
    {
        if (launch is null)
        {
            return true;
        }

        // A CAS loop over EXPLICIT states (review P1-4). Observing Ready is never enough: the
        // path must WIN Ready -> Terminating, or it learns that something else owns the
        // instance. A detach that rolled back and a retried detach can both pass through Ready
        // in between, so the decision is re-made from the live state every time.
        while (true)
        {
            var observed = launch.Lifecycle;
            BetweenFenceObservationAndCas?.Invoke();
            switch (observed)
            {
                case LaunchLifecycle.Ready:
                    if (launch.TryBeginReadyTermination())
                    {
                        return true;
                    }

                    continue;
                case LaunchLifecycle.Detaching:
                case LaunchLifecycle.Detached:
                case LaunchLifecycle.DetachUncertain:
                case LaunchLifecycle.Suppressed:
                    // Suppressed is decided by the SAME CAS word (review round 3, P1): a path
                    // that lost Ready -> Terminating to Ready -> Suppressed learns it here.
                    return false;
                default:
                    // Launching, Committing, Terminating, Ended: the termination fence is
                    // already owned elsewhere (or never applied), and nothing detached.
                    return true;
            }
        }
    }

    /// <summary>Test seam: runs between observing the lifecycle and the fence CAS (review P1-4).</summary>
    internal Action? BetweenFenceObservationAndCas { get; set; }

    // Set by the EOF path once a detach-visible detach failed and left ETABS running on purpose
    // (review P1-1): nothing - including disposal of this session - may then exit or kill it.
    /// <inheritdoc />
    public void SuppressFurtherExit()
    {
        var launch = Volatile.Read(ref _launch);
        if (launch is null)
        {
            return;
        }

        // ONE atomic decision (review round 3, P1): Ready -> Suppressed competes with
        // Ready -> Terminating on the same CAS. Any other state means a rival already owns the
        // instance (detach states, Terminating, Ended) and there is nothing to suppress.
        while (true)
        {
            if (launch.Lifecycle != LaunchLifecycle.Ready || launch.TrySuppress())
            {
                return;
            }
        }
    }

    private ManagedEtabsShutdownResult SuppressedAnswer() => new(
        false,
        ManagedEtabsShutdownErrorCodes.DetachFailed,
        "ETABS was deliberately left running (a failed detach on EOF); nothing was exited or killed.",
        new(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: _cell.Identity?.Pid)
        {
            Disposition = EtabsDisposition.Owned
        });

    public ETABSApplication GetOrStart() => GetOrStartOwned().Application;

    /// <inheritdoc />
    public IManagedEtabsApplication? InspectExisting(out string? refusal)
    {
        refusal = null;
        var launch = Volatile.Read(ref _launch);
        var owned = Volatile.Read(ref _owned);
        if (launch is not { Lifecycle: LaunchLifecycle.Ready } || owned is null)
        {
            refusal = "No ready, owned ETABS instance exists to inspect.";
            return null;
        }

        // The same identity proof GetOrStartOwned makes, but a failure only REFUSES: it starts
        // nothing, shuts nothing down and latches nothing (review round 2, P1-2).
        try
        {
            var record = _records.Read();
            var live = _processes.Find(owned.Identity.Pid);
            if (record is null || live is null
                || record.ManagedLaunchRecordId != owned.ManagedLaunchRecordId
                || !OrphanSessionCleaner.IdentityMatches(record, live)
                || live != owned.Identity)
            {
                refusal = "The instance's process identity could not be verified; nothing was started or ended.";
                return null;
            }
        }
#pragma warning disable CA1031 // an unreadable identity is a refusal, never a cleanup
        catch (Exception exception)
#pragma warning restore CA1031
        {
            refusal = "The instance's process identity could not be read: " +
                EtabsApiDiagnosticFormatter.Bounded(exception.Message);
            return null;
        }

        return owned;
    }

    public IManagedEtabsApplication GetOrStartOwned()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _launch) is { IsDetachDisposition: true })
            {
                // Typed, non-latching and non-destructive: the instance is the user's now.
                throw new EtabsLaunchException(
                    EtabsLaunchErrorCodes.SessionDetached,
                    "This daemon handed its ETABS to the user; it will not start, use or end it.");
            }

            if (_launchFailure is not null)
            {
                throw _launchFailure;
            }

            if (_shutdownResult is not null)
            {
                throw new InvalidOperationException(
                    "Managed ETABS session has reached a terminal shutdown state.");
            }

            if (_owned is null)
            {
                // THE consent gate. Before anything is started, and before any diagnostic
                // claims a start is under way.
                //
                // It guards a COLD start only. A session that already exists - hidden, or
                // deliberately shown to the engineer - serves later background work without
                // asking again, because nothing about reusing it puts a new window on
                // screen. It is process CREATION that the engineer had to agree to.
                RequireVisibleStartConsent();

                _diagnostics.WriteLine(
                    "ℹ Starting ETABS (shared serve session; declared start intent: " +
                    $"{_work.Current.StartIntent})...");
                var state = new EtabsLaunchState(_records, _cell);
                Volatile.Write(ref _launch, state);
                state.MarkBegun();
                var launched = Launch(state);
                _owned = launched;

                // A launcher that did not publish (a test double) is published here, so the
                // pid is observable from this point on.
                state.PublishIfAbsent(launched.Identity);
                state.AdoptRecordId(launched.ManagedLaunchRecordId);
                try
                {
                    state.EnsureProvisionalRecord(launched.Identity);
                }
                catch (Exception recordWriteException)
                {
                    throw FailRecordWrite(state, launched, recordWriteException);
                }

                if (state.Lifecycle != LaunchLifecycle.Launching)
                {
                    throw AbortedWhileLaunching(state);
                }

                // Readiness and hidden state are one gate, not two (CLI #20/#24): an
                // unconfirmed hidden state ends the session on the same path a failed
                // InitializeNewModel does, so "started hidden" can only follow a proof.
                launched.MarkVisibilityStage("cSapModel.InitializeNewModel");
                var creationFailure = Initialize(launched)
                    ?? CompleteReadiness(launched)
                    ?? ConfirmHiddenForBackgroundWork(launched);
                if (creationFailure is not null)
                {
                    _ = state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Terminating);
                    var cleanup = _shutdownMachine.Shutdown(launched);
                    AfterShutdown(state, cleanup);
                    _launchFailure = new EtabsLaunchException(
                        creationFailure.Value.Code,
                        WithTerminalFacts(
                            creationFailure.Value.Diagnostic,
                            cleanup),
                        creationFailure.Value.Exception);
                    throw _launchFailure;
                }

                Commit(state, launched);
                _diagnostics.WriteLine($"✓ ETABS started hidden (PID {_owned.Identity.Pid})");

                // The launch owned the stage until now. Hand it back to whatever command
                // is actually running, so the next observation is attributed to the work
                // and not to the initialization that preceded it.
                if (_commandStage is { } pending)
                {
                    _owned.MarkVisibilityStage(pending);
                }
            }

            var current = Volatile.Read(ref _launch);
            try
            {
                if (current is not { IsReady: true })
                {
                    throw new InvalidOperationException(
                        "Managed ETABS session is owned but not API-ready.");
                }
                Verify(_owned);
                return _owned;
            }
            catch
            {
                if (!MayTerminate(current))
                {
                    // A detach won between the check above and here: the instance is the
                    // user's. Fail this request and touch nothing.
                    throw;
                }

                var cleanup = _shutdownMachine.Shutdown(_owned);
                _shutdownResult = cleanup;
                if (current is not null)
                {
                    AfterShutdown(current, cleanup);
                }
                throw;
            }
        }
    }

    /// <summary>
    /// The one commit point (card W1, X1): the launch wins <c>Launching → Committing</c> or
    /// loses to a shutdown that already won Terminating. The final record is written inside
    /// the short Committing window and Ready is reached only by the final compare-exchange,
    /// so a shutdown that timed out waiting (X1) can never be followed by a Ready.
    /// </summary>
    private void Commit(EtabsLaunchState state, IManagedEtabsApplication launched)
    {
        if (!state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Committing))
        {
            throw AbortedWhileLaunching(state);
        }

        try
        {
            state.WriteFinalRecord(launched.Identity);
        }
        catch (Exception recordWriteException)
        {
            _ = state.TryTransition(LaunchLifecycle.Committing, LaunchLifecycle.Terminating);
            throw FailRecordWrite(state, launched, recordWriteException);
        }

        if (!state.TryTransition(LaunchLifecycle.Committing, LaunchLifecycle.Ready))
        {
            // The shutdown took Terminating from the stalled commit. It kills through the
            // lease and answers; this side never reports Ready, and clears the record it
            // wrote only if that exit is confirmed (card Y1).
            _ = state.ClearRecordAfterConfirmedExit(() => launched.HasExited);
            throw AbortedWhileLaunching(state);
        }

        // Ready. A licence window was seen live ~13 s after the attach, so the startup
        // blocker stays armed (watched by the watchdog) until the first command completes.
        state.ArmStartupBlocker();
    }

    private EtabsLaunchException FailRecordWrite(
        EtabsLaunchState state,
        IManagedEtabsApplication launched,
        Exception recordWriteException)
    {
        _ = state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Terminating);
        var cleanup = _shutdownMachine.ShutdownAfterRecoveryRecordWriteFailure(launched);
        AfterShutdown(state, cleanup);
        _launchFailure = new EtabsLaunchException(
            EtabsLaunchErrorCodes.RecoveryRecordWriteFailed,
            WithTerminalFacts(
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "ManagedEtabsSessionRecord.Write",
                    recordWriteException),
                cleanup),
            recordWriteException);
        return _launchFailure;
    }

    /// <summary>
    /// A shutdown or the startup supervisor took the launch while this thread was still
    /// inside it. The terminator answered for the process; this side records that answer as
    /// the session's terminal state and fails the request.
    /// </summary>
    private EtabsLaunchException AbortedWhileLaunching(EtabsLaunchState state)
    {
        var result = state.TerminationResult
            ?? state.TerminateLaunch(ManagedEtabsLauncher.TerminatorExitTimeout)
            ?? EtabsLaunchState.Failed(
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                "The launch was ended before it committed.",
                state.Publication.Identity?.Pid,
                forced: false,
                recordRetained: true);
        _shutdownResult ??= result;
        if (result.Data.ProcessExitConfirmed)
        {
            _owned = null;
        }

        return new EtabsLaunchException(
            state.TimedOut ? EtabsLaunchErrorCodes.StartTimeout : EtabsLaunchErrorCodes.LaunchAborted,
            WithTerminalFacts("The managed ETABS launch was ended before it committed.", result));
    }

    /// <summary>Settles the lifecycle and the publication after a shutdown answered.</summary>
    private void AfterShutdown(EtabsLaunchState state, ManagedEtabsShutdownResult cleanup)
    {
        _shutdownResult = cleanup;
        if (cleanup.Data.ProcessExitConfirmed)
        {
            _owned = null;
            state.ClearPublication();
            state.MarkEnded();
        }
    }

    /// <summary>
    /// Ends the session because its on-screen state can no longer be vouched for.
    ///
    /// <para><b>Why terminal.</b> Two routes reach here and they share one property. A
    /// failed reveal has already issued or attempted <c>cOAPI.Unhide()</c> and left the
    /// protected interval, so ETABS may be visible or may not be. A certification whose
    /// census could not run knows even less: nothing was observed at all. In both cases the
    /// session is UNCERTAIN, not proven clean.</para>
    ///
    /// <para>Returning a failure while keeping the session reusable would leave later
    /// background work running against an unwatched session that might be sitting in front
    /// of the engineer. That is exactly the case CLI #24 exists to refuse, so an uncertain
    /// session is spent rather than reused - and the next request cold-starts a fresh one,
    /// asking the engineer for consent again.</para>
    /// </summary>
    private Result EndUncertainSession(IManagedEtabsApplication owned, string diagnostic)
    {
        var current = Volatile.Read(ref _launch);
        if (!MayTerminate(current))
        {
            return Result.Fail(diagnostic + "; the instance was handed to the user, so it was not ended");
        }

        var cleanup = _shutdownMachine.Shutdown(owned);
        _shutdownResult = cleanup;
        if (current is not null)
        {
            AfterShutdown(current, cleanup);
        }

        return Result.Fail(WithTerminalFacts(diagnostic, cleanup));
    }

    /// <summary>
    /// Refuses a cold start that nobody declared an intent for.
    ///
    /// <para>Fails BEFORE <c>cHelper.CreateObject</c>, which is the only useful place: once
    /// that call returns, ETABS is already starting and already heading for the screen.
    /// The refusal is typed so the desktop can tell "you must ask the engineer first" apart
    /// from every other launch failure and show the consent prompt rather than an error.</para>
    ///
    /// <para>The intent is never inferred from the command name. A command called
    /// <c>snapshot-export</c> tells us what work was asked for, not whether anyone warned
    /// the engineer that a window is about to appear — and inferring the second from the
    /// first is exactly how an unconsented exposure would get rationalised as expected.</para>
    /// </summary>
    private void RequireVisibleStartConsent()
    {
        // Read from the EXECUTION context. Reading a request-lifetime value here was the
        // defect: a queued operation runs after its request returned, so it would see
        // whatever the ambient field had decayed to - or worse, whatever a later polling
        // request had just published into it.
        var intent = _work.Current.StartIntent;
        if (ManagedEtabsStartIntents.PermitsStart(intent))
        {
            return;
        }

        // Thrown WITHOUT populating _launchFailure. That latch is for failures which
        // leave the world in a state this session can no longer reason about - a started
        // process it could not resolve, a recovery record it could not write. A refusal is
        // the opposite: nothing happened at all, and the very next request may legitimately
        // arrive with consent after the desktop has prompted. Caching it here would make
        // the intended flow - request, refusal, prompt, retry - impossible without
        // restarting the daemon.
        throw new EtabsLaunchException(
            EtabsLaunchErrorCodes.VisibleStartConsentMissing,
            EtabsApiDiagnosticFormatter.Bounded(string.Join(
                "; ",
                $"declaredIntent={intent}",
                $"expected={ManagedEtabsStartIntents.VisibleByConsent}",
                "starting ETABS puts it on screen for several seconds and that cannot be " +
                "prevented on this ETABS build, so a cold start requires the caller to " +
                "declare that the engineer was told and agreed. No process was created, " +
                "and this refusal does not persist: retry with the intent declared.")));
    }

    /// <summary>
    /// Launches, and caches an unresolved launch cleanup as terminal state.
    ///
    /// <para>When a failed launch could not prove that the process it started is gone,
    /// there is no owned handle and no recovery record to describe it. Without caching,
    /// the next request would relaunch on top of it and a later shutdown would report
    /// success with <c>processExitConfirmed=true</c> — a clean answer about a process
    /// nobody resolved.</para>
    /// </summary>
    private IManagedEtabsApplication Launch(EtabsLaunchState state)
    {
        try
        {
            return _launcher.Launch(state);
        }
        catch (EtabsLaunchException failure) when (failure.Cleanup is { Success: false })
        {
            _ = state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Terminating);
            _shutdownResult = failure.Cleanup;
            _launchFailure = failure;
            throw;
        }
        catch
        {
            // A failed attempt is over, whatever the reason: it can no longer commit.
            _ = state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Terminating);
            throw;
        }
    }

    /// <summary>
    /// Wraps the same started object with EtabSharp, only after initialization returned
    /// zero. A wrap failure is a launch failure: the session must not be exposed holding a
    /// handle nothing can use.
    /// </summary>
    private static (string Code, string Diagnostic, Exception? Exception)? CompleteReadiness(
        IManagedEtabsApplication owned)
    {
        try
        {
            owned.CompleteApiReadiness();
            return null;
        }
        catch (Exception exception)
        {
            return (
                EtabsLaunchErrorCodes.ModelInitializationFailed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "ETABSWrapper.WrapExisting",
                    exception),
                exception);
        }
    }

    private static (string Code, string Diagnostic, Exception? Exception)? Initialize(
        IManagedEtabsApplication owned)
    {
        try
        {
            var returnCode = owned.InitializeNewModel();
            return returnCode == 0
                ? null
                : (
                    EtabsLaunchErrorCodes.ModelInitializationFailed,
                    EtabsApiDiagnosticFormatter.ApiReturn(
                        "cSapModel.InitializeNewModel",
                        returnCode),
                    (Exception?)null);
        }
        catch (Exception exception)
        {
            return (
                EtabsLaunchErrorCodes.ModelInitializationFailed,
                EtabsApiDiagnosticFormatter.Exception(
                    "cSapModel.InitializeNewModel",
                    exception),
                exception);
        }
    }

    private static string WithTerminalFacts(
        string diagnostic,
        ManagedEtabsShutdownResult cleanup) =>
        EtabsApiDiagnosticFormatter.AppendTerminalFacts(
            diagnostic,
            $"state={cleanup.Data.State}; " +
            $"processExitConfirmed={cleanup.Data.ProcessExitConfirmed}; " +
            $"forced={cleanup.Data.Forced}; " +
            $"recordRetained={cleanup.Data.RecordRetained}");

    private void Verify(IManagedEtabsApplication owned)
    {
        var record = _records.Read();
        var live = _processes.Find(owned.Identity.Pid);
        if (record is null || live is null
            || record.ManagedLaunchRecordId != owned.ManagedLaunchRecordId
            || !OrphanSessionCleaner.IdentityMatches(record, live)
            || live != owned.Identity)
        {
            throw new InvalidOperationException(
                "Managed ETABS identity verification failed; a clean reopen is required.");
        }
    }

    /// <summary>
    /// The second and last background-suppression proof of a session's life, taken once
    /// <c>InitializeNewModel</c> has produced the blank model and the EtabSharp wrap
    /// exists, and still before the application is handed to any command.
    ///
    /// <para>The launcher already proved suppression right after <c>ApplicationStart</c>.
    /// This is not redundant: <c>InitializeNewModel</c> is what puts the blank
    /// <c>(Untitled)</c> model behind the frame, and the #20 timeline shows ETABS building
    /// UI long after the start call returns. A second exact-owned census costs one window
    /// enumeration and covers everything that surfaced in between.</para>
    ///
    /// <para><b>Failure is terminal.</b> An unproven Windows state returns a launch failure
    /// rather than a warning, and the caller tears the session down through the same
    /// cleanup a failed initialization uses — the authoritative owned process is exited,
    /// not left running with a window on screen.</para>
    ///
    /// <para>It runs ONLY while the session is being created. That is the whole reason a
    /// background command can safely reuse a session the user asked to see: nothing on the
    /// command path ever suppresses anything.</para>
    /// </summary>
    private (string Code, string Diagnostic, Exception? Exception)? ConfirmHiddenForBackgroundWork(
        IManagedEtabsApplication owned)
    {
        var confirmation = owned.ConfirmWindowsSuppressed();
        if (!confirmation.Confirmed)
        {
            return (
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    confirmation.Diagnostic
                        ?? "Managed ETABS window suppression could not be confirmed.",
                    "stage=after cSapModel.InitializeNewModel"),
                (Exception?)null);
        }

        // CLI #24. "Hidden now" is not the question the product asks. An earlier candidate
        // logged "started hidden" truthfully seconds after putting a full-screen ETABS
        // window in front of the engineer, because a point-in-time census cannot see what
        // already happened. Readiness therefore needs BOTH: currently hidden, AND never
        // materially on screen since the consent interval closed.
        var exposure = owned.Exposure;
        if (exposure.Observed)
        {
            return (
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    exposure.Describe(),
                    "stage=after cSapModel.InitializeNewModel; " +
                    "a later hidden census cannot clear an exposure that already happened"),
                (Exception?)null);
        }

        _diagnostics.WriteLine(
            "✓ ETABS background UI suppression confirmed before use " +
            $"(observations={confirmation.Observations}, " +
            $"waitedMs={(long)confirmation.Waited.TotalMilliseconds}, " +
            $"state={owned.VisibilityState}, unconsentedExposure=false).");
        return null;
    }

    /// <inheritdoc />
    public void MarkVisibilityStage(string stage)
    {
        lock (_gate)
        {
            // Remembered unconditionally. The first command on a cold daemon marks its
            // stage BEFORE the session exists, and the launch then walks through its own
            // stages - cOAPI.ApplicationStart, cSapModel.InitializeNewModel. Without this
            // memory, nobody reapplied the command's label afterwards and an exposure
            // during the actual work was filed under a stage that had already finished.
            _commandStage = stage;
            if (_owned is not null && IsStarted)
            {
                _owned.MarkVisibilityStage(stage);
            }
        }
    }

    /// <summary>
    /// CLI #24's per-request certification. A background command that put ETABS on screen
    /// must not be able to return success just because ETABS hid itself again before the
    /// response was written.
    ///
    /// <para>The readiness gate cannot cover this: it runs once, while the session is being
    /// created. Everything after that - every export, every extraction - runs against an
    /// already-ready session, and without this the daemon would observe the exposure,
    /// record it faithfully, and still answer <c>success:true</c> to the very request that
    /// caused it.</para>
    ///
    /// <para>The session is then TERMINATED rather than left running. The evidence is
    /// sticky by design, so every later request would fail this same check anyway; ending
    /// it cleanly turns a permanently-failing zombie into a fresh start that will ask the
    /// engineer for consent again.</para>
    /// </summary>
    public Result CertifyNoUnconsentedExposure()
    {
        lock (_gate)
        {
            if (_owned is null || !IsStarted)
            {
                return Result.Ok();
            }

            // Visibility the engineer asked for, or is in the act of asking for, is not a
            // breach. Only the protected background interval is certified.
            if (_owned.VisibilityState != ManagedEtabsVisibilityState.BackgroundHidden)
            {
                return Result.Ok();
            }

            // A forced census, not a read. The accumulated evidence alone cannot see a
            // window that surfaced moments ago whose event callback has not been delivered
            // yet - which is precisely the window this command would otherwise report
            // success over.
            ManagedEtabsExposureEvidence exposure;
            try
            {
                exposure = _owned.CertifyExposure();
            }
            catch (Exception censusFailure)
            {
                // FAIL CLOSED. The census is the visibility authority; if it could not run,
                // the session's on-screen state at this instant is unknown, and "unknown"
                // must never be spent as "clean". Failing the request alone would leave the
                // same uncertified session ready to serve the next background command -
                // possibly in front of the engineer - which is the identical
                // uncertain-session class that already makes a failed reveal terminal.
                //
                // Every exception is caught on purpose. This is the last gate before a
                // response leaves the daemon, and there is no exception type whose arrival
                // here would make reusing the session safe.
                return EndUncertainSession(
                    _owned,
                    EtabsApiDiagnosticFormatter.Bounded(string.Join(
                        "; ",
                        ManagedEtabsWindowErrorCodes.ExposureNotCertified,
                        $"ownedPid={_owned.Identity.Pid}",
                        EtabsApiDiagnosticFormatter.InfrastructureException(
                            "ManagedEtabsWindowGuard.CertifyExposure",
                            censusFailure),
                        "the visibility authority could not certify the session's final " +
                        "on-screen state; this request is failed and the session has been " +
                        "ended rather than reused")));
            }

            if (!exposure.Observed)
            {
                return Result.Ok();
            }

            return EndUncertainSession(
                _owned,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    exposure.Describe(),
                    "the managed ETABS session was materially on screen during background " +
                    "work the engineer did not ask to see; this request is failed and the " +
                    "session has been ended"));
        }
    }

    /// <inheritdoc />
    public Result RevealForExplicitUserRequest()
    {
        lock (_gate)
        {
            var owned = GetOrStartOwned();

            // C3: userVisible is durable BEFORE anything is revealed. A failed write means
            // nothing reaches the screen and the request fails typed; the session itself is
            // untouched, still hidden and still usable.
            var launch = Volatile.Read(ref _launch);
            if (launch is not null)
            {
                var persisted = false;
                string? writeFailure = null;
                try
                {
                    persisted = launch.WriteUserVisibleRecord(owned.Identity);
                }
#pragma warning disable CA1031 // any failure to persist means no reveal
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    writeFailure = EtabsApiDiagnosticFormatter.InfrastructureException(
                        "ManagedEtabsSessionRecord.Write(userVisible)",
                        exception);
                }

                if (!persisted)
                {
                    return Result.Fail(EtabsApiDiagnosticFormatter.Bounded(
                        $"[{EtabsLaunchErrorCodes.RecoveryRecordWriteFailed}] " +
                        "The userVisible evidence could not be written, so ETABS was not revealed. " +
                        (writeFailure ?? "The recovery record is not this launch's.")));
                }

                // The user is about to be shown ETABS: its floating tool windows and progress
                // forms are no startup blocker (review P2-A).
                launch.DisarmStartupBlocker();
            }

            // Order is the contract, and every step of it is load bearing.
            //
            // The caller has already confirmed the requested model is open. The protected
            // interval is retired FIRST so that the observer cannot record the reveal
            // itself as an unconsented exposure, and so nothing re-arms behind it.
            //
            // What changed with CLI #22: this step used to also put our own hidden HWNDs
            // back with ShowWindow(SW_SHOW), and that restore was what actually reached
            // the screen, because the stuck cOAPI.Visible() flag made the CSI policy skip
            // its Unhide entirely. Diagnostic #4 removed both halves of that workaround and
            // measured the result: with ShowWindow impossible in either direction, an
            // unconditional raw Unhide put the window back 14 ms later. Windows observes;
            // CSI acts.
            owned.ReleaseWindowGuardForExplicitUserAction();

            // CSI mutates, unconditionally. A throw means the call never happened, so
            // there is nothing for the census to certify.
            var csi = owned.ApplyCsiUnhideForExplicitUserAction();
            if (!csi.Issued)
            {
                return EndUncertainSession(
                    owned,
                    EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                        csi.Diagnostic ?? "cOAPI.Unhide could not be issued.",
                        "csiTransitionIssued=false"));
            }

            // THE gate: Windows itself must report an owned top-level ETABS window
            // materially on screen. "Open in ETABS" that shows nothing has not done what
            // was asked, whatever CSI says about it.
            var windows = owned.ConfirmWindowsRevealed();
            if (!windows.Confirmed)
            {
                return EndUncertainSession(
                    owned,
                    EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                        windows.Diagnostic
                            ?? "No owned ETABS window could be confirmed visible.",
                        $"csiReturnCode={csi.ReturnCode}; csiConfirmed={csi.Confirmed}"));
            }

            // Only now is the session UserVisible. Later background work reuses it as-is
            // and must never quietly take the screen back.
            owned.EnterUserVisible();
            Volatile.Write(ref _userVisible, 1);

            _diagnostics.WriteLine(
                $"✓ ETABS shown (PID {owned.Identity.Pid}; " +
                $"visibleOwnedWindows={windows.ObservedWindows.Count}, " +
                $"csiReturnCode={csi.ReturnCode}, state={owned.VisibilityState})");

            return Result.Ok();
        }
    }

    /// <inheritdoc />
    public Result RecordOpenedDocument(EtabsDocumentRecord document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            var owned = GetOrStartOwned();
            var launch = Volatile.Read(ref _launch);
            if (launch is null)
            {
                return Result.Fail(EtabsApiDiagnosticFormatter.Bounded(
                    $"[{EtabsLaunchErrorCodes.RecoveryRecordWriteFailed}] There is no launch to record the " +
                    "opened document for."));
            }

            try
            {
                return launch.WriteDocumentRecord(owned.Identity, document)
                    ? Result.Ok()
                    : Result.Fail(EtabsApiDiagnosticFormatter.Bounded(
                        $"[{EtabsLaunchErrorCodes.RecoveryRecordWriteFailed}] The recovery record is not this " +
                        "launch's, so the opened document was not recorded."));
            }
#pragma warning disable CA1031 // any failure to persist means the identity is not recorded
            catch (Exception exception)
#pragma warning restore CA1031
            {
                return Result.Fail(EtabsApiDiagnosticFormatter.Bounded(
                    $"[{EtabsLaunchErrorCodes.RecoveryRecordWriteFailed}] The opened document could not be " +
                    "recorded. " + EtabsApiDiagnosticFormatter.InfrastructureException(
                        "ManagedEtabsSessionRecord.Write(document)",
                        exception)));
            }
        }
    }

    /// <inheritdoc />
    public EtabsDocumentRecord? ReadRecordedDocument()
    {
        try
        {
            return Volatile.Read(ref _launch)?.ReadDocument();
        }
#pragma warning disable CA1031 // an unreadable record is "no recorded document", which refuses
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public ManagedEtabsShutdownResult Shutdown() =>
        ShutdownWith((owned, _) => _shutdownMachine.Shutdown(owned), workerBlocked: false);

    /// <inheritdoc />
    public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked() =>
        ShutdownWith(_shutdownMachine.ShutdownWhileWorkerBlocked, workerBlocked: true);

    /// <inheritdoc />
    public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked(TimeSpan gateWait) =>
        ShutdownWith(_shutdownMachine.ShutdownWhileWorkerBlocked, workerBlocked: true, gateWait);

    // ── detach (card C1, v2.3 §3) ───────────────────────────────────────────

    private ManagedEtabsShutdownResult DetachedAnswer(EtabsLaunchState launch) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: false,
            ApplicationExitReturnCode: null,
            OwnedPid: launch.DetachedIdentity?.Pid)
        {
            Disposition = EtabsDisposition.Detached
        });

    private ManagedEtabsShutdownResult DetachUncertainAnswer(EtabsLaunchState launch) => new(
        false,
        ManagedEtabsShutdownErrorCodes.DetachUncertain,
        "A detach is unresolved: its durable write started and its outcome is unknown. Nothing " +
        "was exited or killed; the recovery record is the evidence.",
        new(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: launch.Publication.Identity?.Pid ?? launch.DetachedIdentity?.Pid)
        {
            Disposition = EtabsDisposition.Detaching
        });

    /// <summary>
    /// What a shutdown or EOF answers when a detach holds the instance (card C1): Detached →
    /// nothing to do; Uncertain (or still unresolved after the settle wait) → nothing
    /// destructive; a rollback to Ready → null, and the caller proceeds under its own
    /// disposition.
    /// </summary>
    private ManagedEtabsShutdownResult? AnswerForDetachDisposition(EtabsLaunchState launch)
    {
        var settled = launch.Lifecycle == LaunchLifecycle.Detaching
            ? launch.WaitForDetachOutcome(DetachSettleWait)
            : launch.Lifecycle;
        return settled switch
        {
            LaunchLifecycle.Detached => DetachedAnswer(launch),
            LaunchLifecycle.Detaching or LaunchLifecycle.DetachUncertain => DetachUncertainAnswer(launch),
            _ => null
        };
    }

    /// <inheritdoc />
    public DetachResult Detach()
    {
        var launch = Volatile.Read(ref _launch);
        if (launch is null || !launch.Began)
        {
            return DetachResult.Failed(
                ManagedEtabsShutdownErrorCodes.DetachFailed,
                "No managed ETABS instance is held, so there is nothing to detach.");
        }

        while (true)
        {
            switch (launch.Lifecycle)
            {
                case LaunchLifecycle.Detached:
                    return new(DetachOutcome.Detached, null, null, launch.DetachedIdentity, launch.LaunchRecordId);
                case LaunchLifecycle.DetachUncertain:
                    return UncertainResult(launch);
                case LaunchLifecycle.Detaching:
                    if (launch.WaitForDetachOutcome(DetachSettleWait) == LaunchLifecycle.Detaching)
                    {
                        return UncertainResult(launch);
                    }

                    continue;
                case LaunchLifecycle.Ready:
                    if (!launch.TryBeginDetach())
                    {
                        continue;
                    }

                    return RunDetach(launch);
                default:
                    return DetachResult.Failed(
                        ManagedEtabsShutdownErrorCodes.DetachFailed,
                        $"The instance is not ready to be detached (lifecycle {launch.Lifecycle}).");
            }
        }
    }

    private static DetachResult UncertainResult(EtabsLaunchState launch) => new(
        DetachOutcome.Uncertain,
        ManagedEtabsShutdownErrorCodes.DetachUncertain,
        "The detach's durable write started and its outcome is unknown. Nothing was exited or killed.",
        launch.Publication.Identity ?? launch.DetachedIdentity,
        launch.LaunchRecordId);

    /// <summary>
    /// The detach after it won <c>Ready → Detaching</c>. The body runs on its own thread and
    /// this method waits at most <see cref="DetachBudget"/> for it: a timeout BEFORE the
    /// durable write started rolls back, a timeout once it started is Uncertain, and a
    /// completed write is Detached (card v2.3 §3). It never needs the STA and never exits or
    /// kills.
    /// </summary>
    private DetachResult RunDetach(EtabsLaunchState launch)
    {
        var identity = launch.Publication.Identity;
        var owned = Volatile.Read(ref _owned);
        if (identity is null || owned is null)
        {
            _ = launch.TryTransition(LaunchLifecycle.Detaching, LaunchLifecycle.Ready);
            return DetachResult.Failed(
                ManagedEtabsShutdownErrorCodes.DetachFailed,
                "The instance's identity or handle is unavailable; the detach was rolled back.");
        }

        var attempt = new DetachAttempt();
        var body = Task.Factory.StartNew(
            () => DetachBody(launch, attempt, identity, owned),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        if (body.Wait(DetachBudget))
        {
            return body.Result;
        }

        if (attempt.TryChooseRollback())
        {
            // The write never started and now never will: the instance is still owned.
            _ = launch.TryTransition(LaunchLifecycle.Detaching, LaunchLifecycle.Ready);
            return DetachResult.Failed(
                ManagedEtabsShutdownErrorCodes.DetachFailed,
                $"The detach did not reach its durable write within {DetachBudget.TotalSeconds:0.###}s; " +
                "it was rolled back and nothing was exited or killed.");
        }

        if (attempt.Committed)
        {
            // After the commit point the detach IS done (card v2.3 §3): what is left (the guard,
            // the COM release, the lease) is best effort and keeps running on the worker.
            launch.DetachedIdentity = identity;
            _ = launch.TryTransition(LaunchLifecycle.Detaching, LaunchLifecycle.Detached);
            return new(DetachOutcome.Detached, null, null, identity, launch.LaunchRecordId);
        }

        // The write started and has not returned: no rollback. It may still land, so the state
        // is Uncertain (or already Detached if the worker finished in the same instant).
        _ = launch.TryTransition(LaunchLifecycle.Detaching, LaunchLifecycle.DetachUncertain);
        return launch.Lifecycle == LaunchLifecycle.Detached
            ? new(DetachOutcome.Detached, null, null, launch.DetachedIdentity, launch.LaunchRecordId)
            : UncertainResult(launch);
    }

    private DetachResult DetachBody(
        EtabsLaunchState launch,
        DetachAttempt attempt,
        ManagedProcessIdentity identity,
        IManagedEtabsApplication owned)
    {
        bool? userVisible;
        try
        {
            BeforeDetachWrite?.Invoke();

            // Everything that can fail BEFORE the commit point rolls the detach back.
            if (!launch.OwnsTheRecord())
            {
                return RollBack(
                    launch,
                    attempt,
                    "The recovery record is no longer this launch's, so a detached record cannot replace it.");
            }

            userVisible = launch.ReadUserVisible();
        }
#pragma warning disable CA1031 // any pre-commit failure is a rollback, never a half-step
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return RollBack(
                launch,
                attempt,
                EtabsApiDiagnosticFormatter.InfrastructureException("detach (before the write)", exception));
        }

        if (!attempt.TryStartWrite())
        {
            // The supervisor chose the rollback while this worker was delayed: it answers,
            // and this worker writes nothing.
            return DetachResult.Failed(
                ManagedEtabsShutdownErrorCodes.DetachFailed,
                "The detach was rolled back before its durable write started.");
        }

        try
        {
            launch.WriteDetachedRecord(identity, userVisible);
        }
#pragma warning disable CA1031 // the write may or may not have landed: uncertain, never a rollback
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.InfrastructureException(
                "ManagedEtabsSessionRecord.Write(detached)",
                exception));
            _ = launch.TryTransition(LaunchLifecycle.Detaching, LaunchLifecycle.DetachUncertain);
            return UncertainResult(launch);
        }

        // ── committed: from here only logging, never a rollback ──
        launch.DetachedIdentity = identity;
        attempt.MarkCommitted();
        AfterDetachWrite?.Invoke();
        Quietly("window guard", owned.DisposeWindowGuard);
        Quietly("COM release", () =>
        {
            // Skipped when a stuck command holds the gate: the worker is inside a COM call.
            if (!Monitor.TryEnter(_gate, TimeSpan.Zero))
            {
                _diagnostics.WriteLine("ℹ Detach: COM release skipped (the session worker is busy).");
                return;
            }

            try
            {
                owned.ReleaseApiReferences();
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        });
        Quietly("process handle hand-off", owned.ReleaseOwnedProcessHandleForHandoff);
        Quietly("record clear", launch.ClearRecordAfterHandoff);
        Quietly("publication clear", launch.ClearPublication);
        // A write that landed AFTER the supervisor gave up (Uncertain) is committed handoff
        // evidence now (review P2-8): publish it, or status would strand at "detaching" with
        // the record already cleared.
        if (!launch.TryTransition(LaunchLifecycle.Detaching, LaunchLifecycle.Detached))
        {
            _ = launch.TryTransition(LaunchLifecycle.DetachUncertain, LaunchLifecycle.Detached);
        }

        _diagnostics.WriteLine($"✓ ETABS handed to the user (PID {identity.Pid}); this daemon no longer owns it.");
        return launch.Lifecycle == LaunchLifecycle.Detached
            ? new(DetachOutcome.Detached, null, null, identity, launch.LaunchRecordId)
            : UncertainResult(launch);
    }

    private DetachResult RollBack(EtabsLaunchState launch, DetachAttempt attempt, string reason)
    {
        if (attempt.TryChooseRollback())
        {
            _ = launch.TryTransition(LaunchLifecycle.Detaching, LaunchLifecycle.Ready);
        }

        return DetachResult.Failed(
            ManagedEtabsShutdownErrorCodes.DetachFailed,
            EtabsApiDiagnosticFormatter.Bounded(reason + " Nothing was exited or killed."));
    }

    private void Quietly(string step, Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // after the commit point a failing step is only logged (card v2.3 §3)
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.InfrastructureException(
                $"detach after commit: {step}",
                exception));
        }
    }

    private ManagedEtabsShutdownResult ShutdownWith(
        Func<IManagedEtabsApplication, TimeSpan, ManagedEtabsShutdownResult> shutdownWithin,
        bool workerBlocked,
        TimeSpan? gateWaitOverride = null)
    {
        var result = ShutdownWithCore(shutdownWithin, workerBlocked, gateWaitOverride);
        Volatile.Write(ref _releaseAttempted, 1);
        return result;
    }

    private ManagedEtabsShutdownResult ShutdownWithCore(
        Func<IManagedEtabsApplication, TimeSpan, ManagedEtabsShutdownResult> shutdownWithin,
        bool workerBlocked,
        TimeSpan? gateWaitOverride)
    {
        var launch = Volatile.Read(ref _launch);

        // -1. The EOF path chose to leave a visible instance running (review P1-1). Disposal of
        //     the DI scope lands here too, and must not take the gate or end anything.
        if (launch is { Lifecycle: LaunchLifecycle.Suppressed })
        {
            return SuppressedAnswer();
        }

        // 0. A detach holds the instance: shutdown and EOF take NO destructive step and
        //    answer the detach's own disposition (card C1, I11).
        if (launch is { IsDetachDisposition: true } && AnswerForDetachDisposition(launch) is { } detachAnswer)
        {
            return detachAnswer;
        }

        // ONE shared deadline for a blocked shutdown (card P3-D): every phase is capped by
        // what is left of it. The normal path keeps its per-phase bounds.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan Left(TimeSpan phase) => !workerBlocked
            ? phase
            : TimeSpan.FromTicks(Math.Min(
                phase.Ticks,
                Math.Max(0, (_blockedBudget - clock.Elapsed).Ticks)));
        Func<IManagedEtabsApplication, ManagedEtabsShutdownResult> shutdown =
            owned => shutdownWithin(owned, Left(_blockedBudget));

        // 1. A launch that has not reached Ready is arbitrated by the lifecycle alone, with
        //    no gate: the launching thread holds the gate for its whole duration (card W1).
        if (launch is { Began: true })
        {
            if (launch.Lifecycle == LaunchLifecycle.Committing)
            {
                // The commit is short: give it the bounded window (card X1). If it is
                // stalled, TerminateLaunch wins Committing -> Terminating below.
                _ = launch.WaitForCommitOrRelease(Left(EtabsLaunchState.CommitWait));
            }

            if (launch.Lifecycle is LaunchLifecycle.Launching or LaunchLifecycle.Committing)
            {
                var early = launch.TerminateLaunch(Left(ManagedEtabsLauncher.TerminatorExitTimeout));
                if (early is not null)
                {
                    _diagnostics.WriteLine("ℹ Shared ETABS session shut down during its launch.");
                    return early;
                }
            }
        }

        // 2. Ready, or nothing started: the gate guards the session's own fields.
        if (workerBlocked)
        {
            // Never wait on a gate a stuck command may hold for ever (card I6).
            if (!Monitor.TryEnter(_gate, Left(gateWaitOverride ?? _gateWait)))
            {
                return ShutdownThroughTheHandle(launch, Left(ManagedEtabsShutdownMachine.ForcedExitTimeout));
            }

            try
            {
                return ShutdownLocked(shutdown, launch);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        lock (_gate)
        {
            return ShutdownLocked(shutdown, launch);
        }
    }

    private ManagedEtabsShutdownResult ShutdownLocked(
        Func<IManagedEtabsApplication, ManagedEtabsShutdownResult> shutdown,
        EtabsLaunchState? launch)
    {
        if (_shutdownResult is not null)
        {
            return _shutdownResult;
        }

        if (launch is { Lifecycle: LaunchLifecycle.Ended, TerminationResult: { } prior })
        {
            // A handle-only terminator already ended this session and answered; the normal
            // path has nothing left to do and must not judge the (cleared) record.
            _shutdownResult = prior;
            _owned = null;
            return prior;
        }

        if (_owned is null)
        {
            var record = _records.Read();
            _shutdownResult = record is null
                ? NoOwnedProcessSuccess()
                : NoOwnedProcessFailure(record);
            return _shutdownResult;
        }

        // The terminal fence goes up BEFORE the shutdown acts (card W1). If a detach won the
        // arbiter in the meantime, this path acts on nothing (card C1).
        if (!MayTerminate(launch))
        {
            return AnswerForDetachDisposition(launch!) ?? DetachUncertainAnswer(launch!);
        }

        _shutdownResult = shutdown(_owned);
        if (launch is not null)
        {
            AfterShutdown(launch, _shutdownResult);
        }
        else if (_shutdownResult.Data.ProcessExitConfirmed)
        {
            _owned = null;
        }

        _diagnostics.WriteLine("ℹ Shared ETABS session shut down.");
        return _shutdownResult;
    }

    /// <summary>
    /// The gate is unavailable: act ONLY through the authoritative handle, and mutate none
    /// of the gate-guarded fields (card I6, A4). The exit is bounded, the record is cleared
    /// only after a confirmed exit, and the publication likewise.
    /// </summary>
    private ManagedEtabsShutdownResult ShutdownThroughTheHandle(EtabsLaunchState? launch, TimeSpan killWait)
    {
        if (!MayTerminate(launch))
        {
            return AnswerForDetachDisposition(launch!) ?? DetachUncertainAnswer(launch!);
        }

        var owned = Volatile.Read(ref _owned);
        var pid = _cell.Identity?.Pid;
        if (owned is null || launch is null)
        {
            return EtabsLaunchState.Failed(
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                "The session gate is held and no authoritative process handle is available.",
                pid,
                forced: false,
                recordRetained: true);
        }

        Console.Error.WriteLine(
            "⚠ The session gate is held by a stuck command; ending the exact owned ETABS " +
            $"(PID {pid}) through its authoritative handle.");
        var forced = false;
        bool confirmed;
        string? error = null;
        try
        {
            if (!owned.HasExited)
            {
                forced = true;
                owned.Kill();
            }

            confirmed = owned.HasExited || owned.WaitForExit(killWait);
        }
#pragma warning disable CA1031 // an unconfirmed exit is reported, never thrown
        catch (Exception exception)
#pragma warning restore CA1031
        {
            error = EtabsApiDiagnosticFormatter.InfrastructureException(
                "Process.Kill(authoritative-owned-handle)",
                exception);
            confirmed = SafeHasExited(owned);
        }

        if (!confirmed)
        {
            return EtabsLaunchState.Failed(
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                error ?? "The exact owned ETABS process did not confirm exit after forced termination.",
                pid,
                forced: true,
                recordRetained: _records.Read() is not null);
        }

        var retained = launch.ClearRecordAfterConfirmedExit(() => owned.HasExited);
        launch.ClearPublication();
        launch.MarkEnded();
        var answer = EtabsLaunchState.Succeeded(pid, forced, retained);
        launch.RecordTerminationResult(answer);
        return answer;
    }

    private static bool SafeHasExited(IManagedEtabsApplication owned)
    {
        try
        {
            return owned.HasExited;
        }
#pragma warning disable CA1031 // unknown means unconfirmed
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    // Set once a shutdown attempt COMPLETED with an answer (review round 2, P2-3). Disposal then
    // has nothing left to do: re-entering the shutdown would wait for a gate a stuck exit may
    // hold for ever, and the unconfirmed evidence and the recovery record must stay as they are.
    private int _releaseAttempted;

    public void Dispose()
    {
        if (Volatile.Read(ref _releaseAttempted) == 1
            || Volatile.Read(ref _launch) is { Lifecycle: LaunchLifecycle.Suppressed })
        {
            return;
        }

        // Disposal NEVER enters the unsupervised ordinary shutdown (review round 3, P2): that one
        // takes the gate without a bound and calls ExitWithoutSaving synchronously. It runs the
        // bounded, supervised sequence (a bounded gate wait, a bounded graceful exit on a
        // dedicated STA, a bounded kill of the owned handle) on its own thread under ONE overall
        // deadline. An answer that did not confirm keeps its evidence and the recovery record.
        var cleanup = new Thread(() =>
        {
            try
            {
                _ = ShutdownWith(_shutdownMachine.ShutdownWhileWorkerBlocked, workerBlocked: true);
            }
#pragma warning disable CA1031 // disposal must not throw
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.InfrastructureException(
                    "EtabsSession.Dispose", exception));
            }
        })
        {
            IsBackground = true,
            Name = "etab-cli-session-dispose"
        };
        if (OperatingSystem.IsWindows())
        {
            cleanup.SetApartmentState(ApartmentState.STA);
        }

        cleanup.Start();
        if (!cleanup.Join(DisposeBudget))
        {
            _diagnostics.WriteLine(
                $"⚠ Session disposal did not finish within {DisposeBudget.TotalSeconds:0.###}s; " +
                "the instance and its recovery record are left as they are.");
        }
    }

    /// <summary>The one overall deadline of disposal. Tests shorten it.</summary>
    internal TimeSpan DisposeBudget { get; set; } = BlockedShutdownBudget;

    private static ManagedEtabsShutdownResult NoOwnedProcessSuccess() => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: false,
            RecordRetained: false,
            ApplicationExitReturnCode: null,
            OwnedPid: null));

    private static ManagedEtabsShutdownResult NoOwnedProcessFailure(
        ManagedEtabsSessionRecord record) => new(
        false,
        ManagedEtabsShutdownErrorCodes.IdentityMismatch,
        "A managed ETABS recovery record exists, but this session has no authoritative process handle.",
        new(
            ManagedEtabsShutdownState.IdentityMismatch,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: record.Pid));
}
