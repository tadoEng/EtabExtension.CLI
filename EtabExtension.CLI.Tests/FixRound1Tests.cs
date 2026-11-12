using System.Diagnostics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>Fix round 1 (review of CLI#39): P2-2 anchor, P2-3 post-Ready blocker, P3-B, P3-D.</summary>
public sealed class AnchoredRecordLockTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "c747a-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var junction in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                    .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                    .ToList())
                {
                    Directory.Delete(junction, recursive: false);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    // P2-2: a junction ABOVE the trusted anchor (a redirected profile) is accepted.
    [Fact]
    public void AJunctionAboveTheAnchorIsAccepted()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(Path.Combine(real, "AppData"));
        var profile = Path.Combine(_root, "profile");
        Junction(profile, real);
        var anchor = Path.Combine(profile, "AppData");
        var record = Path.Combine(anchor, "etabs-session", "editing", "record.json");

        using var store = new JsonSessionRecordStore(record, Short, anchor);
        store.AcquireExclusive();

        Assert.True(store.HoldsExclusiveLock);
        store.Write(new ManagedEtabsSessionRecord(
            1, 7, DateTimeOffset.UtcNow, @"C:\ETABS\ETABS.exe", Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.True(File.Exists(Path.Combine(real, "AppData", "etabs-session", "editing", "record.json")));
    }

    // P2-2: a junction BELOW the anchor is refused.
    [Fact]
    public void AJunctionBelowTheAnchorIsRefused()
    {
        var anchor = Path.Combine(_root, "AppData");
        Directory.CreateDirectory(anchor);
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Junction(Path.Combine(anchor, "etabs-session"), elsewhere);
        var record = Path.Combine(anchor, "etabs-session", "editing", "record.json");

        using var store = new JsonSessionRecordStore(record, Short, anchor);
        var error = Assert.Throws<RecordPathUnsafeException>(store.AcquireExclusive);

        Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
        Assert.False(store.HoldsExclusiveLock);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    // A record path outside the anchor has no trusted anchor: it is checked from its root.
    [Fact]
    public void ARecordPathOutsideTheAnchorIsCheckedFromItsRoot()
    {
        var anchor = Path.Combine(_root, "AppData");
        Directory.CreateDirectory(anchor);
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_root, "link");
        Junction(link, real);

        using var store = new JsonSessionRecordStore(Path.Combine(link, "record.json"), Short, anchor);

        Assert.Throws<RecordPathUnsafeException>(store.AcquireExclusive);
    }

    [Fact]
    public void ThePlainAnchoredCaseStillLocksAndReportsTheResolvedLockPath()
    {
        var anchor = Path.Combine(_root, "AppData");
        var record = Path.Combine(anchor, "etabs-session", "analysis", "record.json");

        using var first = new JsonSessionRecordStore(record, Short, anchor);
        first.AcquireExclusive();
        using var second = new JsonSessionRecordStore(record, Short, anchor);

        Assert.Throws<RecordInUseException>(second.AcquireExclusive);
    }
}

public sealed class StartupBlockerAfterReadyTests
{
    private static readonly ManagedProcessIdentity Identity = Kit.Identity;
    private const nint Main = 0x100;

    private sealed class NoDialogs : IEtabsDialogWindowSource
    {
        public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid) => [];

        public bool TryDismiss(EtabsDialogWindow dialog) => false;
    }

    private static (EtabsModalDialogWatchdog Watchdog, LaunchPublicationCell Cell, FakeStartupWindows Windows) Build(
        bool armed = true,
        nint main = Main)
    {
        var cell = new LaunchPublicationCell();
        var state = new EtabsLaunchState(new MemoryRecordStore(), cell);
        state.Publish(Identity, lease: null);
        state.SetMainWindow(main);
        if (armed)
        {
            state.ArmStartupBlocker();
        }

        var windows = new FakeStartupWindows();
        var watchdog = new EtabsModalDialogWatchdog(
            new NoDialogs(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(2),
            cell,
            windows);
        return (watchdog, cell, windows);
    }

    // P2-3: the licence window appears AFTER Ready, during the first command: typed
    // ETABS_START_BLOCKED, not a hang.
    [Fact]
    public void ABlockerAppearingAfterReadyDuringTheFirstCommandIsTypedStartBlockedNotAHang()
    {
        var (watchdog, _, windows) = Build();
        var never = new TaskCompletionSource<object>();
        windows.Windows.Add((Identity.Pid, new StartupWindow(Main, "ETABS", "HwndWrapper")));
        var guarded = watchdog.GuardAsync(() => Identity.Pid, () => never.Task, CancellationToken.None);
        Thread.Sleep(50);
        windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));

        var result = guarded.Within("the watchdog to flag the startup blocker");

        var failure = Assert.IsType<Shared.Common.Result>(result);
        Assert.False(failure.Success);
        Assert.StartsWith($"[{EtabsLaunchErrorCodes.StartBlocked}]", failure.Error, StringComparison.Ordinal);
        Assert.Contains("Sign in", failure.Error, StringComparison.Ordinal);
        Assert.Contains(
            "detail={\"title\":\"Sign in\",\"class\":\"AuthenticationView\",\"dismissed\":false}",
            failure.Error,
            StringComparison.Ordinal);
        Assert.True(watchdog.IsBlocked);
    }

    [Fact]
    public void ABlockerThatIsPresentWhenTheWorkCompletesStillFailsTheCommand()
    {
        var (watchdog, cell, windows) = Build();
        windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));

        var result = watchdog.GuardAsync(
            () => Identity.Pid,
            () => Task.FromResult<object>(Shared.Common.Result.Ok()),
            CancellationToken.None).Within("the guarded command");

        Assert.False(Assert.IsType<Shared.Common.Result>(result).Success);
        Assert.True(cell.StartupBlockerArmed, "a blocked command must leave the watch armed");
    }

    [Fact]
    public void TheFirstCleanCommandDisarmsTheWatchSoLaterWindowsAreNotBlockers()
    {
        var (watchdog, cell, windows) = Build();
        windows.Windows.Add((Identity.Pid, new StartupWindow(Main, "ETABS", "HwndWrapper")));

        var first = watchdog.GuardAsync(
            () => Identity.Pid,
            () => Task.FromResult<object>(Shared.Common.Result.Ok()),
            CancellationToken.None).Within("the first command");
        Assert.True(Assert.IsType<Shared.Common.Result>(first).Success);
        Assert.False(cell.StartupBlockerArmed);

        windows.Windows.Add((Identity.Pid, new StartupWindow(0x300, "Tool window", "Other")));
        var second = watchdog.GuardAsync(
            () => Identity.Pid,
            () => Task.FromResult<object>(Shared.Common.Result.Ok()),
            CancellationToken.None).Within("the second command");

        Assert.True(Assert.IsType<Shared.Common.Result>(second).Success);
    }

    [Fact]
    public void NothingIsFlaggedWhileTheWatchIsNotArmedOrTheMainWindowIsUnknownOrTheWindowIsAnotherPids()
    {
        foreach (var scenario in new (bool Armed, nint Main, int Pid)[]
        {
            (false, Main, 0),
            (true, nint.Zero, 0),
            (true, Main, 1)
        })
        {
            var (watchdog, _, windows) = Build(scenario.Armed, scenario.Main);
            windows.Windows.Add((Identity.Pid + scenario.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));

            var result = watchdog.GuardAsync(
                () => Identity.Pid,
                () => Task.FromResult<object>(Shared.Common.Result.Ok()),
                CancellationToken.None).Within("the command");

            Assert.True(Assert.IsType<Shared.Common.Result>(result).Success, $"scenario {scenario}");
        }
    }

    // The session arms the watch at commit, so it is armed from Ready.
    [Fact]
    public void ACommittedSessionArmsTheStartupBlockerAndAFailedLaunchDoesNot()
    {
        var ready = new SessionRig();
        ready.Session.GetOrStartOwned();
        Assert.True(ready.L.Cell.StartupBlockerArmed);
        Assert.Equal(0x100, (int)ready.L.Cell.MainWindow);

        var failing = new SessionRig();
        failing.L.Api.HasSapModelValue = false;
        Assert.ThrowsAny<Exception>(() => failing.Session.GetOrStartOwned());
        Assert.False(failing.L.Cell.StartupBlockerArmed);
        Assert.Equal(nint.Zero, failing.L.Cell.MainWindow);
    }
}

