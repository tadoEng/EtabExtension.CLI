using System.Diagnostics;
using System.Text.Json;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>#747-D fix round 1 (Codex review of 90022fa): one integration test per finding.</summary>
public sealed class FixRound1Tests747d
{
    private const string Working = @"D:\Project\.etabs-ext\main\working\model.edb";

    private static SessionRig Visible()
    {
        var rig = new SessionRig();
        rig.Session.DetachSettleWait = TimeSpan.FromSeconds(5);
        _ = rig.Session.GetOrStartOwned();
        Assert.True(rig.Session.RevealForExplicitUserRequest().Success);
        Assert.True(rig.Session.IsUserVisible);
        return rig;
    }

    // P1-1: the failed EOF detach leaves ETABS running - and so does the full scope teardown.
    [Fact]
    public void AFailedEofDetachSurvivesTheFullDiScopeTeardown()
    {
        var rig = Visible();
        rig.L.Records.Record = null; // the detach fails before its write and rolls back
        var services = new ServiceCollection();
        services.AddScoped<IEtabsSession>(_ => rig.Session);
        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IEtabsSession>();
        var coordinator = new ServeShutdownCoordinator(
            new ProbeWorker(busy: false),
            session,
            new ProbeWatchdog(),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(200));

        var eof = coordinator.EndOfInputAsync(ServeEofPolicy.DetachVisible).Within("EOF");
        scope.Dispose(); // disposes the session exactly as the daemon's scope does

        Assert.False(eof.Success);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.False(rig.L.Process.HasExited);
    }

    // P1-2: discard consent waives change detection, never document identity.
    [Theory]
    [InlineData("other-model")]
    [InlineData("expected-document")]
    [InlineData("no-stamp")]
    public void TheDiscardFallbackStillRefusesWhenTheOpenModelIsNotTheOne(string scenario)
    {
        var api = new EventedFileApi([]) { CurrentPath = Working };
        var session = new ProbeSession();
        var service = new ShutdownIfUnchangedService(
            session,
            new ProbeWatchdog(),
            new FixedApiFactory(api),
            new ShutdownIfUnchangedRegistry(),
            new FixedStamps(new SavedFileStamp("0000abcd-0000000000001234", 4096, DateTimeOffset.UtcNow)),
            TimeSpan.FromSeconds(10),
            (_, _) => new FileIdentityResult(FileIdentityMatch.Different, 0));
        var request = new ShutdownIfUnchangedRequest
        {
            FilePath = Working,
            DiscardConfirmed = true,
            SavedStamp = scenario == "no-stamp"
                ? null
                : new SavedStamp("0000abcd-0000000000001234", 4096, DateTimeOffset.UtcNow, "unsupported", false),
            ExpectedDocument = scenario == "expected-document" ? new ExpectedDocument("0000ffff-0000000000000001") : null
        };
        if (scenario is "other-model" or "no-stamp")
        {
            api.CurrentPath = @"D:\Project\.etabs-ext\main\working\switched.edb"; // the engineer switched models
        }

        var result = service.RunAsync(request, 1, check => Task.Run(check)).Within("the command");

        var failure = Assert.IsType<Result>(result, exactMatch: false);
        Assert.Contains(ShutdownIfUnchangedCodes.ChangedSinceSave, failure.Error, StringComparison.Ordinal);
        Assert.Equal(0, session.NormalShutdowns);
    }

    [Fact]
    public void TheDiscardFallbackExitsWhenTheOpenModelIsTheRequestedOne()
    {
        var session = new ProbeSession();
        var service = new ShutdownIfUnchangedService(
            session,
            new ProbeWatchdog(),
            new FixedApiFactory(new EventedFileApi([]) { CurrentPath = Working }),
            new ShutdownIfUnchangedRegistry());

        var result = service.RunAsync(
            new ShutdownIfUnchangedRequest { FilePath = Working, DiscardConfirmed = true },
            1,
            check => Task.Run(check)).Within("the command");

        Assert.IsType<Result<ManagedEtabsShutdownData>>(result);
        Assert.Equal(1, session.NormalShutdowns);
    }

