using System.Runtime.Versioning;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

// The managed startup contract (CLI #747, card v3), exercised end to end without ETABS or COM.
//
// Ownership by construction: preflight census (refusal only) -> Process.Start (the returned
// handle is THE authority, stored in the handle slot) -> identity from the handle ->
// provisional record -> identity published -> window guard armed -> wait for the main window
// (Z1) -> cHelper.GetObjectProcess(pid) -> hide -> SapModel. There is no census attribution,
// no ApplicationStart (the L5 live run proved it is not needed), and nothing is ever killed
// except the handle this launch created.
public sealed class ManagedEtabsLauncherTests
{
    private static readonly ManagedProcessIdentity Identity = Kit.Identity;

    [Fact]
    public void LaunchStartsRecordsAttachesAndHoldsOneAuthoritativeHandle()
    {
        var rig = new LauncherRig();
        var state = rig.NewState();

        var managed = rig.Launcher.Launch(state);

        Assert.Equal(Identity, managed.Identity);
        Assert.Equal(state.LaunchRecordId, managed.ManagedLaunchRecordId);
        Assert.Equal(
            [
                "census-preflight",
                "process-start",
                // Immediately after Process.Start, before anything can hang: the evidence.
                "record-write:Provisional",
                "window-guard-activate",
                // Only once the main window exists (Z1), and with NO ApplicationStart.
                "attach",
                // CSI mutates. The hide is issued UNCONDITIONALLY.
                "hide",
                "visible",
                // THE gate: the exact-owned Windows census.
                "window-guard-confirm-suppressed",
                "window-guard-enter-background-hidden",
                "sap-model",
                "version-probe",
                "oapi-version"
            ],
            rig.Events);

        Assert.Equal(Identity.Pid, rig.Attach.AttachedPid);
        Assert.Equal(0, rig.Api.InitializeCount);
        Assert.Equal(0, rig.Api.WrapCount);
        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Equal(0, rig.Process.KillCount);

        // Identity is published; commit is the session's job, so not Ready yet.
        Assert.Equal(Identity, rig.Cell.Identity);
        Assert.Equal(LaunchLifecycle.Launching, state.Lifecycle);
    }

    // Test 2 of the card: the provisional record exists BEFORE the attach returns, and the
    // final record reuses its id.
    [Fact]
    public void ProvisionalRecordExistsBeforeTheAttachReturnsWithTheFinalRecordId()
    {
        var rig = new LauncherRig();
        var state = rig.NewState();
        ManagedEtabsSessionRecord? seenAtAttach = null;
        rig.Attach.OnAttach = () => seenAtAttach = rig.Records.Record;

        var managed = rig.Launcher.Launch(state);

        Assert.NotNull(seenAtAttach);
        Assert.Equal(ManagedEtabsRecordPhase.Provisional, seenAtAttach!.Phase);
        Assert.Equal(Identity.Pid, seenAtAttach.Pid);
        Assert.Equal(Identity.ProcessStartTimeUtc, seenAtAttach.ProcessStartTimeUtc);
        Assert.Equal(Identity.ExecutablePath, seenAtAttach.ExecutablePath);
        Assert.Equal(managed.ManagedLaunchRecordId, seenAtAttach.ManagedLaunchRecordId);
        Assert.Equal(state.LaunchRecordId, seenAtAttach.ManagedLaunchRecordId);
    }

    [Fact]
    public void LaunchWrapsTheSameObjectItAttachedToRatherThanReattaching()
    {
        var rig = new LauncherRig();

        var managed = rig.Launcher.Launch(rig.NewState());
        managed.CompleteApiReadiness();

        Assert.Equal(1, rig.Api.WrapCount);
        Assert.Same(rig.Api, rig.Api.WrappedInstance);
        Assert.Equal(23, rig.Api.WrappedMajorVersion);
        Assert.Equal("23.3.0", rig.Api.WrappedFullVersion);
        Assert.Equal(2.16, rig.Api.WrappedApiVersion);
        Assert.Equal(1, rig.Attach.AttachCalls);
    }