public sealed class ExpiryBlockerEvidenceTests
{
    private static readonly ManagedProcessIdentity Identity = Kit.Identity;

    // P3-B: no main window, but a visible window of our pid -> START_BLOCKED, not TIMEOUT.
    [Fact]
    public void AnExpiryWithAVisibleWindowAndNoMainWindowAnswersStartBlockedNotTimeout()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { NoWindowPolls = int.MaxValue });
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in to ETABS", "AuthenticationView")));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.Contains("Sign in to ETABS", error.Message, StringComparison.Ordinal);
        Assert.Contains("AuthenticationView", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.True(error.Cleanup!.Success);
        Assert.Equal(0, rig.Attach.AttachCalls);
    }

    // The supervisor-timer path captures the evidence BEFORE it kills the process.
    [Fact]
    public void TheSupervisorExpiryCapturesTheBlockerBeforeTheKill()
    {
        var process = new FakeSpawnedProcess(Identity) { NoWindowPolls = int.MaxValue };
        var rig = new LauncherRig(
            new ManagedEtabsLauncherOptions
            {
                StartupDeadline = TimeSpan.FromMilliseconds(2000),
                AttachPollInterval = TimeSpan.FromMilliseconds(10),
                ForcedExitTimeout = TimeSpan.FromSeconds(5),
                GracefulCleanupWait = TimeSpan.Zero
            },
            SystemManagedEtabsClock.Instance,
            process);
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));

        var launch = Task.Run(() => Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState())));
        var error = launch.Within("the launch to fail");

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.Contains("Sign in", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpiryWithNoVisibleWindowAtAllStaysATimeoutAndIsWrittenToStderr()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { NoWindowPolls = int.MaxValue });

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.Contains(EtabsLaunchErrorCodes.StartTimeout, rig.Diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheExpiryFailureOfTheSupervisorIsWrittenToStderr()
    {
        var rig = new LauncherRig(
            new ManagedEtabsLauncherOptions
            {
                StartupDeadline = TimeSpan.FromMilliseconds(300),
                AttachPollInterval = TimeSpan.FromMilliseconds(10),
                ForcedExitTimeout = TimeSpan.FromSeconds(5),
                GracefulCleanupWait = TimeSpan.Zero
            },
            SystemManagedEtabsClock.Instance,
            new FakeSpawnedProcess(Identity) { NoWindowPolls = int.MaxValue });

        var launch = Task.Run(() => Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState())));
        _ = launch.Within("the launch to fail");

        Assert.Contains(EtabsLaunchErrorCodes.StartTimeout, rig.Diagnostics.ToString(), StringComparison.Ordinal);
    }
}