    // P1-3: through the loop, behind a blocked ordinary request, a C10 is registered at intake.
    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    public void AQueuedC10CanBeCancelledOrExpireAndNeverExitsAfterTheBlockedRequestReturns(string how)
    {
        var save = new BlockingSaveService();
        var session = new ProbeSession { Started = true };
        var registry = new ShutdownIfUnchangedRegistry();
        var service = new ShutdownIfUnchangedService(
            session,
            new ProbeWatchdog(),
            new FixedApiFactory(new EventedFileApi([]) { CurrentPath = Working }),
            registry,
            null,
            how == "expire" ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(20),
            null);
        var dispatcher = DispatcherRig.Create(
            session,
            new ProbeWatchdog(),
            c10: registry,
            service: service,
            save: save,
            operations: new SerialOperations());
        var loop = new ServeLoop(
            dispatcher,
            new RecordingCoordinator(),
            new ManagedEtabsStartIntentScope(),
            capabilities => new ServeHandshake(
                "etab-cli-serve", 1, "0.1.0", "0.1.0+gtest", Environment.ProcessId,
                Path.GetFullPath(Environment.ProcessPath!), capabilities),
            TextWriter.Null);
        using var reader = new ScriptedLines();
        var writer = new FrameCollector();
        var run = loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);

        reader.Send($"{{\"id\":1,\"command\":\"save-model\",\"request\":{{\"filePath\":\"{Working.Replace("\\", "\\\\")}\"}}}}");
        DetachTests.WaitFor(() => save.Started, "the ordinary request to block the STA");
        reader.Send($"{{\"id\":2,\"command\":\"shutdown-if-unchanged\",\"request\":{{\"filePath\":\"{Working.Replace("\\", "\\\\")}\",\"discardConfirmed\":true}}}}");
        DetachTests.WaitFor(() => reader.LinesRead == 2, "the C10 frame to be read");
        if (how == "cancel")
        {
            DetachTests.WaitFor(() => registry.Cancel(2) == ShutdownIfUnchangedState.Abandoned, "the queued C10 to be cancellable by request id");
        }

        DetachTests.WaitFor(() => writer.Frame(2) is not null, "the C10 answer while the ordinary request is still blocked");
        Assert.Null(writer.Frame(1));
        Assert.Contains(
            how == "cancel" ? ShutdownIfUnchangedCodes.Abandoned : ShutdownIfUnchangedCodes.CheckTimeout,
            writer.Frame(2)!.Value.GetProperty("error").GetString(),
            StringComparison.Ordinal);

        save.Release.SetResult(Result.Ok(new SaveModelData()));
        DetachTests.WaitFor(() => writer.Frame(1) is not null, "the ordinary request's own answer");
        Thread.Sleep(150); // the stale C10 body now runs and must find the arbiter taken
        reader.Close();
        run.Within("the loop");

