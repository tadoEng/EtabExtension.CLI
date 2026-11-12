using System.Reflection;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>A session over the REAL launcher with ETABS-free doubles (card v3: ownership by construction).</summary>
internal sealed class SessionRig
{
    public LauncherRig L { get; }
    public EtabsSession Session { get; }

    public SessionRig(
        LauncherRig? launcher = null,
        TimeSpan? gateWait = null,
        TimeSpan? graceful = null,
        bool processExitsOnApplicationExit = true,
        Func<ISessionRecordStore, IManagedEtabsShutdownMachine>? machine = null)
    {
        L = launcher ?? new LauncherRig();
        L.Census.Live = Kit.Identity;
        if (processExitsOnApplicationExit)
        {
            L.Api.OnExit = L.Process.MarkExited;
        }

        var work = new EtabsWorkScope();
        _ = work.Enter(new EtabsWorkContext(ManagedEtabsStartIntent.VisibleByConsent, "test"));
        var wait = graceful ?? TimeSpan.FromSeconds(1);
        Session = new EtabsSession(
            L.Launcher,
            L.Census,
            L.Records,
            machine?.Invoke(L.Records) ?? new ManagedEtabsShutdownMachine(L.Records, wait, wait),
            work,
            TextWriter.Null,
            L.Cell,
            gateWait ?? TimeSpan.FromMilliseconds(100));
    }

    /// <summary>The session's own gate, for a test that has to hold it the way a stuck command does.</summary>
    public object Gate => Field("_gate")!;

    public object? Field(string name) =>
        typeof(EtabsSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Session);

    public Task<Exception?> StartOnBackgroundThread() =>
        Task.Run(() => Record.Exception(() => Session.GetOrStartOwned()));
}

public sealed class ManagedEtabsOwnershipTests
{
    private static readonly ManagedProcessIdentity Identity = Kit.Identity;

    // ── commit, record phases, publication ──────────────────────────────────

    [Fact]
    public void ACommittedSessionHasAProvisionalThenAFinalRecordWithOneIdAndIsReady()
    {
        var rig = new SessionRig();

        var owned = rig.Session.GetOrStartOwned();

        Assert.True(rig.Session.IsStarted);
        Assert.Equal(Identity.Pid, rig.Session.ProcessId);
        Assert.Equal(
            [ManagedEtabsRecordPhase.Provisional, ManagedEtabsRecordPhase.Ready],
            rig.L.Records.Writes.Select(record => record.Phase).ToArray());
        Assert.All(
            rig.L.Records.Writes,
            record => Assert.Equal(owned.ManagedLaunchRecordId, record.ManagedLaunchRecordId));
    }

    // Test 12: the pid is published at identity time and reading it never takes the gate.
    [Fact]
    public void ProcessIdIsPublishedAtIdentityTimeWithoutTakingTheGate()
    {
        var rig = new SessionRig();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        rig.L.Attach.OnAttach = () =>
        {
            entered.Set();
            release.Signalled("the test to release the attach");
        };
        var launching = rig.StartOnBackgroundThread();
        entered.Signalled("the launch to reach the attach (it holds the gate)");

        var read = Task.Run(() => (rig.Session.ProcessId, rig.Session.IsStarted))
            .Within("ProcessId/IsStarted while the launch holds the gate");

        Assert.Equal(Identity.Pid, read.ProcessId);
        Assert.False(read.IsStarted);
        release.Set();
        Assert.Null(launching.Within("the launch to finish"));
    }