public sealed class SharedShutdownDeadlineTests
{
    // P3-D: ONE shared deadline. With production phase timeouts, a stuck exit and a kill that
    // never confirms, a 3 s budget ends the blocked shutdown at ~3 s - not 5 s + 10 s.
    [Fact]
    public void ABlockedShutdownIsHeldToOneSharedDeadlineAcrossItsPhases()
    {
        var process = new FakeSpawnedProcess(Kit.Identity) { RefusesToExit = true, WaitSleeps = true };
        var rig = new SessionRig(
            new LauncherRig(process: process),
            gateWait: TimeSpan.FromSeconds(2),
            processExitsOnApplicationExit: false,
            machine: records => new ManagedEtabsShutdownMachine(records),
            blockedBudget: TimeSpan.FromSeconds(3));
        using var neverAnswers = new ManualResetEventSlim(false);
        rig.Session.GetOrStartOwned();
        rig.L.Api.OnExit = () => neverAnswers.Wait(TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew();

        var result = Task.Run(rig.Session.ShutdownWhileWorkerBlocked)
            .Within("the budgeted blocked shutdown", TimeSpan.FromSeconds(20));
        watch.Stop();
        neverAnswers.Set();

        Assert.False(result.Success);
        Assert.False(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.RecordRetained);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}, over the shared 3 s deadline");
    }

    // A Committing stall plus the kill wait are chained phases of the SAME budget.
    [Fact]
    public void ACommittingStallAndTheKillWaitShareOneBudget()
    {
        var process = new FakeSpawnedProcess(Kit.Identity) { RefusesToExit = true, WaitSleeps = true };
        var rig = new SessionRig(
            new LauncherRig(process: process),
            processExitsOnApplicationExit: false,
            blockedBudget: TimeSpan.FromSeconds(3));
        var writing = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        rig.L.Records.OnWrite = record =>
        {
            if (record.Phase == ManagedEtabsRecordPhase.Ready)
            {
                writing.Set();
                release.Signalled("the test to release the stalled commit");
            }
        };
        var launching = rig.StartOnBackgroundThread();
        writing.Signalled("the commit to stall");
        var watch = Stopwatch.StartNew();

        var result = Task.Run(rig.Session.ShutdownWhileWorkerBlocked)
            .Within("the budgeted shutdown of a stalled commit", TimeSpan.FromSeconds(20));
        watch.Stop();
        release.Set();
        _ = launching.Within("the stalled launch");

        Assert.False(result.Success);
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(5),
            $"2 s commit wait + 10 s kill wait were chained: {watch.Elapsed}");
    }

    // The commit wait itself is capped by the shared budget: a 1 s budget cannot spend 2 s waiting.
    [Fact]
    public void TheCommitWaitIsCappedByTheSharedBudget()
    {
        var rig = new SessionRig(new LauncherRig(), blockedBudget: TimeSpan.FromSeconds(1));
        var writing = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        rig.L.Records.OnWrite = record =>
        {
            if (record.Phase == ManagedEtabsRecordPhase.Ready)
            {
                writing.Set();
                release.Signalled("the test to release the stalled commit");
            }
        };
        var launching = rig.StartOnBackgroundThread();
        writing.Signalled("the commit to stall");
        var watch = Stopwatch.StartNew();

        _ = Kit.OnDedicatedThread(rig.Session.ShutdownWhileWorkerBlocked).Within("the budgeted shutdown");
        watch.Stop();
        release.Set();
        _ = launching.Within("the stalled launch");

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1800), $"the 2 s commit wait ran over a 1 s budget: {watch.Elapsed}");
    }

    [Fact]
    public void TheSessionBudgetEqualsTheCoordinatorEnvelope()
    {
        Assert.Equal(
            EtabsSession.BlockedShutdownBudget,
            EtabExtension.CLI.Features.Serve.ServeShutdownCoordinator.BlockedShutdownEnvelope);
        Assert.Equal(TimeSpan.FromSeconds(19.5), EtabsSession.BlockedShutdownBudget);
    }
}