    [Fact]
    public void ProcessStartFailingFailsTypedAndTouchesNothing()
    {
        var rig = new LauncherRig();
        rig.Spawner.Failure = new InvalidOperationException("no such file");

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.ProcessStartFailed, error.Code);
        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Equal(0, rig.Process.KillCount);
        Assert.Empty(rig.Records.Writes);
        Assert.True(error.Cleanup!.Success);
    }

    // The card's FailedAttachKillsOnlyOurOwnHandle: the attach never works, the deadline
    // passes, and the only termination is of the handle this launch created.
    [Fact]
    public void FailedAttachKillsOnlyOurOwnHandle()
    {
        var rig = new LauncherRig();
        rig.Attach.Failure = new InvalidOperationException("RPC server unavailable");
        var state = rig.NewState();

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(state));

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Equal(0, rig.Api.ExitCount);
        Assert.True(error.Cleanup!.Success);
        Assert.True(error.Cleanup.Data.ProcessExitConfirmed);
        Assert.Equal(Identity.Pid, error.Cleanup.Data.OwnedPid);

        // The record is cleared only because the exit was confirmed (Y1), generation-matched.
        Assert.Null(rig.Records.Record);
        Assert.Equal(1, rig.Records.ClearCount);
        Assert.Null(rig.Cell.Identity);
        Assert.Contains("RPC server unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSapModelFailsTypedAndStopsTheProcessItAlreadyOwns()
    {
        var rig = new LauncherRig();
        rig.Api.HasSapModelValue = false;

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.ApiModelUnavailable, error.Code);

        // The raw exit is requested first, then the handle is killed as the bounded last resort.
        Assert.Equal(1, rig.Api.ExitCount);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.True(rig.Events.IndexOf("application-exit-save=False") > rig.Events.IndexOf("sap-model"));
        Assert.True(rig.Guards.Activated[0].Disposed);
        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Null(rig.Records.Record);
    }

    [Fact]
    public void ExternalInstanceBeforeLaunchFailsClosedWithoutStartingAnything()
    {
        var rig = new LauncherRig();
        rig.Census.Preflight = [Identity with { Pid = 99 }];

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(["census-preflight"], rig.Events);
        Assert.Equal(0, rig.Spawner.StartCount);
        Assert.Empty(rig.Guards.Activated);
    }

    // Card A1 / V7: a survivor found by census is NEVER killed. An external ETABS that
    // appears while our own start fails is untouched, and the census is not even consulted
    // again.
    [Fact]
    public void AnExternalEtabsAppearingDuringOurFailedStartIsNeverTouched()
    {
        var rig = new LauncherRig();
        var external = Identity with { Pid = 777, ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddMinutes(1) };
        rig.Attach.OnAttach = () => rig.Census.Preflight = [external];
        rig.Api.HasSapModelValue = false;

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Equal(1, rig.Census.ObserveCalls);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Equal(Identity.Pid, error.Cleanup!.Data.OwnedPid);
        Assert.True(error.Cleanup.Success);
    }

    [Fact]
    public void AnUnconfirmedKillOfOurOwnHandleRetainsTheProvisionalRecord()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { RefusesToExit = true });
        rig.Api.HasSapModelValue = false;
        var state = rig.NewState();

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(state));

        Assert.False(error.Cleanup!.Success);
        Assert.Equal(ManagedEtabsShutdownState.ProcessExitUnconfirmed, error.Cleanup.Data.State);
        Assert.False(error.Cleanup.Data.ProcessExitConfirmed);
        Assert.True(error.Cleanup.Data.RecordRetained);
        Assert.NotNull(rig.Records.Record);
        Assert.Equal(0, rig.Records.ClearCount);
        Assert.Equal(Identity.Pid, error.Cleanup.Data.OwnedPid);
        Assert.Equal(Identity.Pid, rig.Cell.Identity?.Pid);
    }

    [Fact]
    public void ProvisionalWriteFailureEndsOnlyOurOwnHandleAndFailsTyped()
    {
        var rig = new LauncherRig();
        rig.Records.WriteException = new IOException("disk full");

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, error.Code);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Equal(0, rig.Attach.AttachCalls);
    }

    [Fact]
    public void VersionProbeFailureStopsTheProcessItAlreadyOwns()
    {
        var rig = new LauncherRig();
        var launcher = new ManagedEtabsLauncher(
            rig.Census,
            new FakeResolver(Identity.ExecutablePath),
            rig.Attach,
            new ThrowingVersionProbe(),
            TextWriter.Null,
            rig.Guards,
            rig.Clock,
            rig.Spawner,
            rig.Windows,
            new ManagedEtabsLauncherOptions());

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.ProcessIdentityFailed, error.Code);
        Assert.Equal(1, rig.Api.ExitCount);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Equal(0, rig.Census.TerminateExactCalls);
    }

    /// <summary>
    /// A window-subscription teardown that cannot be joined is named in the launch failure
    /// (CLI #20/#24), and the launch still stops the process it started.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnUnjoinableWindowSubscriptionTeardownIsNamedInTheLaunchFailure()
    {
        var rig = new LauncherRig();
        var guards = new LateSubscriptionGuardFactory();
        var launcher = new ManagedEtabsLauncher(
            rig.Census,
            new FakeResolver(Identity.ExecutablePath),
            rig.Attach,
            new FakeVersionProbe(),
            TextWriter.Null,
            guards,
            rig.Clock,
            rig.Spawner,
            rig.Windows,
            new ManagedEtabsLauncherOptions());

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.WindowSuppressionUnavailable, error.Code);
        Assert.Contains("UNRESOLVED", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Equal(0, rig.Attach.AttachCalls);

        guards.Release.Set();
        Assert.True(guards.Removed.Wait(TimeSpan.FromSeconds(30)), "the late hook was never removed.");
    }

    [Fact]
    public void LaunchHidesTheAttachedApplicationBeforeAnythingElseTouchesIt()
    {
        var rig = new LauncherRig();

        rig.Launcher.Launch(rig.NewState());

        Assert.Equal(1, rig.Api.HideCalls);
        Assert.False(rig.Api.IsVisible);
        var hide = rig.Events.IndexOf("hide");
        Assert.True(hide > rig.Events.IndexOf("attach"), "Hide must follow the attach.");
        Assert.True(hide < rig.Events.IndexOf("sap-model"), "Hide must precede everything after the attach.");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ACsiHideThatNeverAgreesDoesNotFailALaunchWindowsConfirms(
        bool nonZeroReturn,
        bool ignoreHide)
    {
        var rig = new LauncherRig();
        rig.Api.HideReturnCode = nonZeroReturn ? 1 : 0;
        rig.Api.IgnoreHide = ignoreHide;

        var managed = rig.Launcher.Launch(rig.NewState());

        Assert.Equal(Identity, managed.Identity);
        Assert.Equal(1, rig.Api.HideCalls);
        Assert.Contains("Windows state is the authority", rig.Diagnostics.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, rig.Guards.Activated[0].SuppressionConfirmations);
        Assert.Equal(1, rig.Guards.Activated[0].EnterBackgroundHiddenCalls);
    }

    [Fact]
    public void AnOwnedWindowStillOnScreenFailsTheLaunchAndStopsTheOwnedProcess()
    {
        var rig = new LauncherRig();
        rig.Guards.StillVisible = [0x2A4];

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.HiddenStateNotEstablished, error.Code);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed,
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("stage=after cHelper.GetObjectProcess", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.True(rig.Guards.Activated[0].Disposed);
        Assert.True(error.Cleanup!.Success);
        Assert.Equal(0, rig.Api.WrapCount);
    }

    [Fact]
    public void TheCsiHideCostsNoConvergenceWaitingAtAll()
    {
        var rig = new LauncherRig();
        rig.Api.IgnoreHide = true;

        rig.Launcher.Launch(rig.NewState());

        Assert.Equal(1, rig.Api.HideCalls);
        Assert.Equal(TimeSpan.Zero, rig.Clock.Elapsed);
        Assert.Empty(rig.Clock.Waits);
    }

    [Fact]
    public void LaunchReportsThatWindowsConfirmedTheSuppression()
    {
        var rig = new LauncherRig();

        rig.Launcher.Launch(rig.NewState());

        var text = rig.Diagnostics.ToString();
        Assert.Contains(
            "background UI suppression confirmed after cHelper.GetObjectProcess",
            text,
            StringComparison.Ordinal);
        Assert.Contains("observations=", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window guard is armed over the owned handle (never a pid) as soon as the process
    /// exists, before the attach and the hide.
    /// </summary>
    [Fact]
    public void TheWindowGuardIsArmedOverTheOwnedHandleBeforeTheAttach()
    {
        var rig = new LauncherRig();

        rig.Launcher.Launch(rig.NewState());

        var guard = Assert.Single(rig.Guards.Activated);
        Assert.Equal(Identity, guard.Identity);
        Assert.True(rig.Events.IndexOf("window-guard-activate") < rig.Events.IndexOf("attach"));
        Assert.True(rig.Events.IndexOf("record-write:Provisional") < rig.Events.IndexOf("window-guard-activate"));
        Assert.False(guard.Disposed);
        Assert.False(guard.ReleasedForUser);
    }

    // Z1: never attach to a half-initialised instance.
    [Fact]
    public void NoAttachIsMadeBeforeTheMainWindowExists()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { NoWindowPolls = 3 });
        var pollsAtAttach = -1;
        rig.Attach.OnAttach = () => pollsAtAttach = rig.Process.Polls;

        rig.Launcher.Launch(rig.NewState());

        Assert.Equal(1, rig.Attach.AttachCalls);
        Assert.True(pollsAtAttach > 3, $"attached after only {pollsAtAttach} polls");
        Assert.Equal(3, rig.Clock.Waits.Count);
    }

    [Fact]
    public void AMainWindowThatNeverAppearsFailsTypedAtTheDeadlineAndEndsOurHandle()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { NoWindowPolls = int.MaxValue });

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.Equal(0, rig.Attach.AttachCalls);
        Assert.True(rig.Clock.Elapsed >= new ManagedEtabsLauncherOptions().StartupDeadline);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Null(rig.Records.Record);
    }

    [Fact]
    public void ANotYetAttachableInstanceIsRetriedInsideTheDeadline()
    {
        var rig = new LauncherRig();
        rig.Attach.NullsBeforeSuccess = 2;

        rig.Launcher.Launch(rig.NewState());

        Assert.Equal(3, rig.Attach.AttachCalls);
        Assert.Equal(2, rig.Clock.Waits.Count);
    }

    // Z2: a startup-blocking window (the licence sign-in) is a typed failure, not a hang.
    [Fact]
    public void AVisibleNonMainWindowDuringStartupFailsTypedWithItsTitleAndClass()
    {
        var rig = new LauncherRig();
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in to ETABS", "AuthenticationView")));
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x100, "ETABS", "HwndWrapper[ETABS.exe]")));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.Contains("Sign in to ETABS", error.Message, StringComparison.Ordinal);
        Assert.Contains("AuthenticationView", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "detail={\"title\":\"Sign in to ETABS\",\"class\":\"AuthenticationView\",\"dismissed\":false," +
            "\"phase\":\"launch\",\"ownedExitConfirmed\":true,\"recordRetained\":false}",
            error.Message,
            StringComparison.Ordinal);

        // Our process is killed, and its record is cleared only after the confirmed exit.
        Assert.Equal(1, rig.Process.KillCount);
        Assert.True(error.Cleanup!.Success);
        Assert.Null(rig.Records.Record);
        Assert.Equal(1, rig.Records.ClearCount);
        Assert.Equal(0, rig.Attach.AttachCalls);
    }

    [Fact]
    public void AStartupBlockerWhoseProcessWillNotExitRetainsTheRecord()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { RefusesToExit = true });
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.False(error.Cleanup!.Success);
        Assert.True(error.Cleanup.Data.RecordRetained);
        Assert.NotNull(rig.Records.Record);
        Assert.Equal(0, rig.Records.ClearCount);
    }

    [Fact]
    public void AWindowOfAnotherPidIsNotAStartupBlocker()
    {
        var rig = new LauncherRig();
        rig.Windows.Windows.Add((Identity.Pid + 1, new StartupWindow(0x300, "Other app", "Anything")));
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x100, "ETABS", "Main")));

        var managed = rig.Launcher.Launch(rig.NewState());

        Assert.Equal(Identity, managed.Identity);
        Assert.Equal(0, rig.Process.KillCount);
    }

    // ── ownership races (card W2, X2, Y2) ───────────────────────────────────

    private static ManagedEtabsLauncherOptions Quick(int deadlineMs = 300) => new()
    {
        StartupDeadline = TimeSpan.FromMilliseconds(deadlineMs),
        AttachPollInterval = TimeSpan.FromMilliseconds(10),
        ForcedExitTimeout = TimeSpan.FromSeconds(5),
        GracefulCleanupWait = TimeSpan.Zero
    };

    // V7: AttachDeadlineFiresWhileGetObjectProcessIsBlocked, with an independent test deadline.
    // The blocked call models a COM call on a half-initialised instance: it returns only once
    // the process behind it is gone.
    [Fact]
    public void AttachDeadlineFiresWhileGetObjectProcessIsBlocked()
    {
        var rig = new LauncherRig(Quick(), SystemManagedEtabsClock.Instance);
        rig.Attach.OnAttach = () => rig.Process.Killed.Signalled("the supervisor to end our handle");

        var launch = Task.Run(() => Assert.Throws<EtabsLaunchException>(
            () => rig.Launcher.Launch(rig.NewState())));
        var error = launch.Within("Launch to fail with ETABS_START_TIMEOUT");

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Equal(0, rig.Census.TerminateExactCalls);

        // The late attach result was discarded: nothing hid, nothing wrapped.
        Assert.Equal(0, rig.Api.HideCalls);
        Assert.Equal(0, rig.Api.WrapCount);
        Assert.Null(rig.Records.Record);
    }

    [Fact]
    public void LateAttachAfterTheFenceIsDiscarded()
    {
        var rig = new LauncherRig();
        var state = rig.NewState();
        rig.Attach.OnAttach = () => _ = state.TerminateLaunch(TimeSpan.FromSeconds(5));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(state));

        Assert.Equal(EtabsLaunchErrorCodes.LaunchAborted, error.Code);
        Assert.Equal(0, rig.Api.HideCalls);
        Assert.Equal(1, rig.Process.KillCount);

        // No final record was ever written, and nothing is Ready.
        Assert.All(rig.Records.Writes, record => Assert.Equal(ManagedEtabsRecordPhase.Provisional, record.Phase));
        Assert.NotEqual(LaunchLifecycle.Ready, state.Lifecycle);
        Assert.Null(rig.Records.Record);
    }

    // W2: a blocked provisional write -> the timeout, then the late kill.
    [Fact]
    public void ABlockedProvisionalWriteTimesOutAndTheLateRecordIsClearedAfterTheConfirmedExit()
    {
        var rig = new LauncherRig(Quick(), SystemManagedEtabsClock.Instance);
        var release = new ManualResetEventSlim(false);
        rig.Records.OnWrite = _ => release.Signalled("the test to release the stalled write");
        var state = rig.NewState();

        var launch = Task.Run(() => Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(state)));
        // Release as soon as the supervisor has taken the launch.
        SpinWait.SpinUntil(() => state.Lifecycle == LaunchLifecycle.Terminating, Kit.TestDeadline);
        var error = launch.Within("Launch to fail with ETABS_START_TIMEOUT");

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Null(rig.Cell.Identity);

        release.Set();
        Assert.True(
            SpinWait.SpinUntil(() => rig.Records.Record is null && rig.Records.Writes.Count == 1, Kit.TestDeadline)
            || rig.Records.Record is null,
            "the late provisional record was not cleared after the confirmed exit");
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Null(rig.Cell.Identity);
    }

    // X2 test 1: a handle returned before the timeout while the identity read is blocked.
    [Fact]
    public void ABlockedIdentityReadIsEndedBySupervisorThroughTheSlotAndPublishesNothing()
    {
        var process = new FakeSpawnedProcess(Identity);
        var release = new ManualResetEventSlim(false);
        process.BeforeReadIdentity = () => release.Signalled("the test to release the identity read");
        var rig = new LauncherRig(Quick(), SystemManagedEtabsClock.Instance, process);
        var state = rig.NewState();

        var launch = Task.Run(() => Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(state)));
        var error = launch.Within("Launch to fail with ETABS_START_TIMEOUT");
        release.Set();

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.Equal(1, process.KillCount);
        Assert.Null(rig.Cell.Identity);
        Assert.Empty(rig.Records.Writes);
        Assert.True(error.Cleanup!.Success);
    }

    // X2 test 3: Process.Start returning after the timeout -> the worker kills its own handle.
    [Fact]
    public void ProcessStartReturningAfterTheDeadlineIsKilledByTheWorkerAndPublishesNothing()
    {
        var rig = new LauncherRig(Quick(), SystemManagedEtabsClock.Instance);
        var state = rig.NewState();
        rig.Spawner.BeforeReturn = () =>
            SpinWait.SpinUntil(() => state.Lifecycle == LaunchLifecycle.Terminating, Kit.TestDeadline);

        var launch = Task.Run(() => Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(state)));
        var error = launch.Within("Launch to fail with ETABS_START_TIMEOUT");

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.True(
            SpinWait.SpinUntil(() => rig.Process.KillCount == 1, Kit.TestDeadline),
            "the late handle was never killed");
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Null(rig.Cell.Identity);
        Assert.Empty(rig.Records.Writes);
    }

    // X2 test 4 / Y2 explicit order, with the CONTENT of the answer asserted (card P2-1):
    // the terminator wins Terminating and CLOSES THE EMPTY SLOT, THEN the worker stores and
    // sees CLOSED -> exactly one kill, by the worker, and the terminator learns it was confirmed.
    [Fact]
    public void ACloseOfTheEmptySlotBeforeTheLateStoreMakesTheWorkerTheOnlyKillerAndTheAnswerSaysSo()
    {
        var rig = new LauncherRig();
        var state = rig.NewState();
        var slotClosed = new ManualResetEventSlim(false);
        state.SlotClosedHook = slotClosed.Set;
        rig.Spawner.BeforeReturn = () => slotClosed.Signalled("the terminator to close the empty slot");
        var launching = Task.Run(() => Record.Exception(() => rig.Launcher.Launch(state)));
        Assert.True(SpinWait.SpinUntil(() => rig.Spawner.StartCount == 1, Kit.TestDeadline));

        var answer = Task.Run(() => state.TerminateLaunch(TimeSpan.FromSeconds(5)))
            .Within("the terminator to hear from the worker");

        Assert.NotNull(answer);
        Assert.True(answer!.Success, answer.Error);
        Assert.True(answer.Data.ProcessExitConfirmed);
        Assert.True(answer.Data.Forced);
        var error = Assert.IsType<EtabsLaunchException>(launching.Within("the launch to end"));
        Assert.Equal(EtabsLaunchErrorCodes.LaunchAborted, error.Code);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Null(rig.Cell.Identity);
        Assert.Empty(rig.Records.Writes);
        Assert.Equal(0, rig.Attach.AttachCalls);
    }

    // P2-1: the late kill is NOT confirmed -> the answer is UNCONFIRMED and the worker has
    // written the provisional record (the creation proof) so the orphan stays described.
    [Fact]
    public void AnUnconfirmedLateKillAnswersUnconfirmedAndWritesTheProvisionalRecord()
    {
        var process = new FakeSpawnedProcess(Identity) { RefusesToExit = true };
        var rig = new LauncherRig(process: process);
        var state = rig.NewState();
        var slotClosed = new ManualResetEventSlim(false);
        state.SlotClosedHook = slotClosed.Set;
        rig.Spawner.BeforeReturn = () => slotClosed.Signalled("the terminator to close the empty slot");
        var launching = Task.Run(() => Record.Exception(() => rig.Launcher.Launch(state)));
        Assert.True(SpinWait.SpinUntil(() => rig.Spawner.StartCount == 1, Kit.TestDeadline));

        var answer = Task.Run(() => state.TerminateLaunch(TimeSpan.FromSeconds(5)))
            .Within("the terminator to hear from the worker");
        _ = launching.Within("the launch to end");

        Assert.False(answer!.Success);
        Assert.False(answer.Data.ProcessExitConfirmed);
        Assert.Equal(ManagedEtabsShutdownState.ProcessExitUnconfirmed, answer.Data.State);
        Assert.True(answer.Data.RecordRetained);
        Assert.Equal(1, process.KillCount);
        var record = Assert.Single(rig.Records.Writes);
        Assert.Equal(ManagedEtabsRecordPhase.Provisional, record.Phase);
        Assert.Equal(Identity.Pid, record.Pid);
        Assert.NotNull(rig.Records.Record);
    }

    // P2-1: the worker is still inside Process.Start - there is no proof either way, so the
    // answer within the bound is UNCONFIRMED, never "confirmed".
    [Fact]
    public void ATerminationWhileProcessStartIsStillRunningAnswersUnconfirmedWithinTheBound()
    {
        var rig = new LauncherRig();
        var state = rig.NewState();
        var release = new ManualResetEventSlim(false);
        rig.Spawner.BeforeReturn = () => release.Signalled("the test to release Process.Start");
        var launching = Task.Run(() => Record.Exception(() => rig.Launcher.Launch(state)));
        Assert.True(SpinWait.SpinUntil(() => rig.Spawner.StartCount == 1, Kit.TestDeadline));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var answer = Task.Run(() => state.TerminateLaunch(TimeSpan.FromMilliseconds(300)))
            .Within("the terminator to give up on the worker");
        watch.Stop();
        release.Set();
        _ = launching.Within("the launch to end");

        Assert.False(answer!.Success);
        Assert.False(answer.Data.ProcessExitConfirmed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        // The late worker still kills its own handle afterwards.
        Assert.True(SpinWait.SpinUntil(() => rig.Process.KillCount == 1, Kit.TestDeadline));
    }

    // X2 test 5b: the expiry meets a launch that is already Ready - it finished in time.
    [Fact]
    public void AnExpiryThatMeetsAReadyLaunchKillsNothing()
    {
        var rig = new LauncherRig();
        var state = rig.NewState();
        var process = new FakeSpawnedProcess(Identity);
        Assert.True(state.TryStoreHandle(new HandleLease(process)));
        Assert.True(state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Committing));
        Assert.True(state.TryTransition(LaunchLifecycle.Committing, LaunchLifecycle.Ready));

        rig.Launcher.ExpireStartup(state);

        Assert.Equal(0, process.KillCount);
        Assert.Equal(LaunchLifecycle.Ready, state.Lifecycle);
        Assert.False(state.TimedOut);
    }

    // X2 test 5a / Y3: the expiry meets a Committing stall -> bounded termination, no Ready,
    // and the record per Y1.
    [Fact]
    public void AnExpiryThatMeetsAStalledCommitTerminatesWithinTheBoundAndNeverReachesReady()
    {
        var rig = new LauncherRig();
        var state = rig.NewState();
        var process = new FakeSpawnedProcess(Identity);
        var lease = new HandleLease(process);
        Assert.True(state.TryStoreHandle(lease));
        state.Publish(Identity, lease);
        state.WriteProvisionalRecord(Identity);
        Assert.True(state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Committing));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task.Run(() => rig.Launcher.ExpireStartup(state)).Within("the expiry to finish");
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal(LaunchLifecycle.Ended, state.Lifecycle);
        Assert.False(state.TryTransition(LaunchLifecycle.Committing, LaunchLifecycle.Ready));
        Assert.Equal(1, process.KillCount);
        Assert.Null(rig.Records.Record);
        Assert.True(state.TimedOut);
    }

    // CARD test 13: the watchdog sees a cold-start dialog BEFORE the launch returns, because
    // the pid is published the moment Process.Start produced the identity.
    [Fact]
    public void WatchdogSeesAColdStartDialogBeforeTheAttachReturns()
    {
        var rig = new LauncherRig();
        var attachEntered = new ManualResetEventSlim(false);
        var releaseAttach = new ManualResetEventSlim(false);
        rig.Attach.OnAttach = () =>
        {
            attachEntered.Set();
            releaseAttach.Signalled("the test to release the attach");
        };
        var dialogs = new DialogSource(new EtabsDialogWindow(0x55, "Recover autosave?", ["Recover?"]));
        var watchdog = new EtabsModalDialogWatchdog(
            dialogs,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));
        var launch = Task.Run(() => rig.Launcher.Launch(rig.NewState()));

        attachEntered.Signalled("the launch to reach the attach");
        var guarded = watchdog.GuardAsync(
            () => rig.Cell.Identity?.Pid,
            () => launch.ContinueWith(_ => (object)"launched", TaskScheduler.Default),
            CancellationToken.None).Within("the watchdog to flag the dialog");

        var failure = Assert.IsType<Shared.Common.Result>(guarded);
        Assert.False(failure.Success);
        Assert.Contains(EtabsTypedFailureCodes.ModalDialog, failure.Error!, StringComparison.Ordinal);
        Assert.False(launch.IsCompleted, "the dialog must be seen while the launch is still in flight");
        Assert.Equal(Identity.Pid, dialogs.LastPid);

        releaseAttach.Set();
        launch.Within("the launch to finish");
    }

    private sealed class DialogSource(EtabsDialogWindow dialog) : IEtabsDialogWindowSource
    {
        public int LastPid { get; private set; }

        public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid)
        {
            LastPid = pid;
            return [dialog];
        }

        public bool TryDismiss(EtabsDialogWindow dialog) => false;
    }

    // ── container and boundary ──────────────────────────────────────────────

    [Fact]
    public void TheServeContainerCanStillConstructTheLauncherAndTheSession()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection([]).Build());
        services.AddServeFeature();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ManagedEtabsLauncher>(
            scope.ServiceProvider.GetRequiredService<IManagedEtabsLauncher>());
        var session = scope.ServiceProvider.GetRequiredService<IEtabsSession>();
        Assert.Null(session.ProcessId);
        Assert.False(session.IsStarted);
    }

    [Fact]
    public void UnreadableApiVersionDoesNotFailAProvenLifecycle()
    {
        var rig = new LauncherRig();
        rig.Api.OapiVersionException = new InvalidOperationException("no version");

        var managed = rig.Launcher.Launch(rig.NewState());
        managed.CompleteApiReadiness();

        Assert.Equal(0d, rig.Api.WrappedApiVersion);
        Assert.Equal(23, rig.Api.WrappedMajorVersion);
    }

    // The census-attribution path is gone, not merely unused.
    [Theory]
    [InlineData("IEtabsProcessStarter")]
    [InlineData("WindowsEtabsProcessStarter")]
    [InlineData("IManagedEtabsConnector")]
    [InlineData("EtabSharpManagedEtabsConnector")]
    [InlineData("IEtabsLaunchClock")]
    [InlineData("SystemEtabsLaunchClock")]
    public void RemovedManagedStartupTypesAreAbsentFromTheAssembly(string typeName)
    {
        var assembly = typeof(IManagedEtabsApplication).Assembly;

        Assert.Null(assembly.GetType(
            $"EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.{typeName}"));
    }

    [Fact]
    public void ResolveSurvivorWithoutHandleIsDeleted()
    {
        var methods = typeof(ManagedEtabsLauncher)
            .GetMethods(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Select(method => method.Name);

        Assert.DoesNotContain("ResolveSurvivorWithoutHandle", methods);
        Assert.DoesNotContain("CensusExactlyOneOwnedProcess", methods);
    }

    // The raw boundary is lifecycle plus the three cOAPI visibility calls: no attach, no
    // ROT lookup, no out-of-band start - and no ApplicationStart, which L5 proved returns -1.
    [Fact]
    public void RawApiBoundaryExposesLifecycleAndVisibilityAndNothingElse()
    {
        var members = typeof(IEtabsRawApi).GetInterfaces()
            .Append(typeof(IEtabsRawApi))
            .SelectMany(contract => contract.GetMembers())
            .Select(member => member.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Application",
                "ApplicationExit",
                "CompleteApiReadiness",
                "GetOapiVersionNumber",
                "HasSapModel",
                "Hide",
                "InitializeNewModel",
                "ReleaseApiReferences",
                "Unhide",
                "Visible"
            ],
            members);
    }

    // ── siblings (core-loop §2.3) ───────────────────────────────────────────

    private static readonly ManagedProcessIdentity SiblingEtabs = new(
        5151,
        new DateTimeOffset(2026, 8, 13, 5, 0, 0, TimeSpan.Zero),
        Identity.ExecutablePath);

    private static ManagedEtabsSessionRecord SiblingRecord(DateTimeOffset startTime) => new(
        1,
        SiblingEtabs.Pid,
        startTime,
        SiblingEtabs.ExecutablePath,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow);

    private sealed class FixedSiblingRecord(ManagedEtabsSessionRecord record) : ISiblingRecordSource
    {
        public ManagedEtabsSessionRecord? Read() => record;
    }

    private static LauncherRig RigBesideSibling(DateTimeOffset siblingRecordStart)
    {
        var cell = new LaunchPublicationCell();
        var rig = new LauncherRig(
            inspectorFactory: census => new SiblingAwareProcessInspector(
                census,
                new FixedSiblingRecord(SiblingRecord(siblingRecordStart)),
                cell));
        rig.Census.Preflight = [SiblingEtabs];
        return rig;
    }

    [Fact]
    public void LaunchBesideTheSiblingsEtabsTakesOnlyItsOwnProcess()
    {
        var rig = RigBesideSibling(SiblingEtabs.ProcessStartTimeUtc);

        var managed = rig.Launcher.Launch(rig.NewState());

        Assert.Equal(Identity, managed.Identity);
        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Equal(0, rig.Process.KillCount);
    }

    [Fact]
    public void ASiblingRecordWithAnotherStartTimeDoesNotExcuseAnExternalEtabs()
    {
        var rig = RigBesideSibling(SiblingEtabs.ProcessStartTimeUtc.AddSeconds(1));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Contains("already exists: observedPids=[5151]", error.Message, StringComparison.Ordinal);
    }

    // A failed start beside the sibling's ETABS ends only OUR handle.
    [Fact]
    public void AFailedStartBesideTheSiblingNeverTouchesTheSiblingsEtabs()
    {
        var rig = RigBesideSibling(SiblingEtabs.ProcessStartTimeUtc);
        rig.Api.HasSapModelValue = false;

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(0, rig.Census.TerminateExactCalls);
        Assert.Equal(1, rig.Process.KillCount);
        Assert.Equal(Identity.Pid, error.Cleanup!.Data.OwnedPid);
    }

    /// <summary>§2.7 / DD-5: exit goes through <c>ApplicationExit(false)</c>, never a window close.</summary>
    [Fact]
    public void ManagedExitGoesThroughApplicationExitWithoutSaving()
    {
        var rig = new LauncherRig();
        var managed = rig.Launcher.Launch(rig.NewState());

        Assert.Equal(0, managed.ExitWithoutSaving());

        Assert.Equal(1, rig.Api.ExitCount);
        Assert.Equal("application-exit-save=False", rig.Events[^1]);
    }

    private sealed class ThrowingVersionProbe : IEtabsVersionProbe
    {
        public (int MajorVersion, string FullVersion) Read(string executablePath) =>
            throw new IOException("version unreadable");
    }

    /// <summary>
    /// The PRODUCTION window monitor over a <c>SetWinEventHook</c> the test holds open past
    /// both of the monitor's deadlines.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class LateSubscriptionGuardFactory : IManagedEtabsWindowGuardFactory
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public ManualResetEventSlim Removed { get; } = new(false);

        public IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess) =>
            new ManagedEtabsWindowGuard(
                ownedProcess,
                new NoWindows(),
                ManagedEtabsWindowPolicy.Default,
                new Win32OwnedWindowSurfaceMonitor(
                    TimeSpan.FromMilliseconds(5),
                    HoldTheInstallOpen,
                    _ => Removed.Set(),
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(50)),
                new StatedDesktop());

        private nint HoldTheInstallOpen(
            int processId,
            Win32OwnedWindowSurfaceMonitor.WinEventProc proc)
        {
            _ = Release.Wait(TimeSpan.FromSeconds(30));
            return 0x5150;
        }

        private sealed class NoWindows : ITopLevelWindows
        {
            public IReadOnlyList<TopLevelWindow> Enumerate() => [];
        }

        private sealed class StatedDesktop : IVirtualDesktop
        {
            public WindowBounds Bounds => new(0, 0, 1920, 1080);
        }
    }
}