    // Test 18: reading the pid while the gate is held parks no thread.
    [Fact]
    public void ReadingTheOwnedPidWhileTheGateIsHeldLeavesNoBlockedThread()
    {
        var rig = new SessionRig();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        rig.L.Attach.OnAttach = () =>
        {
            entered.Set();
            release.Signalled("the test to release the attach");
        };
        var launching = rig.StartOnBackgroundThread();
        entered.Signalled("the launch to reach the attach");

        var reads = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => rig.Session.ProcessId))
            .ToArray();
        Task.WhenAll(reads).Within("200 concurrent pid reads while the gate is held");

        Assert.All(reads, read => Assert.Equal(Identity.Pid, read.Result));
        release.Set();
        Assert.Null(launching.Within("the launch to finish"));
    }

    // Test 14: the pid and the record are cleared only after a CONFIRMED exit.
    [Fact]
    public void PublishedLaunchPidIsClearedOnlyAfterConfirmedExit()
    {
        var process = new FakeSpawnedProcess(Identity) { RefusesToExit = true };
        var rig = new SessionRig(new LauncherRig(process: process), processExitsOnApplicationExit: false);
        rig.Session.GetOrStartOwned();

        var unconfirmed = rig.Session.Shutdown();

        Assert.False(unconfirmed.Data.ProcessExitConfirmed);
        Assert.Equal(Identity.Pid, rig.Session.ProcessId);
        Assert.NotNull(rig.L.Records.Record);
    }

    [Fact]
    public void ThePublishedPidIsClearedOnceTheExitIsConfirmed()
    {
        var rig = new SessionRig();
        rig.Session.GetOrStartOwned();

        var confirmed = rig.Session.Shutdown();

        Assert.True(confirmed.Data.ProcessExitConfirmed);
        Assert.Null(rig.Session.ProcessId);
        Assert.False(rig.Session.IsStarted);
        Assert.Null(rig.L.Records.Record);
    }

    // ── the lead's rule: ApplicationExit first, kill only as the bounded last resort ──

    [Fact]
    public void NormalShutdownAsksApplicationExitBeforeAnyKillOrTerminateExact()
    {
        var process = new FakeSpawnedProcess(Identity);
        var rig = new SessionRig(new LauncherRig(process: process), processExitsOnApplicationExit: false);
        process.Events = rig.L.Events;
        rig.Session.GetOrStartOwned();
        rig.L.Events.Clear();

        var result = rig.Session.Shutdown();

        Assert.True(result.Success, result.Error);
        var exit = rig.L.Events.IndexOf("application-exit-save=False");
        var kill = rig.L.Events.IndexOf("process-kill");
        Assert.True(exit >= 0, "the normal shutdown never asked ETABS to exit");
        Assert.True(kill > exit, "the process was killed before ApplicationExit was attempted");
        Assert.Equal(0, rig.L.Census.TerminateExactCalls);
        Assert.True(result.Data.Forced);
    }

    [Fact]
    public void NormalShutdownThatETABSAnswersNeverKillsAnything()
    {
        var process = new FakeSpawnedProcess(Identity);
        var rig = new SessionRig(new LauncherRig(process: process));
        rig.Session.GetOrStartOwned();

        var result = rig.Session.Shutdown();

        Assert.True(result.Success, result.Error);
        Assert.False(result.Data.Forced);
        Assert.Equal(1, rig.L.Api.ExitCount);
        Assert.Equal(0, process.KillCount);
        Assert.Equal(0, rig.L.Census.TerminateExactCalls);
    }

    // ── blocked shutdown (card I6) ──────────────────────────────────────────

    // Test 15: the launch holds the gate and is stuck; the blocked shutdown kills the
    // launching process through its handle and never waits for the gate.
    [Fact]
    public void BlockedShutdownDuringColdStartKillsTheLaunchingProcessByExactIdentity()
    {
        var rig = new SessionRig();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        rig.L.Attach.OnAttach = () =>
        {
            entered.Set();
            release.Signalled("the test to release the attach");
        };
        var launching = rig.StartOnBackgroundThread();
        entered.Signalled("the launch to reach the attach");

        var result = Task.Run(rig.Session.ShutdownWhileWorkerBlocked)
            .Within("the blocked shutdown while the launch holds the gate");

        Assert.True(result.Success, result.Error);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.Equal(Identity.Pid, result.Data.OwnedPid);
        Assert.Equal(1, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Census.TerminateExactCalls);
        Assert.Null(rig.Session.ProcessId);

        release.Set();
        var failure = Assert.IsType<EtabsLaunchException>(launching.Within("the launch to end"));
        Assert.Equal(EtabsLaunchErrorCodes.LaunchAborted, failure.Code);
        Assert.False(rig.Session.IsStarted);
        Assert.Equal(0, rig.L.Api.HideCalls);
        Assert.Null(rig.L.Records.Record);
    }

    // Test 16: a stuck command holds the gate; the shutdown acts ONLY through the handle and
    // writes none of the gate-guarded fields.
    [Fact]
    public void BlockedShutdownNeverMutatesGatedStateWithoutTheGate()
    {
        var rig = new SessionRig();
        var owned = rig.Session.GetOrStartOwned();
        var holding = new ManualResetEventSlim(false);
        var letGo = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            lock (rig.Gate)
            {
                holding.Set();
                letGo.Signalled("the test to release the gate");
            }
        });
        holding.Signalled("the stuck command to take the gate");
        var ownedBefore = rig.Field("_owned");
        var resultBefore = rig.Field("_shutdownResult");

        var result = Task.Run(rig.Session.ShutdownWhileWorkerBlocked)
            .Within("the blocked shutdown while a stuck command holds the gate");

        // Snapshot WHILE the gate is still held: nothing it guards was written.
        Assert.Same(ownedBefore, rig.Field("_owned"));
        Assert.Same(owned, rig.Field("_owned"));
        Assert.Equal(resultBefore, rig.Field("_shutdownResult"));
        Assert.Null(rig.Field("_shutdownResult"));
        Assert.Null(rig.Field("_launchFailure"));

        // And the handle did the work.
        Assert.True(result.Success, result.Error);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.Forced);
        Assert.Equal(1, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Census.TerminateExactCalls);
        Assert.Null(rig.L.Records.Record);
        Assert.Null(rig.Session.ProcessId);

        letGo.Set();
        holder.Within("the stuck command to release the gate");

        // The normal path that follows sees a finished session and does not kill twice.
        var later = rig.Session.Shutdown();
        Assert.True(later.Success, later.Error);
        Assert.Equal(1, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Census.TerminateExactCalls);
    }

    // A4: a fallback that cannot confirm the exit retains the record and the pid.
    [Fact]
    public void AHandleOnlyShutdownThatCannotConfirmTheExitRetainsTheRecordAndThePid()
    {
        var process = new FakeSpawnedProcess(Identity) { RefusesToExit = true };
        var rig = new SessionRig(new LauncherRig(process: process), processExitsOnApplicationExit: false);
        rig.Session.GetOrStartOwned();
        var holding = new ManualResetEventSlim(false);
        var letGo = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            lock (rig.Gate)
            {
                holding.Set();
                letGo.Signalled("the test to release the gate");
            }
        });
        holding.Signalled("the stuck command to take the gate");

        var result = Task.Run(rig.Session.ShutdownWhileWorkerBlocked).Within("the handle-only shutdown");

        Assert.False(result.Success);
        Assert.False(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.RecordRetained);
        Assert.NotNull(rig.L.Records.Record);
        Assert.Equal(Identity.Pid, rig.Session.ProcessId);
        letGo.Set();
        holder.Within("the gate to be released");
    }

    // ── W1: one atomic transition decides commit versus shutdown ───────────

    // The launch holds the point just before its CAS; shutdown wins Terminating.
    [Fact]
    public void ShutdownWinningTerminatingBeforeTheCommitCasLeavesNoRecordAndNoReady()
    {
        var rig = new SessionRig();
        var atCommit = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        rig.L.Guards.OnActivated = guard => guard.OnConfirmSuppressed = call =>
        {
            // The second confirmation is the session's, immediately before it commits.
            if (call == 2)
            {
                atCommit.Set();
                release.Signalled("the test to release the launch");
            }
        };
        var launching = rig.StartOnBackgroundThread();
        atCommit.Signalled("the launch to reach the commit");

        var answer = Task.Run(rig.Session.Shutdown).Within("the shutdown while the launch is before its CAS");

        Assert.True(answer.Success, answer.Error);
        Assert.Equal(1, rig.L.Process.KillCount);
        release.Set();
        var failure = Assert.IsType<EtabsLaunchException>(launching.Within("the launch to end"));
        Assert.Equal(EtabsLaunchErrorCodes.LaunchAborted, failure.Code);

        Assert.False(rig.Session.IsStarted);
        Assert.Single(rig.L.Records.Writes);
        Assert.Equal(ManagedEtabsRecordPhase.Provisional, rig.L.Records.Writes[0].Phase);
        Assert.Null(rig.L.Records.Record);
        Assert.Equal(1, rig.L.Process.KillCount);
    }

    // The mirror: the commit wins, the shutdown waits, then the NORMAL shutdown runs.
    [Fact]
    public void ACommitThatWinsMakesTheShutdownWaitAndThenShutDownNormally()
    {
        var rig = new SessionRig();
        var writing = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        rig.L.Records.OnWrite = record =>
        {
            if (record.Phase == ManagedEtabsRecordPhase.Ready)
            {
                writing.Set();
                release.Signalled("the test to release the final record write");
            }
        };
        var launching = rig.StartOnBackgroundThread();
        writing.Signalled("the commit to reach its final record write");

        var shutdown = Task.Run(rig.Session.Shutdown);
        Thread.Sleep(200);
        Assert.False(shutdown.IsCompleted, "the shutdown must wait for the short commit");
        release.Set();

        Assert.Null(launching.Within("the launch to commit"));
        var answer = shutdown.Within("the normal shutdown after the commit");
        Assert.True(answer.Success, answer.Error);
        Assert.Equal(1, rig.L.Api.ExitCount);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Null(rig.L.Records.Record);
        Assert.False(rig.Session.IsStarted);
    }

    // X1: a Committing stall beyond the shutdown's wait.
    [Fact]
    public void ACommittingStallIsTerminatedByShutdownWithinItsBudgetAndNeverPublishesReady()
    {
        var rig = new SessionRig();
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
        var everReady = false;
        var launching = rig.StartOnBackgroundThread();
        writing.Signalled("the commit to stall in its final record write");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var answer = Task.Run(rig.Session.Shutdown).Within("the shutdown to answer the stalled commit");
        watch.Stop();
        everReady |= rig.Session.IsStarted;

        Assert.True(answer.Success, answer.Error);
        Assert.True(answer.Data.ProcessExitConfirmed);
        Assert.True(
            watch.Elapsed < EtabsLaunchState.CommitWait + TimeSpan.FromSeconds(5),
            $"shutdown took {watch.Elapsed}");
        Assert.Equal(1, rig.L.Process.KillCount);

        release.Set();
        var failure = Assert.IsType<EtabsLaunchException>(launching.Within("the stalled launch to end"));
        everReady |= rig.Session.IsStarted;

        Assert.Equal(EtabsLaunchErrorCodes.LaunchAborted, failure.Code);
        Assert.False(everReady, "Ready was published after the shutdown took Terminating");
        Assert.Null(rig.L.Records.Record);
        Assert.True(rig.L.Process.HasExited);
    }

    // Y1: the same stall with a kill that is not confirmed -> the record is retained.
    [Fact]
    public void AnUnconfirmedKillInTheCommittingStallRetainsTheRecordAndAnswersUnconfirmed()
    {
        var process = new FakeSpawnedProcess(Identity) { RefusesToExit = true };
        var rig = new SessionRig(new LauncherRig(process: process), processExitsOnApplicationExit: false);
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

        var answer = Task.Run(rig.Session.Shutdown).Within("the shutdown to answer");
        release.Set();
        _ = launching.Within("the stalled launch to end");

        Assert.False(answer.Success);
        Assert.False(answer.Data.ProcessExitConfirmed);
        Assert.False(rig.Session.IsStarted);
        Assert.NotNull(rig.L.Records.Record);
        Assert.Equal(0, rig.L.Records.ClearCount);
        Assert.Equal(Identity.Pid, rig.Session.ProcessId);
    }

    // ── I4: our own identity is never a sibling ─────────────────────────────

    [Fact]
    public void OwnIdentityIsNeverASiblingEvenWhenTheSiblingRecordIsAnAliasOfOurOwn()
    {
        var aliasOfOurOwn = new ManagedEtabsSessionRecord(
            1,
            Identity.Pid,
            Identity.ProcessStartTimeUtc,
            Identity.ExecutablePath,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);
        var observation = new EtabsProcessObservation([Identity], 0);

        var classified = EtabsSiblingCensus.Classify(observation, aliasOfOurOwn, Identity);

        Assert.Empty(classified.Siblings);
        Assert.Equal([Identity], classified.NonSibling);
        Assert.Equal(
            EtabsInstanceOwnership.Managed,
            EtabsOwnershipResolver.Resolve(classified, Identity.Pid));

        // Without the own-identity input the alias WOULD hide our own ETABS as a sibling.
        Assert.Single(EtabsSiblingCensus.Classify(observation, aliasOfOurOwn, ownIdentity: null).Siblings);
    }

    [Fact]
    public void TheInspectorRemovesThePublishedOwnIdentityFromTheSiblingSet()
    {
        var cell = new LaunchPublicationCell();
        var state = new EtabsLaunchState(new MemoryRecordStore(), cell);
        state.Publish(Identity, lease: null);
        var alias = new ManagedEtabsSessionRecord(
            1, Identity.Pid, Identity.ProcessStartTimeUtc, Identity.ExecutablePath,
            Guid.NewGuid(), DateTimeOffset.UtcNow);
        var census = new FakeCensus { Preflight = [Identity] };
        var inspector = new SiblingAwareProcessInspector(census, new FixedSibling(alias), cell);

        var observation = inspector.ObserveEtabs();

        Assert.Empty(observation.Siblings);
        Assert.Equal([Identity], observation.NonSibling);
    }

    private sealed class FixedSibling(ManagedEtabsSessionRecord record) : ISiblingRecordSource
    {
        public ManagedEtabsSessionRecord? Read() => record;
    }

    // Classification: a reused pid is not a sibling (start time / path mismatches).
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReusedPidWithADifferentStartTimeOrPathIsNotASibling(bool differentStart, bool differentPath)
    {
        var sibling = new ManagedEtabsSessionRecord(
            1, 5151, Identity.ProcessStartTimeUtc, Identity.ExecutablePath,
            Guid.NewGuid(), DateTimeOffset.UtcNow);
        var live = new ManagedProcessIdentity(
            5151,
            differentStart ? Identity.ProcessStartTimeUtc.AddSeconds(1) : Identity.ProcessStartTimeUtc,
            differentPath ? @"C:\Other\ETABS.exe" : Identity.ExecutablePath);

        var classified = EtabsSiblingCensus.Classify(new EtabsProcessObservation([live], 0), sibling);

        Assert.Empty(classified.Siblings);
    }

    // ── handle lease (card V4) ──────────────────────────────────────────────

    [Fact]
    public void TheHandleIsDisposedOnlyWhenTheLastReferenceGoes()
    {
        var process = new FakeSpawnedProcess(Identity);
        var lease = new HandleLease(process);

        Assert.True(lease.TryAcquire());
        lease.ReleaseOwner();
        Assert.False(process.Disposed);
        lease.Kill();
        Assert.Equal(1, process.KillCount);

        lease.Release();
        Assert.True(process.Disposed);
        Assert.True(lease.IsDisposed);
    }

    // The owner reference counts ONCE, however many parties believe they own the cleanup.
    [Fact]
    public void TheOwnerReferenceIsReleasedAtMostOnce()
    {
        var process = new FakeSpawnedProcess(Identity);
        var lease = new HandleLease(process);
        Assert.True(lease.TryAcquire());

        lease.ReleaseOwner();
        lease.ReleaseOwner();
        lease.ReleaseOwner();

        Assert.False(process.Disposed, "a second owner release dropped a reference it did not own");
        Assert.True(lease.TryAcquire());
        lease.Release();
        lease.Release();
        Assert.True(process.Disposed);
    }

    [Fact]
    public void ALeaseUsedAfterDisposalIsAHarmlessNoOp()
    {
        var process = new FakeSpawnedProcess(Identity);
        var lease = new HandleLease(process);
        lease.ReleaseOwner();

        Assert.False(lease.TryAcquire());
        lease.Kill();
        Assert.True(lease.HasExited);
        Assert.True(lease.WaitForExit(TimeSpan.Zero));
        Assert.Equal(0, process.KillCount);
        lease.ReleaseOwner();
        Assert.True(process.Disposed);
    }

    [Fact]
    public void ConcurrentAcquireAndReleaseDisposeTheHandleExactlyOnceAndNeverUseItAfterwards()
    {
        for (var round = 0; round < 50; round++)
        {
            var process = new FakeSpawnedProcess(Identity);
            var lease = new HandleLease(process);
            var users = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    if (lease.TryAcquire())
                    {
                        _ = lease.HasExited;
                        lease.Release();
                    }
                }
            })).ToArray();
            var owner = Task.Run(lease.ReleaseOwner);

            Task.WhenAll(users.Append(owner)).Within("the lease users");

            Assert.True(process.Disposed);
            Assert.True(lease.IsDisposed);
            Assert.False(lease.TryAcquire());
        }
    }

    // Disposal race: the fallback kill while the normal path releases its owner reference.
    [Fact]
    public void AKillRacingTheOwnersReleaseNeverTouchesADisposedHandle()
    {
        for (var round = 0; round < 50; round++)
        {
            var process = new FakeSpawnedProcess(Identity);
            var lease = new HandleLease(process);
            var killer = Task.Run(lease.Kill);
            var owner = Task.Run(lease.ReleaseOwner);

            Task.WhenAll(killer, owner).Within("the kill and the release");

            Assert.True(process.Disposed);
            Assert.InRange(process.KillCount, 0, 1);
        }
    }

    // ── state machine unit facts ────────────────────────────────────────────

    [Fact]
    public void TheHandleSlotHasExactlyOneCleanupOwner()
    {
        var state = new EtabsLaunchState(new MemoryRecordStore(), new LaunchPublicationCell());
        var first = new HandleLease(new FakeSpawnedProcess(Identity));
        var second = new HandleLease(new FakeSpawnedProcess(Identity));

        Assert.True(state.TryStoreHandle(first));
        Assert.False(state.TryStoreHandle(second));
        Assert.Same(first, state.CloseSlot());
        Assert.Null(state.CloseSlot());
        Assert.False(state.TryStoreHandle(second));
    }

    [Fact]
    public void LifecycleTransitionsAreCompareExchangeOnly()
    {
        var state = new EtabsLaunchState(new MemoryRecordStore(), new LaunchPublicationCell());

        Assert.False(state.TryTransition(LaunchLifecycle.Committing, LaunchLifecycle.Ready));
        Assert.True(state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Committing));
        Assert.False(state.TryTransition(LaunchLifecycle.Launching, LaunchLifecycle.Terminating));
        Assert.True(state.TryTransition(LaunchLifecycle.Committing, LaunchLifecycle.Terminating));
        Assert.False(state.TryTransition(LaunchLifecycle.Committing, LaunchLifecycle.Ready));
    }

    [Fact]
    public void ARecordOfAnotherGenerationIsNeverClearedByThisAttempt()
    {
        var records = new MemoryRecordStore
        {
            Record = new ManagedEtabsSessionRecord(
                1, Identity.Pid, Identity.ProcessStartTimeUtc, Identity.ExecutablePath,
                Guid.NewGuid(), DateTimeOffset.UtcNow)
        };
        var state = new EtabsLaunchState(records, new LaunchPublicationCell());

        var retained = state.ClearRecordAfterConfirmedExit(() => true);

        Assert.True(retained);
        Assert.NotNull(records.Record);
        Assert.Equal(0, records.ClearCount);
    }
}
