using System.Diagnostics.CodeAnalysis;
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
}

public sealed class EtabsSession : IEtabsSession
{
    /// <summary>How long a blocked-worker shutdown waits for the session gate (card I6).</summary>
    public static readonly TimeSpan BlockedShutdownGateWait = TimeSpan.FromSeconds(2);

    private readonly IManagedEtabsLauncher _launcher;
    private readonly IProcessInspector _processes;
    private readonly ISessionRecordStore _records;
    private readonly IManagedEtabsShutdownMachine _shutdownMachine;
    private readonly IEtabsWorkScope _work;
    private readonly LaunchPublicationCell _cell;
    private readonly TimeSpan _gateWait;
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
        TimeSpan gateWait)
    {
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

    public ETABSApplication GetOrStart() => GetOrStartOwned().Application;

    public IManagedEtabsApplication GetOrStartOwned()
    {
        lock (_gate)
        {
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
                _ = current?.TryBeginReadyTermination();
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
            ?? state.TerminateLaunch(ManagedEtabsLauncher.ForcedExitTimeout)
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
        _ = current?.TryBeginReadyTermination();
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

            _diagnostics.WriteLine(
                $"✓ ETABS shown (PID {owned.Identity.Pid}; " +
                $"visibleOwnedWindows={windows.ObservedWindows.Count}, " +
                $"csiReturnCode={csi.ReturnCode}, state={owned.VisibilityState})");

                        return Result.Ok();
        }
    }

    public ManagedEtabsShutdownResult Shutdown() =>
        ShutdownWith(_shutdownMachine.Shutdown, workerBlocked: false);

    /// <inheritdoc />
    public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked() =>
        ShutdownWith(_shutdownMachine.ShutdownWhileWorkerBlocked, workerBlocked: true);

    private ManagedEtabsShutdownResult ShutdownWith(
        Func<IManagedEtabsApplication, ManagedEtabsShutdownResult> shutdown,
        bool workerBlocked)
    {
        var launch = Volatile.Read(ref _launch);

        // 1. A launch that has not reached Ready is arbitrated by the lifecycle alone, with
        //    no gate: the launching thread holds the gate for its whole duration (card W1).
        if (launch is { Began: true })
        {
            if (launch.Lifecycle == LaunchLifecycle.Committing)
            {
                // The commit is short: give it the bounded window (card X1). If it is
                // stalled, TerminateLaunch wins Committing -> Terminating below.
                _ = launch.WaitForCommitOrRelease(EtabsLaunchState.CommitWait);
            }

            if (launch.Lifecycle is LaunchLifecycle.Launching or LaunchLifecycle.Committing)
            {
                var early = launch.TerminateLaunch(ManagedEtabsLauncher.ForcedExitTimeout);
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
            if (!Monitor.TryEnter(_gate, _gateWait))
            {
                return ShutdownThroughTheHandle(launch);
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

        // The terminal fence goes up BEFORE the shutdown acts (card W1).
        _ = launch?.TryBeginReadyTermination();
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
    private ManagedEtabsShutdownResult ShutdownThroughTheHandle(EtabsLaunchState? launch)
    {
        _ = launch?.TryBeginReadyTermination();
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

            confirmed = owned.HasExited || owned.WaitForExit(ManagedEtabsShutdownMachine.ForcedExitTimeout);
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

    public void Dispose()
    {
        _ = Shutdown();
    }

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
