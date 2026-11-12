using System.Text.Json;
using System.Text.Json.Nodes;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Microsoft.Extensions.Configuration;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public interface IManagedEtabsLauncher
{
    /// <summary>
    /// Starts and attaches to one managed ETABS. <paramref name="state"/> is the session's
    /// shared view of this attempt: the launcher writes the provisional record and publishes
    /// the identity through it the instant <c>Process.Start</c> returns, so the watchdog
    /// watches this pid from then on and a concurrent shutdown can end the launch.
    /// </summary>
    IManagedEtabsApplication Launch(EtabsLaunchState state);
}

/// <summary>Timing of the managed start. Tests shorten it; production uses the defaults.</summary>
public sealed record ManagedEtabsLauncherOptions
{
    /// <summary>
    /// Total budget from before <c>Process.Start</c> to a committed attach (card W2). The
    /// session's own initialization after the launch is not part of it.
    /// </summary>
    public TimeSpan StartupDeadline { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the main window and the attach are retried.</summary>
    public TimeSpan AttachPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Bounded wait for our own handle to confirm exit after a kill.</summary>
    public TimeSpan ForcedExitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the TERMINATOR (a shutdown, the supervisor or a failed start) waits for the
    /// startup worker's kill to be confirmed (review P3-F). It is one second longer than the
    /// worker's own <see cref="ForcedExitTimeout"/>, so a kill the worker confirms right at its
    /// limit is never reported unconfirmed by a terminator that gave up a moment earlier.
    /// </summary>
    public TimeSpan TerminatorExitTimeout => ForcedExitTimeout + ManagedEtabsLauncher.TerminatorMargin;

    /// <summary>Bounded wait after a raw <c>ApplicationExit</c> request before the kill.</summary>
    public TimeSpan GracefulCleanupWait { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Starts and takes exclusive ownership of one managed ETABS instance by CONSTRUCTION
/// (card v3): this daemon creates the process with <c>Process.Start</c>, the returned
/// handle is the only authority, and <c>cHelper.GetObjectProcess</c> attaches to that exact
/// pid once its main window exists (card Z1).
///
/// <para>There is no census-based attribution and no census-based kill. The census stays
/// only for classification and reporting (the preflight refusal). Whatever fails, only the
/// handle this launch created is ever killed, and a record is cleared only after that exit
/// is confirmed.</para>
///
/// <para>Sequence: preflight census (no external or ambiguous instance) →
/// <c>Process.Start</c> (handle into the slot) → identity from the handle → provisional
/// record → identity published → window guard armed → wait for the main window, failing
/// typed on a startup-blocking window → <c>GetObjectProcess(pid)</c> → hide → SapModel.
/// <c>cOAPI.ApplicationStart</c> is never called: the L5 live run proved the attached object
/// is already started and that the call returns -1.</para>
/// </summary>
public sealed class ManagedEtabsLauncher : IManagedEtabsLauncher
{
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The terminator's extra second over the worker's kill wait (review P3-F).</summary>
    public static readonly TimeSpan TerminatorMargin = TimeSpan.FromSeconds(1);

    /// <summary>The terminator's kill-confirmation wait: 11 s (review P3-F).</summary>
    public static readonly TimeSpan TerminatorExitTimeout = ForcedExitTimeout + TerminatorMargin;

    private static readonly JsonSerializerOptions DetailJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IProcessInspector _processes;
    private readonly IEtabsExecutableResolver _executableResolver;
    private readonly IEtabsRawApiFactory _apiFactory;
    private readonly IEtabsVersionProbe _versionProbe;
    private readonly TextWriter _diagnostics;
    private readonly IManagedEtabsWindowGuardFactory _windowGuards;
    private readonly IManagedEtabsClock _clock;
    private readonly IEtabsProcessSpawner _spawner;
    private readonly IEtabsStartupWindowSource _startupWindows;
    private readonly ManagedEtabsLauncherOptions _options;

    public ManagedEtabsLauncher(
        IProcessInspector processes,
        IConfiguration configuration) : this(
            processes,
            new EtabsExecutableResolver(configuration, new WindowsEtabsInstallDiscovery()),
            new EtabsRawApiFactory(),
            new FileEtabsVersionProbe(),
            Console.Error,
            WindowsManagedEtabsWindowGuardFactory.Instance,
            SystemManagedEtabsClock.Instance,
            new WindowsEtabsProcessSpawner(),
            PlatformWindows(),
            new ManagedEtabsLauncherOptions())
    {
    }

    public ManagedEtabsLauncher(
        IProcessInspector processes,
        IEtabsExecutableResolver executableResolver,
        IEtabsRawApiFactory apiFactory,
        IEtabsVersionProbe versionProbe,
        TextWriter diagnostics,
        IManagedEtabsWindowGuardFactory windowGuards,
        IManagedEtabsClock clock,
        IEtabsProcessSpawner spawner,
        IEtabsStartupWindowSource startupWindows,
        ManagedEtabsLauncherOptions options)
    {
        _processes = processes;
        _executableResolver = executableResolver;
        _apiFactory = apiFactory;
        _versionProbe = versionProbe;
        _diagnostics = diagnostics;
        _windowGuards = windowGuards;
        _clock = clock;
        _spawner = spawner;
        _startupWindows = startupWindows;
        _options = options;
    }

    private static Win32EtabsDialogWindowSource PlatformWindows() => OperatingSystem.IsWindows()
        ? new Win32EtabsDialogWindowSource()
        : throw new PlatformNotSupportedException("The ETABS launcher requires Windows.");

    public IManagedEtabsApplication Launch(EtabsLaunchState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // StartupDeadline starts BEFORE Process.Start (card W2).
        var startedAt = _clock.Timestamp;
        CaptureCrossCheckBaseline();
        var executablePath = _executableResolver.Resolve();

        // The supervisor is a real-time timer, independent of the (possibly blocked) calling
        // thread: when the deadline passes it wins Terminating and kills OUR handle, which
        // is also what releases a COM call stuck on a half-initialised instance.
        using var supervisor = new Timer(
            _ => ExpireStartup(state),
            null,
            _options.StartupDeadline,
            Timeout.InfiniteTimeSpan);

        IEtabsRawApi? rawApi = null;
        IManagedEtabsWindowGuard? windowGuard = null;
        HandleLease? mine = null;
        try
        {
            var started = StartProcess(state, executablePath, startedAt);
            mine = started.Lease;
            var identity = started.Identity;

            // The process exists and is ours: hold its windows down from here.
            windowGuard = ActivateWindowGuard(new LeasedOwnedProcess(started.Lease, identity));
            windowGuard.MarkStage("cHelper.GetObjectProcess");

            rawApi = Attach(state, started, startedAt);

            // CSI mutates. Unconditionally, exactly once, and a throw is fatal.
            AskCsiToHide(rawApi);

            // Windows certifies AND the startup-consent interval closes, in one
            // indivisible step (CLI #24).
            RequireWindowsSuppressed(windowGuard, "after cHelper.GetObjectProcess");
            RequireNoStartupBlocker(state, identity.Pid);
            RequireStillLaunching(state);

            RequireSapModel(rawApi);

            var version = ReadVersion(identity.ExecutablePath);
            var managed = new ManagedEtabsApplication(
                rawApi,
                identity,
                state.LaunchRecordId,
                started.Lease,
                windowGuard,
                new ManagedEtabsApiVersion(
                    version.MajorVersion,
                    ReadApiVersion(rawApi),
                    version.FullVersion));
            windowGuard = null; // the guard's lifetime moved to the managed application
            return managed;
        }
        catch (Exception failure)
        {
            windowGuard?.Dispose();
            var timedOut = state.TimedOut
                || failure is EtabsLaunchException { Code: EtabsLaunchErrorCodes.StartTimeout };
            var cleanup = failure is EtabsLaunchException { Cleanup: not null } carried
                ? carried.Cleanup
                : CleanUpFailedStart(state, rawApi, timedOut);

            var code = timedOut
                ? (state.BlockerEvidence is not null
                    ? EtabsLaunchErrorCodes.StartBlocked
                    : EtabsLaunchErrorCodes.StartTimeout)
                : failure is EtabsLaunchException typed
                    ? typed.Code
                    : EtabsLaunchErrorCodes.LaunchFailed;
            var message = failure is EtabsLaunchException original
                ? StripCode(original)
                : failure.Message;

            string FactsOf(string text) => cleanup is { Success: true }
                ? text
                : EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    text,
                    $"cleanup={cleanup?.Data.State}; " +
                    $"processExitConfirmed={cleanup?.Data.ProcessExitConfirmed}; " +
                    $"forced={cleanup?.Data.Forced}; " +
                    $"recordRetained={cleanup?.Data.RecordRetained}; " +
                    $"ownedPid={cleanup?.Data.OwnedPid?.ToString() ?? "unknown"}");

            string finalMessage;
            if (code is EtabsLaunchErrorCodes.StartBlocked or EtabsLaunchErrorCodes.StartTimeout)
            {
                // Card C4: the typed detail is ONE json object that runs to the end of the
                // string, and it says whether this launch's own process is gone and whether a
                // record is still retained, so the host can tell "retry" from "reconcile".
                var (prose, detail) = SplitDetail(message);
                finalMessage = FactsOf(prose)
                    + EtabsTypedFailureCodes.DetailMarker
                    + StartDetail(code, detail, state.BlockerEvidence, cleanup).ToJsonString(DetailJson);
            }
            else
            {
                finalMessage = FactsOf(message);
            }

            throw new EtabsLaunchException(code, finalMessage, failure.InnerException ?? failure)
            {
                Cleanup = cleanup
            };
        }
        finally
        {
            mine?.Release();
        }
    }

    // ── start (card W2, X2, Y2) ─────────────────────────────────────────────

    private sealed record StartedProcess(
        ManagedProcessIdentity Identity,
        HandleLease Lease,
        ISpawnedEtabsProcess Process);

    private StartedProcess StartProcess(
        EtabsLaunchState state,
        string executablePath,
        long startedAt)
    {
        state.MarkWorkerStarted();
        var worker = Task.Run(() => StartWorker(state, executablePath));
        var remaining = _options.StartupDeadline - _clock.ElapsedSince(startedAt);
        // WaitAny, not Wait: a faulted worker must surface its own typed exception below,
        // not an AggregateException.
        if (Task.WaitAny([worker], remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) < 0)
        {
            // Process.Start, the identity read or the provisional write is still running.
            // A late handle is the worker's to kill (Y2); a handle already in the slot is
            // taken and killed here.
            throw StartTimeout(state);
        }

        StartedProcess? started;
        try
        {
            started = worker.GetAwaiter().GetResult();
        }
        catch (EtabsLaunchException)
        {
            throw;
        }

        return started ?? throw AbortedOrTimedOut(state);
    }

    private StartedProcess? StartWorker(EtabsLaunchState state, string executablePath)
    {
        ISpawnedEtabsProcess process;
        try
        {
            process = _spawner.Start(executablePath);
        }
        catch (Exception ex) when (ex is not EtabsLaunchException)
        {
            state.ReportWorkerOutcome(EtabsLaunchState.WorkerOutcome.NoProcess);
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ProcessStartFailed,
                EtabsApiDiagnosticFormatter.InfrastructureException("Process.Start", ex),
                ex);
        }

        var lease = new HandleLease(process);
        _ = lease.TryAcquire(); // the worker's own reference
        if (!state.TryStoreHandle(lease))
        {
            // CLOSED: a supervisor or shutdown already took the launch. This worker owns
            // the cleanup of the handle it just created, and publishes nothing.
            var confirmed = KillOwnHandle(lease);
            if (!confirmed)
            {
                // The kill is not confirmed: an unrecorded orphan must not be possible, so
                // the creation proof (the provisional record) is written now (card P2-1).
                try
                {
                    state.WriteProvisionalRecord(process.ReadIdentity());
                }
#pragma warning disable CA1031 // best effort: the unconfirmed outcome is reported either way
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.InfrastructureException(
                        "ManagedEtabsSessionRecord.Write(provisional, late unconfirmed kill)",
                        ex));
                }
            }
            else
            {
                lease.ReleaseOwner();
            }

            lease.Release();
            state.ReportWorkerOutcome(confirmed
                ? EtabsLaunchState.WorkerOutcome.KilledConfirmed
                : EtabsLaunchState.WorkerOutcome.KillUnconfirmed);
            return null;
        }