        Assert.Equal(0, session.NormalShutdowns);
        Assert.Equal(0, session.BlockedShutdowns);
    }

    // P1-4: a shutdown that observed Ready must WIN Ready->Terminating; a detach that completed
    // in between (after an earlier rollback) owns the instance and nothing is exited.
    [Fact]
    public void AShutdownThatObservedReadyAcrossARollbackAndARetriedDetachExitsNothing()
    {
        var rig = new SessionRig();
        _ = rig.Session.GetOrStartOwned();

        // First detach: rolled back by its supervisor before its write.
        rig.Session.DetachBudget = TimeSpan.FromMilliseconds(150);
        using var hold = new ManualResetEventSlim(false);
        rig.Session.BeforeDetachWrite = () => hold.Signalled("the stuck first detach");
        Assert.Equal(DetachOutcome.Failed, rig.Session.Detach().Outcome);
        hold.Set();
        rig.Session.BeforeDetachWrite = null;
        rig.Session.DetachBudget = TimeSpan.FromSeconds(5);

        // The shutdown observes Ready and pauses just before its fence CAS.
        using var observed = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var first = 0;
        rig.Session.BetweenFenceObservationAndCas = () =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                observed.Set();
                resume.Signalled("the test to let the shutdown continue");
            }
        };
        var shutdown = Kit.OnDedicatedThread(rig.Session.Shutdown);
        observed.Signalled("the shutdown to observe Ready");

        // The retried detach completes its durable write and hands over, meanwhile.
        Assert.Equal(DetachOutcome.Detached, rig.Session.Detach().Outcome);
        resume.Set();
        var answer = shutdown.Within("the shutdown");

        Assert.True(answer.Success);
        Assert.Equal(EtabsDisposition.Detached, answer.Data.Disposition);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.False(rig.L.Process.HasExited);
    }

    // P2-5: the work envelope's certification may not end the session under a refused C10.
    [Fact]
    public void ARefusedC10IsNonDestructiveEvenThroughTheRealEnvelopeWithAFailingCertification()
    {
        var rig = new SessionRig();
        _ = rig.Session.GetOrStartOwned();
        rig.L.Guards.Activated[0].Exposure = new ManagedEtabsExposureEvidence(true, 1, null, null);
        var watchdog = new ProbeWatchdog
        {
            Check = new EtabsDialogCheck(
                EtabsDialogCheckKind.Modal,
                new EtabsModalDialogObservation("Define Load Patterns", "x", false),
                null)
        };
        var registry = new ShutdownIfUnchangedRegistry();
        var service = new ShutdownIfUnchangedService(
            rig.Session,
            watchdog,
            new FixedApiFactory(new EventedFileApi([]) { CurrentPath = Working }),
            registry);
        var dispatcher = DispatcherRig.Create(rig.Session, watchdog, c10: registry, service: service);

        var result = dispatcher.DispatchAsync(
            "shutdown-if-unchanged",
            JsonSerializer.SerializeToElement(new { filePath = Working, discardConfirmed = true }),
            CancellationToken.None).Within("the command");

        var failure = Assert.IsType<Result>(result, exactMatch: false);
        Assert.Contains(ShutdownIfUnchangedCodes.ModalOpen, failure.Error, StringComparison.Ordinal);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.True(rig.Session.IsStarted);
    }

    // P2-6: an ordinary exit that STARTED and never returns is still bounded.
    [Fact]
    public void AnOrdinaryExitThatNeverReturnsIsTakenOverByTheBoundedCleanupWithinTheDeadline()
    {
        using var never = new ManualResetEventSlim(false);
        var session = new ProbeSession { NormalShutdown = () => { never.Wait(TimeSpan.FromSeconds(30)); return Shutdowns.Ok(); } };
        var coordinator = new ServeShutdownCoordinator(
            new ProbeWorker(busy: false),
            session,
            new ProbeWatchdog(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(200),
            null,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(1500));
        var clock = Stopwatch.StartNew();

        var result = coordinator.ShutdownAsync().Within("the supervised shutdown");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.NormalShutdowns);
        Assert.Equal(1, session.BlockedShutdowns);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        never.Set();
    }

    [Fact]
    public void AnExitAndItsCleanupThatBothNeverReturnAnswerUnconfirmedAtTheDeadline()
    {
        using var never = new ManualResetEventSlim(false);
        var session = new ProbeSession
        {
            NormalShutdown = () => { never.Wait(TimeSpan.FromSeconds(30)); return Shutdowns.Ok(); },
            BlockedShutdown = () => { never.Wait(TimeSpan.FromSeconds(30)); return Shutdowns.Ok(); }
        };
        var coordinator = new ServeShutdownCoordinator(
            new ProbeWorker(busy: false),
            session,
            new ProbeWatchdog(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(200),
            null,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(1200));
        var clock = Stopwatch.StartNew();

        var result = coordinator.ShutdownAsync().Within("the supervised shutdown");

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownState.ProcessExitUnconfirmed, result.Data!.State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
        never.Set();
    }

    // P2-7: an abandoned C10 whose body is still blocked answers the typed abandonment AT ONCE.
    [Fact]
    public void AnAlreadyAbandonedC10WhoseBodyIsStillBlockedAnswersAtOnce()
    {
        var session = new ProbeSession();
        var registry = new ShutdownIfUnchangedRegistry();
        var service = new ShutdownIfUnchangedService(
            session,
            new ProbeWatchdog(),
            new FixedApiFactory(new EventedFileApi([]) { CurrentPath = Working }),
            registry,
            null,
            TimeSpan.FromSeconds(20),
            null);
        using var hold = new ManualResetEventSlim(false);
        service.BeforeCheck = () => hold.Signalled("the test to release the blocked body");

        var running = service.RunAsync(
            new ShutdownIfUnchangedRequest { FilePath = Working, DiscardConfirmed = true },
            4,
            check => Task.Run(check));
        Thread.Sleep(100);
        Assert.Equal(ShutdownIfUnchangedState.Abandoned, registry.Cancel(4));
        var clock = Stopwatch.StartNew();

        var answer = running.Within("the abandonment answer");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Contains(ShutdownIfUnchangedCodes.Abandoned, Assert.IsType<Result>(answer, exactMatch: false).Error, StringComparison.Ordinal);
        hold.Set();
        Thread.Sleep(100);
        Assert.Equal(0, session.NormalShutdowns);
    }

    [Fact]
    public void AWonExitThatNeverReturnsIsAnsweredUnconfirmedWithinItsExitBudget()
    {
        using var never = new ManualResetEventSlim(false);
        var session = new ProbeSession { NormalShutdown = () => { never.Wait(TimeSpan.FromSeconds(30)); return Shutdowns.Ok(); } };
        var service = new ShutdownIfUnchangedService(
            session,
            new ProbeWatchdog(),
            new FixedApiFactory(new EventedFileApi([]) { CurrentPath = Working }),
            new ShutdownIfUnchangedRegistry(),
            null,
            TimeSpan.FromMilliseconds(200),
            null,
            TimeSpan.FromMilliseconds(600));

        var answer = service.RunAsync(
            new ShutdownIfUnchangedRequest { FilePath = Working, DiscardConfirmed = true },
            1,
            check => Task.Run(check)).Within("the exit answer");

        Assert.Contains(ShutdownIfUnchangedService.ExitUnconfirmedCode, Assert.IsType<Result>(answer, exactMatch: false).Error, StringComparison.Ordinal);
        never.Set();
    }

    // P2-7 arithmetic: Checking + the exit bound fit the inherited 30 s response allowance.
    [Fact]
    public void TheC10CheckingBudgetPlusTheExitBoundFitTheThirtySecondResponseAllowance()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), ShutdownIfUnchangedService.DefaultCheckBudget);
        Assert.Equal(TimeSpan.FromSeconds(21), ShutdownIfUnchangedService.DefaultExitBudget);
        Assert.True(
            ShutdownIfUnchangedService.DefaultCheckBudget + ShutdownIfUnchangedService.DefaultExitBudget
                <= TimeSpan.FromSeconds(30));
        Assert.True(ServeShutdownCoordinator.StartedExitDeadline + StaExecutionWorker.DefaultDisposeJoinTimeout
            <= TimeSpan.FromSeconds(22));
    }

    // P2-8: a late successful detach is committed hand-off evidence for status, shutdown and EOF.
    [Fact]
    public void ALateSuccessfulDetachFromUncertainIsReportedDetachedByStatusAndEof()
    {
        var rig = new SessionRig();
        rig.Session.DetachBudget = TimeSpan.FromMilliseconds(1500);
        _ = rig.Session.GetOrStartOwned();
        using var release = new ManualResetEventSlim(false);
        rig.L.Records.OnWrite = record =>
        {
            if (record.Phase == ManagedEtabsRecordPhase.Detached)
            {
                release.Signalled("the stalled write");
            }
        };
        Assert.Equal(DetachOutcome.Uncertain, rig.Session.Detach().Outcome);
        Assert.Equal(EtabsDisposition.Detaching, rig.Session.Disposition);

        release.Set();
        DetachTests.WaitFor(() => rig.Session.Disposition == EtabsDisposition.Detached, "the late write to publish its hand-off");
        var coordinator = new ServeShutdownCoordinator(
            new ProbeWorker(busy: false),
            rig.Session,
            new ProbeWatchdog(),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(200));

        var eof = coordinator.EndOfInputAsync(ServeEofPolicy.Exit).Within("EOF");

        Assert.True(eof.Success, eof.Error);
        Assert.Equal(EtabsDisposition.Detached, eof.Data!.Disposition);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
    }

    private sealed class BlockingSaveService : EtabExtension.CLI.Features.SaveModel.ISaveModelService
    {
        private int _started;

        public bool Started => Volatile.Read(ref _started) == 1;

        public TaskCompletionSource<Result<SaveModelData>> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<SaveModelData>> SaveModelAsync(string filePath) => Release.Task;

        public Task<Result<SaveModelData>> SaveModelOnAppAsync(EtabSharp.Core.ETABSApplication app, string filePath)
        {
            Volatile.Write(ref _started, 1);
            return Release.Task;
        }
    }
}
