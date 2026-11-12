using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// #747-D, C1/C3/C5: detach as an atomic terminal disposition (cards v2.1 C1, v2.3 §3).
/// Every test is ETABS-free: fakes for the process, the record store and the window guard.
/// </summary>
public sealed class DetachTests
{
    private static SessionRig Ready(TimeSpan? budget = null)
    {
        var rig = new SessionRig();
        rig.Session.DetachBudget = budget ?? TimeSpan.FromSeconds(5);
        rig.Session.DetachSettleWait = TimeSpan.FromMilliseconds(300);
        _ = rig.Session.GetOrStartOwned();
        return rig;
    }

    private static EtabsLaunchState Launch(SessionRig rig) => (EtabsLaunchState)rig.Field("_launch")!;

    internal static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Kit.TestDeadline;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Test deadline expired waiting for: {what}");
            }

            Thread.Sleep(5);
        }
    }

    // Card C1: the durable write is step 1, the hand-off ends with the lease released as
    // handed-off (never exit-confirmed), and nothing is exited or killed.
    [Fact]
    public void ADetachWritesThePhaseFirstThenHandsTheInstanceOverAndKillsNothing()
    {
        var rig = Ready();
        ManagedEtabsSessionRecord? recordAtCommit = null;
        var guardDisposedAtCommit = true;
        var clearsAtCommit = -1;
        rig.Session.AfterDetachWrite = () =>
        {
            recordAtCommit = rig.L.Records.Record;
            guardDisposedAtCommit = rig.L.Guards.Activated[0].Disposed;
            clearsAtCommit = rig.L.Records.ClearCount;
        };
        var launch = Launch(rig);
        var lease = launch.SlotLease!;

        var result = rig.Session.Detach();

        Assert.Equal(DetachOutcome.Detached, result.Outcome);
        Assert.Equal(Kit.Identity, result.Identity);
        Assert.Equal(launch.LaunchRecordId, result.LaunchRecordId);
        Assert.Equal(ManagedEtabsRecordPhase.Detached, recordAtCommit!.Phase);
        Assert.False(guardDisposedAtCommit, "the guard is retired only AFTER the durable write");
        Assert.Equal(0, clearsAtCommit);
        Assert.True(rig.L.Guards.Activated[0].Disposed);
        Assert.Null(rig.L.Records.Record);
        Assert.Equal(1, rig.L.Records.ClearCount);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.True(lease.HandedOff);
        Assert.False(lease.ExitConfirmed, "a hand-off is never an exit confirmation");
        Assert.True(rig.L.Process.Disposed);
        Assert.Equal(EtabsDisposition.Detached, rig.Session.Disposition);
        Assert.Null(rig.Session.ProcessId);
        Assert.False(rig.Session.IsStarted);
    }

    // Test 26: exactly one of detach or exit wins, whichever order.
    [Fact]
    public void DetachAndShutdownHaveOneWinnerInBothOrders()
    {
        // Detach first, held before its write: a concurrent shutdown waits for the outcome
        // and then does nothing destructive.
        var first = Ready();
        using var hold = new ManualResetEventSlim(false);
        first.Session.BeforeDetachWrite = () => hold.Signalled("the test to release the detach");
        var detaching = Task.Run(first.Session.Detach);
        DetachTests.WaitFor(() => Launch(first).Lifecycle == LaunchLifecycle.Detaching, "the detach to win the arbiter");
        var shutdown = Kit.OnDedicatedThread(first.Session.Shutdown);
        Thread.Sleep(80);
        Assert.False(shutdown.IsCompleted, "the shutdown waits for the detach outcome");

        hold.Set();
        var detached = detaching.Within("the detach");
        var answer = shutdown.Within("the shutdown");

        Assert.Equal(DetachOutcome.Detached, detached.Outcome);
        Assert.True(answer.Success);
        Assert.Equal(EtabsDisposition.Detached, answer.Data.Disposition);
        Assert.False(answer.Data.ProcessExitConfirmed);
        Assert.Equal(0, first.L.Process.KillCount);
        Assert.Equal(0, first.L.Api.ExitCount);

        // Shutdown first: the detach finds Terminating/Ended and does nothing.
        var second = Ready();
        var exited = second.Session.Shutdown();
        var lateDetach = second.Session.Detach();

        Assert.True(exited.Success);
        Assert.True(exited.Data.ProcessExitConfirmed);
        Assert.Equal(DetachOutcome.Failed, lateDetach.Outcome);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.DetachFailed, lateDetach.ErrorCode);
        Assert.DoesNotContain(
            second.L.Records.Writes,
            record => record.Phase == ManagedEtabsRecordPhase.Detached);
    }

    // A failure BEFORE the durable write started rolls back and kills nothing.
    [Fact]
    public void AFailureBeforeTheWriteStartedRollsBackToReadyAndKillsNothing()
    {
        var rig = Ready();
        rig.L.Records.Record = null; // the record is no longer this launch's

        var result = rig.Session.Detach();

        Assert.Equal(DetachOutcome.Failed, result.Outcome);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.DetachFailed, result.ErrorCode);
        Assert.Equal(LaunchLifecycle.Ready, Launch(rig).Lifecycle);
        Assert.Equal(EtabsDisposition.Owned, rig.Session.Disposition);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.DoesNotContain(
            rig.L.Records.Writes,
            record => record.Phase == ManagedEtabsRecordPhase.Detached);
        Assert.True(rig.Session.IsStarted, "a rolled-back detach leaves the instance usable");
    }

    // Test 69a: a timeout before the write started rolls back, and the late worker never writes.
    [Fact]
    public void ADetachTimeoutBeforeTheWriteRollsBackAndALateWorkerNeverWrites()
    {
        var rig = Ready(budget: TimeSpan.FromMilliseconds(150));
        using var hold = new ManualResetEventSlim(false);
        rig.Session.BeforeDetachWrite = () => hold.Signalled("the test to release the stuck worker");

        var result = rig.Session.Detach();

        Assert.Equal(DetachOutcome.Failed, result.Outcome);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.DetachFailed, result.ErrorCode);
        Assert.Equal(LaunchLifecycle.Ready, Launch(rig).Lifecycle);

        hold.Set();
        Thread.Sleep(300);

        Assert.DoesNotContain(
            rig.L.Records.Writes,
            record => record.Phase == ManagedEtabsRecordPhase.Detached);
        Assert.Equal(LaunchLifecycle.Ready, Launch(rig).Lifecycle);
        Assert.Equal(0, rig.L.Process.KillCount);

        // And a later detach is a fresh attempt that can succeed.
        rig.Session.BeforeDetachWrite = null;
        rig.Session.DetachBudget = TimeSpan.FromSeconds(5);
        Assert.Equal(DetachOutcome.Detached, rig.Session.Detach().Outcome);
    }

    // Test 69b: a timeout while the write is in flight is Uncertain: no rollback, nothing destroyed.
    [Fact]
    public void ADetachTimeoutDuringTheWriteIsUncertainAndNeverRolledBack()
    {
        var rig = Ready(budget: TimeSpan.FromMilliseconds(1500));
        using var writing = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        rig.L.Records.OnWrite = record =>
        {
            if (record.Phase == ManagedEtabsRecordPhase.Detached)
            {
                writing.Set();
                release.Signalled("the test to release the stalled write");
            }
        };

        var result = rig.Session.Detach();

        Assert.Equal(DetachOutcome.Uncertain, result.Outcome);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.DetachUncertain, result.ErrorCode);
        Assert.Equal(LaunchLifecycle.DetachUncertain, Launch(rig).Lifecycle);
        Assert.Equal(EtabsDisposition.Detaching, rig.Session.Disposition);

        release.Set();
        WaitFor(
            () => rig.L.Records.Writes.ToArray().Any(r => r.Phase == ManagedEtabsRecordPhase.Detached),
            "the stalled write to land");

        // Review P2-8: the late successful write is committed handoff evidence, not a stranded
        // "detaching" with the record already cleared.
        WaitFor(() => Launch(rig).Lifecycle == LaunchLifecycle.Detached, "the late detach to publish its hand-off");

        Assert.Equal(EtabsDisposition.Detached, rig.Session.Disposition);
        Assert.Null(rig.Session.ProcessId);
        var later = rig.Session.Shutdown();
        Assert.True(later.Success);
        Assert.Equal(EtabsDisposition.Detached, later.Data.Disposition);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
    }

    // Test 69c: a timeout after the commit is handed-off; later steps are only logged.
    [Fact]
    public void ADetachTimeoutAfterTheCommitIsHandedOff()
    {
        var rig = Ready(budget: TimeSpan.FromMilliseconds(1500));
        using var hold = new ManualResetEventSlim(false);
        rig.Session.AfterDetachWrite = () => hold.Signalled("the test to release the slow best-effort steps");

        var result = rig.Session.Detach();

        Assert.Equal(DetachOutcome.Detached, result.Outcome);
        Assert.Equal(LaunchLifecycle.Detached, Launch(rig).Lifecycle);
        Assert.Equal(EtabsDisposition.Detached, rig.Session.Disposition);
        Assert.Equal(0, rig.L.Process.KillCount);
        hold.Set();
    }

    // Tests 27/53b: a failed durable write under a concurrent shutdown or EOF kills nothing.
    [Fact]
    public void AFailedDetachWriteUnderConcurrentShutdownOrEofKillsAndExitsNothing()
    {
        var rig = Ready();
        rig.L.Records.WriteException = new IOException("disk full");

        var result = rig.Session.Detach();

        Assert.Equal(DetachOutcome.Uncertain, result.Outcome);

        // Test 70: a shutdown (normal, blocked-worker) and EOF all do nothing destructive.
        var normal = rig.Session.Shutdown();
        var blocked = rig.Session.ShutdownWhileWorkerBlocked();

        foreach (var answer in new[] { normal, blocked })
        {
            Assert.False(answer.Success);
            Assert.Equal(ManagedEtabsShutdownErrorCodes.DetachUncertain, answer.ErrorCode);
            Assert.True(answer.Data.RecordRetained);
            Assert.False(answer.Data.ProcessExitConfirmed);
            Assert.Equal(EtabsDisposition.Detaching, answer.Data.Disposition);
        }

        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.Equal(LaunchLifecycle.DetachUncertain, Launch(rig).Lifecycle);
    }

    [Fact]
    public void ACommandAfterADetachNeverStartsUsesOrEndsTheInstance()
    {
        var rig = Ready();
        Assert.Equal(DetachOutcome.Detached, rig.Session.Detach().Outcome);

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.SessionDetached, error.Code);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.Equal(1, rig.L.Spawner.StartCount);
    }

    [Fact]
    public void ADetachIsIdempotentAndASecondOneIsTheSameAnswer()
    {
        var rig = Ready();

        var first = rig.Session.Detach();
        var second = rig.Session.Detach();

        Assert.Equal(DetachOutcome.Detached, first.Outcome);
        Assert.Equal(DetachOutcome.Detached, second.Outcome);
        Assert.Equal(first.LaunchRecordId, second.LaunchRecordId);
        Assert.Equal(1, rig.L.Records.ClearCount);
    }

    // C5: the disposition and the launch generation the status reports.
    [Fact]
    public void TheDispositionIsNoneBeforeAnInstanceOwnedWhileHeldAndDetachedAfter()
    {
        var rig = new SessionRig();
        Assert.Equal(EtabsDisposition.None, rig.Session.Disposition);
        Assert.Null(rig.Session.LaunchRecordId);

        _ = rig.Session.GetOrStartOwned();
        Assert.Equal(EtabsDisposition.Owned, rig.Session.Disposition);
        Assert.Equal(Launch(rig).LaunchRecordId, rig.Session.LaunchRecordId);

        _ = rig.Session.Detach();
        Assert.Equal(EtabsDisposition.Detached, rig.Session.Disposition);
    }

    // C3: userVisible is durable BEFORE the reveal is issued (test 30).
    [Fact]
    public void UserVisibleIsPersistedBeforeTheRevealAndAFailedWriteRevealsNothing()
    {
        var rig = Ready();
        var revealed = rig.Session.RevealForExplicitUserRequest();

        Assert.True(revealed.Success, revealed.Error);
        Assert.True(rig.L.Records.Record!.UserVisible);
        Assert.True(rig.Session.IsUserVisible);
        var events = rig.L.Events;
        var persisted = events.LastIndexOf($"record-write:{ManagedEtabsRecordPhase.Ready}");
        Assert.True(persisted >= 0 && persisted < events.IndexOf("window-guard-begin-explicit-reveal"));
        Assert.True(persisted < events.IndexOf("unhide"));

        var blocked = Ready();
        blocked.L.Events.Clear();
        blocked.L.Records.WriteException = new IOException("read-only volume");

        var refused = blocked.Session.RevealForExplicitUserRequest();

        Assert.False(refused.Success);
        Assert.Contains(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, refused.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unhide", blocked.L.Events);
        Assert.DoesNotContain("window-guard-begin-explicit-reveal", blocked.L.Events);
        Assert.False(blocked.Session.IsUserVisible);
        Assert.True(blocked.Session.IsStarted, "a refused reveal leaves the hidden session usable");
    }

    // Review P2-A: the explicit reveal disarms the post-Ready blocker watch.
    [Fact]
    public void TheRevealDisarmsThePostReadyStartupBlockerWatch()
    {
        var rig = Ready();
        Assert.True(rig.L.Cell.StartupBlockerArmed);

        _ = rig.Session.RevealForExplicitUserRequest();

        Assert.False(rig.L.Cell.StartupBlockerArmed);
    }
}