        ManagedProcessIdentity identity;
        try
        {
            identity = process.ReadIdentity();
        }
        catch (Exception ex) when (ex is not EtabsLaunchException)
        {
            lease.Release();
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ProcessIdentityFailed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "Process identity from the Process.Start handle",
                    ex),
                ex);
        }

        if (state.Lifecycle != LaunchLifecycle.Launching)
        {
            lease.Release();
            return null;
        }

        try
        {
            state.WriteProvisionalRecord(identity);
        }
        catch (Exception ex) when (ex is not EtabsLaunchException)
        {
            lease.Release();
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.RecoveryRecordWriteFailed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "ManagedEtabsSessionRecord.Write(provisional)",
                    ex),
                ex);
        }

        if (state.Lifecycle != LaunchLifecycle.Launching)
        {
            // A late record write after the launch was taken: the terminator owns the kill,
            // and this record is cleared only once that exit is confirmed (card Y1).
            _ = lease.WaitForExit(_options.ForcedExitTimeout);
            _ = state.ClearRecordAfterConfirmedExit(lease);
            lease.Release();
            return null;
        }

        state.Publish(identity, lease);
        _diagnostics.WriteLine(
            $"ℹ ETABS process started and recorded (PID {identity.Pid}); attaching once its window exists.");
        return new StartedProcess(identity, lease, process);
    }

    private bool KillOwnHandle(HandleLease lease)
    {
        try
        {
            lease.Kill();
            return lease.WaitForExit(_options.ForcedExitTimeout) || lease.ExitConfirmed;
        }
#pragma warning disable CA1031 // an unconfirmed exit leaves nothing cleared and nothing guessed
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.InfrastructureException(
                "Process.Kill(own late handle)",
                ex));
            return false;
        }
    }

    /// <summary>
    /// The supervisor's expiry (card W2, X2, Y3). A launch still Launching is taken; one in a
    /// short Committing window is given the bounded commit wait and then taken if stalled;
    /// one already Ready, or already ended, is left alone - it finished in time.
    /// </summary>
    internal void ExpireStartup(EtabsLaunchState state)
    {
        try
        {
            var current = state.Lifecycle;
            if (current is LaunchLifecycle.Ready or LaunchLifecycle.Ended or LaunchLifecycle.Terminating)
            {
                return;
            }

            if (current == LaunchLifecycle.Committing
                && state.WaitForCommitOrRelease(EtabsLaunchState.CommitWait))
            {
                return;
            }

            CaptureBlockerWithoutMainWindow(state);
            _diagnostics.WriteLine(
                $"⚠ {EtabsLaunchErrorCodes.StartTimeout}: the ETABS start did not commit within " +
                $"{_options.StartupDeadline.TotalSeconds:0.###}s; ending the process this attempt created.");
            _ = state.TerminateLaunch(
                _options.TerminatorExitTimeout,
                TimeSpan.Zero,
                timedOut: true);
        }
#pragma warning disable CA1031 // a timer callback must never throw
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.InfrastructureException(
                "ManagedEtabsLauncher.ExpireStartup",
                ex));
        }
    }

    /// <summary>
    /// Before an expiry kills the process, remember a visible window of it that held the
    /// startup while no main window existed (card P3-B): after the kill the evidence is gone,
    /// and the engineer needs "sign in" rather than "timeout".
    /// </summary>
    private void CaptureBlockerWithoutMainWindow(EtabsLaunchState state)
    {
        if (state.BlockerEvidence is not null
            || state.Publication.Identity is not { } identity)
        {
            return;
        }

        try
        {
            // The same narrowed rule (card v2.5a A3): only a licence/auth window, or a disabled
            // known main window with another visible window, counts as what held the startup.
            var assessment = EtabsStartupBlockerRule.Assess(
                _startupWindows.VisibleTopLevelWindows(identity.Pid),
                state.MainWindow,
                main => _startupWindows.IsLiveWindowOf(main, identity.Pid),
                _startupWindows.IsWindowEnabled);
            if (assessment.Blocker is { } blocker)
            {
                state.RecordBlockerEvidence(new StartupBlockerEvidence(blocker.Title, blocker.ClassName));
            }
        }
#pragma warning disable CA1031 // evidence only: a failed scan changes nothing
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _diagnostics.WriteLine($"⚠ ETABS startup window scan at expiry failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Splits a message into its prose and its trailing <c> detail={json}</c> object. Text after
    /// the first complete object (a diagnostic appended later) stays with the prose.
    /// </summary>
    private static (string Prose, JsonObject? Detail) SplitDetail(string message)
    {
        var marker = EtabsTypedFailureCodes.DetailMarker;
        var at = message.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return (message, null);
        }

        var start = at + marker.Length;
        var depth = 0;
        var inString = false;
        for (var index = start; index < message.Length; index++)
        {
            var c = message[index];
            if (inString)
            {
                if (c == '\\')
                {
                    index++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                var json = message[start..(index + 1)];
                var rest = message[(index + 1)..];
                try
                {
                    return (message[..at] + rest, JsonNode.Parse(json) as JsonObject);
                }
                catch (JsonException)
                {
                    return (message[..at] + rest, null);
                }
            }
        }

        return (message[..at], null);
    }

    private static JsonObject StartDetail(
        string code,
        JsonObject? existing,
        StartupBlockerEvidence? evidence,
        ManagedEtabsShutdownResult? cleanup)
    {
        var detail = existing ?? new JsonObject();
        if (code == EtabsLaunchErrorCodes.StartBlocked)
        {
            if (!detail.ContainsKey("title") && evidence is not null)
            {
                detail["title"] = EtabsApiDiagnosticFormatter.Bounded(evidence.Title);
                detail["class"] = EtabsApiDiagnosticFormatter.Bounded(evidence.ClassName);
            }

            detail["dismissed"] = false;
            detail["phase"] = StartBlockerPhases.Launch;
        }

        // An unknown cleanup is never read as confirmed: the process may still be alive.
        detail["ownedExitConfirmed"] = cleanup?.Data.ProcessExitConfirmed ?? false;
        detail["recordRetained"] = cleanup?.Data.RecordRetained ?? true;
        return detail;
    }

    private static EtabsLaunchException StartBlocked(string title, string windowClass)
    {
        var boundedTitle = EtabsApiDiagnosticFormatter.Bounded(title);
        var boundedClass = EtabsApiDiagnosticFormatter.Bounded(windowClass);
        var detail = JsonSerializer.Serialize(
            new { title = boundedTitle, @class = boundedClass, dismissed = false },
            DetailJson);
        return new EtabsLaunchException(
            EtabsLaunchErrorCodes.StartBlocked,
            $"ETABS is waiting on a window it cannot continue past: title='{boundedTitle}', " +
            $"class='{boundedClass}'. It was left alone and the process this attempt created was " +
            $"ended. The engineer must act (for example sign in to the ETABS licence).{EtabsTypedFailureCodes.DetailMarker}{detail}");
    }

    private EtabsLaunchException StartTimeout(EtabsLaunchState state)
    {
        CaptureBlockerWithoutMainWindow(state);
        _diagnostics.WriteLine(
            $"⚠ {EtabsLaunchErrorCodes.StartTimeout}: the ETABS start did not commit within " +
            $"{_options.StartupDeadline.TotalSeconds:0.###}s.");
        var cleanup = state.TerminateLaunch(
            _options.TerminatorExitTimeout,
            TimeSpan.Zero,
            timedOut: true);
        if (state.BlockerEvidence is { } evidence)
        {
            var blocked = StartBlocked(evidence.Title, evidence.ClassName);
            return new EtabsLaunchException(blocked.Code, StripCode(blocked))
            {
                Cleanup = cleanup ?? EtabsLaunchState.Succeeded(null, forced: false, recordRetained: false)
            };
        }

        return new EtabsLaunchException(
            EtabsLaunchErrorCodes.StartTimeout,
            "ETABS did not become ready within " +
            $"{_options.StartupDeadline.TotalSeconds:0.###}s of starting; the process this " +
            "attempt created was ended and nothing else was touched. Retry.")
        {
            Cleanup = cleanup ?? EtabsLaunchState.Succeeded(null, forced: false, recordRetained: false)
        };
    }

    private EtabsLaunchException AbortedOrTimedOut(EtabsLaunchState state) =>
        state.TimedOut
            ? StartTimeout(state)
            : new EtabsLaunchException(
                EtabsLaunchErrorCodes.LaunchAborted,
                "The ETABS launch was ended by a shutdown before it committed.")
            {
                Cleanup = state.TerminateLaunch(_options.TerminatorExitTimeout)
            };

    private void RequireStillLaunching(EtabsLaunchState state)
    {
        if (state.Lifecycle != LaunchLifecycle.Launching)
        {
            throw AbortedOrTimedOut(state);
        }
    }

    // ── attach (card Z1, Z2) ────────────────────────────────────────────────

    /// <summary>
    /// The attach loop. KNOWN RESIDUAL (card P3-A): <c>GetObjectProcess</c> runs on the
    /// session STA thread because the RCW is apartment-bound, and the startup supervisor can
    /// only release a hung call by ending the process. If that process cannot be killed
    /// (an unkillable ETABS) the STA stays inside the call: the launch never returns, the
    /// expiry is written to stderr, the answer to the request is unconfirmed, and the
    /// blocked-shutdown path (a dedicated thread, never the worker) takes over.
    /// </summary>
    private IEtabsRawApi Attach(EtabsLaunchState state, StartedProcess started, long startedAt)
    {
        Exception? lastFailure = null;
        while (true)
        {
            RequireStillLaunching(state);
            if (_clock.ElapsedSince(startedAt) >= _options.StartupDeadline)
            {
                throw StartTimeoutWithFailure(state, lastFailure);
            }

            if (started.Process.HasExited)
            {
                throw new EtabsLaunchException(
                    EtabsLaunchErrorCodes.ApiObjectCreationFailed,
                    $"The started ETABS process (pid={started.Identity.Pid}) exited before it could be attached.");
            }

            // Card v2.5a A3: a licence/sign-in window blocks AT ONCE, the moment it is seen - even
            // before any main window exists - and the main window is captured by the rule, never
            // from Process.MainWindowHandle (the licence dialog became that on a live run).
            RequireNoStartupBlocker(state, started.Identity.Pid);

            // Z1: never attach to a half-initialised instance. Until the main window
            // exists, GetObjectProcess hands back an object whose OpenFile never returns.
            var main = started.Process.RefreshMainWindowHandle();
            if (main != nint.Zero)
            {
                try
                {
                    var api = _apiFactory.TryAttach(started.Identity.Pid);
                    if (api is not null)
                    {
                        // A late attach result after the fence is DISCARDED: no hide, no
                        // publish, no record write (card V1.4).
                        RequireStillLaunching(state);
                        return api;
                    }
                }
#pragma warning disable CA1031 // a not-yet-attachable instance is retried inside the deadline
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    lastFailure = ex;
                }
            }

            _clock.Wait(_options.AttachPollInterval);
        }
    }

    private EtabsLaunchException StartTimeoutWithFailure(EtabsLaunchState state, Exception? lastFailure)
    {
        var timeout = StartTimeout(state);
        return lastFailure is null
            ? timeout
            : new EtabsLaunchException(
                EtabsLaunchErrorCodes.StartTimeout,
                StripCode(timeout) + " " + EtabsApiDiagnosticFormatter.Exception(
                    "cHelper.GetObjectProcess",
                    lastFailure),
                lastFailure)
            {
                Cleanup = timeout.Cleanup
            };
    }

    /// <summary>
    /// The startup-blocker rule of card v2.5a A3, applied between <c>Process.Start</c> and Ready:
    /// a visible licence/auth window, or a KNOWN main window that is disabled while another
    /// visible window of the pid exists (<see cref="EtabsStartupBlockerRule"/>). The untitled
    /// splash before the main window exists is not a blocker. The failure is typed, nothing is
    /// dismissed (a sign-in needs the user), and a window of another pid is never considered.
    /// The captured main window is kept in <paramref name="state"/>.
    /// </summary>
    private void RequireNoStartupBlocker(EtabsLaunchState state, int pid)
    {
        StartupBlockerAssessment assessment;
        try
        {
            assessment = EtabsStartupBlockerRule.Assess(
                _startupWindows.VisibleTopLevelWindows(pid),
                state.MainWindow,
                main => _startupWindows.IsLiveWindowOf(main, pid),
                _startupWindows.IsWindowEnabled);
        }
#pragma warning disable CA1031 // no evidence either way: the deadline still bounds the start
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _diagnostics.WriteLine($"⚠ ETABS startup window scan failed: {ex.Message}");
            return;
        }

        state.SetMainWindow(assessment.Main);
        if (assessment.Blocker is { } blocker)
        {
            throw StartBlocked(blocker.Title, blocker.ClassName);
        }
    }

    // ── unchanged lifecycle steps ───────────────────────────────────────────

    /// <summary>
    /// Activates Windows-level observation of the exact owned process's windows, for the
    /// whole of the background life of the session it is about to create. Not defended
    /// against failure on purpose: an exception here fails the launch.
    /// </summary>
    private IManagedEtabsWindowGuard ActivateWindowGuard(LeasedOwnedProcess ownedProcess)
    {
        IManagedEtabsWindowGuard guard;
        try
        {
            guard = _windowGuards.Activate(ownedProcess);
        }
        catch (Exception exception) when (exception is not EtabsLaunchException)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.WindowSuppressionUnavailable,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "IManagedEtabsWindowGuardFactory.Activate",
                    exception),
                exception);
        }

        _diagnostics.WriteLine(
            "ℹ ETABS window suppression armed for owned " +
            $"pid={ownedProcess.Identity.Pid} (released only by an explicit open-model).");
        return guard;
    }

    /// <summary>
    /// Asks CSI to take the application it just attached to off screen. Once,
    /// unconditionally; a throw is fatal, a non-zero return is recorded and the Windows
    /// census decides.
    /// </summary>
    private void AskCsiToHide(IEtabsRawApi rawApi)
    {
        var outcome = ManagedEtabsVisibility.ApplyHidden(rawApi);
        if (!outcome.Issued)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    outcome.Diagnostic ?? "cOAPI.Hide could not be issued.",
                    "stage=after cHelper.GetObjectProcess; csiTransitionIssued=false"));
        }

        _diagnostics.WriteLine(outcome.Diagnostic is null
            ? $"ℹ cOAPI.Hide issued (returnCode={outcome.ReturnCode})."
            : $"ℹ cOAPI.Hide issued (returnCode={outcome.ReturnCode}); " +
                $"Windows state is the authority. {outcome.Diagnostic}");
    }

    private void RequireWindowsSuppressed(IManagedEtabsWindowGuard guard, string stage)
    {
        var confirmation = guard.ConfirmSuppressedAndCloseConsentInterval();
        if (!confirmation.Confirmed)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    confirmation.Diagnostic
                        ?? "Managed ETABS window suppression could not be confirmed.",
                    $"stage={stage}"));
        }

        _diagnostics.WriteLine(
            $"✓ ETABS background UI suppression confirmed {stage} " +
            $"(observations={confirmation.Observations}, " +
            $"waitedMs={(long)confirmation.Waited.TotalMilliseconds}).");
    }

    private static void RequireSapModel(IEtabsRawApi rawApi)
    {
        bool available;
        try
        {
            available = rawApi.HasSapModel;
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiModelUnavailable,
                EtabsApiDiagnosticFormatter.Exception("cOAPI.SapModel", ex),
                ex);
        }

        if (!available)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiModelUnavailable,
                "cOAPI.SapModel was unavailable on the attached application object.");
        }
    }

    private (int MajorVersion, string FullVersion) ReadVersion(string executablePath)
    {
        try
        {
            return _versionProbe.Read(executablePath);
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ProcessIdentityFailed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "FileVersionInfo.GetVersionInfo(ETABS.exe)",
                    ex),
                ex);
        }
    }

    private double ReadApiVersion(IEtabsRawApi rawApi)
    {
        try
        {
            return rawApi.GetOapiVersionNumber();
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                EtabsApiDiagnosticFormatter.Exception("cHelper.GetOAPIVersionNumber", ex));
            return 0;
        }
    }

    /// <summary>
    /// Classification and refusal only: an external or ambiguous ETABS already running means
    /// this start is refused. The census never decides what to terminate.
    /// </summary>
    private void CaptureCrossCheckBaseline()
    {
        EtabsProcessObservation observation;
        try
        {
            observation = _processes.ObserveEtabs();
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance,
                $"Could not verify that ETABS is absent before launch: {ex.Message}",
                ex);
        }

        var nonSibling = observation.NonSibling;
        if (nonSibling.Count > 0 || observation.UnidentifiedCount > 0)
        {
            var pids = nonSibling
                .Select(identity => identity.Pid)
                .Distinct()
                .Order()
                .ToList();
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance,
                "Refusing to start managed ETABS because an external or ambiguous " +
                $"instance already exists: observedPids=[{string.Join(", ", pids)}], " +
                $"unidentifiedCount={observation.UnidentifiedCount}.");
        }
    }

    private static string StripCode(EtabsLaunchException failure)
    {
        var prefix = $"[{failure.Code}] ";
        return failure.Message.StartsWith(prefix, StringComparison.Ordinal)
            ? failure.Message[prefix.Length..]
            : failure.Message;
    }

    /// <summary>
    /// A failed start ends the launch through the shared state: our own handle only, the
    /// raw exit requested first when an object exists, the record cleared after a confirmed
    /// exit and otherwise retained.
    /// </summary>
    private ManagedEtabsShutdownResult CleanUpFailedStart(
        EtabsLaunchState state,
        IEtabsRawApi? rawApi,
        bool timedOut)
    {
        var applicationExitReturnCode = RequestRawExit(rawApi);
        var graceful = applicationExitReturnCode == 0
            ? _options.GracefulCleanupWait
            : TimeSpan.Zero;
        var result = state.TerminateLaunch(_options.TerminatorExitTimeout, graceful, timedOut)
            ?? EtabsLaunchState.Succeeded(null, forced: false, recordRetained: false);
        var reported = result with
        {
            Data = result.Data with { ApplicationExitReturnCode = applicationExitReturnCode }
        };
        if (!reported.Success)
        {
            _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                reported.Error ?? "Managed ETABS start failed and its process exit is unconfirmed.",
                $"code={reported.ErrorCode}; state={reported.Data.State}; forced={reported.Data.Forced}"));
        }

        return reported;
    }

    /// <summary>Requests the raw exit when an object exists; a missing object is not an error.</summary>
    private int? RequestRawExit(IEtabsRawApi? rawApi)
    {
        if (rawApi is null)
        {
            return null;
        }

        try
        {
            var returnCode = rawApi.ApplicationExit(false);
            if (returnCode != 0)
            {
                _diagnostics.WriteLine(
                    EtabsApiDiagnosticFormatter.ApiReturn("cOAPI.ApplicationExit(false)", returnCode));
            }

            return returnCode;
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                EtabsApiDiagnosticFormatter.Exception("cOAPI.ApplicationExit(false)", ex));
            return null;
        }
    }
}