/// <summary>Cards C3/v2.3 §3: a recovery pass detaches, and never terminates, a handed-off identity.</summary>
public sealed class OrphanDetachTests
{
    private static ManagedEtabsSessionRecord Record(ManagedEtabsRecordPhase phase, bool? userVisible = null) => new(
        1,
        Kit.Identity.Pid,
        Kit.Identity.ProcessStartTimeUtc,
        Kit.Identity.ExecutablePath,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow)
    {
        Phase = phase,
        UserVisible = userVisible
    };

    [Theory]
    [InlineData(ManagedEtabsRecordPhase.Ready, true)]
    [InlineData(ManagedEtabsRecordPhase.Detached, null)]
    [InlineData(ManagedEtabsRecordPhase.Detached, true)]
    public void ALiveUserVisibleOrDetachedIdentityIsClearedReportedAndNeverTerminated(
        ManagedEtabsRecordPhase phase,
        bool? userVisible)
    {
        var records = new MemoryRecordStore { Record = Record(phase, userVisible) };
        var census = new FakeCensus { Live = Kit.Identity };
        var cell = new OrphanDispositionCell();

        var result = new OrphanSessionCleaner(records, census, cell).Clean();

        Assert.True(result.Success);
        Assert.Equal(0, census.TerminateExactCalls);
        Assert.Null(records.Record);
        Assert.Equal(Kit.Identity, cell.Detached);
        Assert.Equal(EtabsDisposition.Detached, result.Data.Disposition);
        Assert.False(result.Data.ProcessExitConfirmed);
    }

    // Test 28: a crash after the detached phase is never killed by recovery - even if the
    // identity is live. A dead or reused pid just clears.
    [Fact]
    public void ADetachedRecordWhoseProcessIsGoneOrReusedIsClearedWithoutAnyKill()
    {
        foreach (var live in new ManagedProcessIdentity?[]
        {
            null,
            Kit.Identity with { ProcessStartTimeUtc = Kit.Identity.ProcessStartTimeUtc.AddMinutes(5) }
        })
        {
            var records = new MemoryRecordStore { Record = Record(ManagedEtabsRecordPhase.Detached) };
            var census = new FakeCensus { Live = live };
            var cell = new OrphanDispositionCell();

            var result = new OrphanSessionCleaner(records, census, cell).Clean();

            Assert.True(result.Success);
            Assert.Equal(0, census.TerminateExactCalls);
            Assert.Null(records.Record);
            Assert.Null(cell.Detached);
        }
    }

    // The ordinary orphan (no userVisible, not detached) is still ended by exact identity.
    [Fact]
    public void AnOrdinaryOrphanIsStillTerminatedByExactIdentity()
    {
        var records = new MemoryRecordStore { Record = Record(ManagedEtabsRecordPhase.Ready) };
        var census = new FakeCensus { Live = Kit.Identity };

        var result = new OrphanSessionCleaner(records, census, new OrphanDispositionCell()).Clean();

        Assert.True(result.Success);
        Assert.Equal(1, census.TerminateExactCalls);
    }
}
