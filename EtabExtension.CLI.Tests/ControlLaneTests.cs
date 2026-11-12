using System.Diagnostics;
using System.Text.Json;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>#747-D C8: the serve loop never awaits a command before reading the next frame.</summary>
public sealed class ControlLaneTests
{
    private sealed class LaneDispatcher : IServeDispatcher
    {
        private readonly object _gate = new();
        private readonly List<string> _commands = [];

        public IReadOnlyCollection<string> Capabilities { get; } = ["get-status", "cancel-operation", "slow", "save-model", "b"];

        /// <summary>The synchronous command that is "in flight" until the test releases it.</summary>
        public TaskCompletionSource<object> Slow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_gate)
                {
                    return _commands.ToList();
                }
            }
        }

        public Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
        {
            lock (_gate)
            {
                _commands.Add(command);
            }

            return command is "slow" or "save-model"
                ? Slow.Task
                : Task.Run(async () =>
                {
                    await Task.Delay(5, ct);
                    return (object)Result.Ok(new ControlEcho(command));
                }, ct);
        }
    }

    private static ServeHandshake Handshake(IReadOnlyList<string> capabilities) => new(
        "etab-cli-serve",
        1,
        "0.1.0",
        "0.1.0+gtest",
        Environment.ProcessId,
        Path.GetFullPath(Environment.ProcessPath!),
        capabilities);

    private static string Line(long id, string command) => $"{{\"id\":{id},\"command\":\"{command}\",\"request\":{{}}}}";

    private static (ServeLoop Loop, ScriptedLines Reader, FrameCollector Writer, LaneDispatcher Dispatcher, RecordingCoordinator Coordinator)
        Build(ServeEofPolicy policy = ServeEofPolicy.Exit)
    {
        var dispatcher = new LaneDispatcher();
        var coordinator = new RecordingCoordinator();
        var loop = new ServeLoop(
            dispatcher,
            coordinator,
            new ManagedEtabsStartIntentScope(),
            Handshake,
            TextWriter.Null,
            policy);
        return (loop, new ScriptedLines(), new FrameCollector(), dispatcher, coordinator);
    }

    private static async Task<Task> StartSlowAsync(
        ServeLoop loop,
        ScriptedLines reader,
        FrameCollector writer,
        LaneDispatcher dispatcher)
    {
        var run = loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);
        reader.Send(Line(1, "slow"));
        await Task.Run(() => DetachTests.WaitFor(
            () => dispatcher.Commands.Contains("slow"),
            "the synchronous command to be in flight"));
        return run;
    }

    // Test 59: shutdown is read, and answered, while a synchronous command is still running.
    [Fact]
    public async Task TheDaemonAcceptsShutdownWhileASynchronousCommandRuns()
    {
        var (loop, reader, writer, dispatcher, coordinator) = Build();
        using var _ = reader;
        var run = await StartSlowAsync(loop, reader, writer, dispatcher);

        reader.Send(Line(2, "shutdown"));
        await Task.Run(() => DetachTests.WaitFor(() => writer.Frame(2) is not null, "the shutdown answer"));

        Assert.True(coordinator.ShutdownCount >= 1);
        Assert.Equal(0, coordinator.EofCount);
        Assert.Null(writer.Frame(1));
        Assert.True(writer.Frame(2)!.Value.GetProperty("success").GetBoolean());
        dispatcher.Slow.SetResult(Result.Ok());
        run.Within("the loop to end after its shutdown");
    }

    [Fact]
    public async Task GetStatusAndCancelAreAnsweredWhileASynchronousCommandRuns()
    {
        var (loop, reader, writer, dispatcher, _) = Build();
        using var _ = reader;
        var run = await StartSlowAsync(loop, reader, writer, dispatcher);

        reader.Send(Line(2, "get-status"));
        reader.Send(Line(3, "cancel-operation"));
        await Task.Run(() => DetachTests.WaitFor(
            () => writer.Frame(2) is not null && writer.Frame(3) is not null,
            "the control answers"));

        Assert.Null(writer.Frame(1));
        Assert.Equal("get-status", writer.Frame(2)!.Value.GetProperty("data").GetProperty("value").GetString());
        dispatcher.Slow.SetResult(Result.Ok());
        await Task.Run(() => DetachTests.WaitFor(() => writer.Frame(1) is not null, "the slow command's own answer"));
        reader.Close();
        run.Within("the loop to end");
    }

    [Fact]
    public async Task DetachIsAcceptedAndAnsweredWhileASynchronousCommandRuns()
    {
        var (loop, reader, writer, dispatcher, coordinator) = Build();
        using var _ = reader;
        var run = await StartSlowAsync(loop, reader, writer, dispatcher);

        reader.Send(Line(2, "detach"));
        await Task.Run(() => DetachTests.WaitFor(() => writer.Frame(2) is not null, "the detach answer"));

        var frame = writer.Frame(2)!.Value;
        Assert.Equal(1, coordinator.DetachCount);
        Assert.True(frame.GetProperty("success").GetBoolean());
        Assert.True(frame.GetProperty("data").GetProperty("detached").GetBoolean());
        Assert.Equal("detached", frame.GetProperty("data").GetProperty("disposition").GetString());
        Assert.Equal(Kit.Identity.Pid, frame.GetProperty("data").GetProperty("identity").GetProperty("pid").GetInt32());
        Assert.Null(writer.Frame(1));
        dispatcher.Slow.SetResult(Result.Ok());
        reader.Close();
        run.Within("the loop to end");
    }

    // Ordinary commands keep their one-at-a-time, arrival-order semantic: control intake does
    // not make ETABS work concurrent.
    [Fact]
    public async Task OrdinaryCommandsStillRunOneAtATimeBehindASlowOne()
    {
        var (loop, reader, writer, dispatcher, _) = Build();
        using var _ = reader;
        var run = await StartSlowAsync(loop, reader, writer, dispatcher);

        reader.Send(Line(2, "b"));
        await Task.Run(() => DetachTests.WaitFor(() => reader.LinesRead == 2, "the next frame to be READ"));
        await Task.Delay(100);

        Assert.DoesNotContain("b", dispatcher.Commands);
        dispatcher.Slow.SetResult(Result.Ok());
        await Task.Run(() => DetachTests.WaitFor(() => writer.Frame(2) is not null, "the queued command to run"));
        reader.Close();
        run.Within("the loop to end");
        Assert.Equal(["slow", "b"], dispatcher.Commands.ToArray());
    }

    // Every reply is ONE whole frame under the write lock: concurrent replies never interleave.
    [Fact]
    public async Task ConcurrentRepliesAreWholeFramesWrittenUnderTheWriteLock()
    {
        var (loop, reader, writer, _, _) = Build();
        using var _ = reader;
        writer.WriteLinger = TimeSpan.FromMilliseconds(30);
        var run = loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);

        for (var id = 1; id <= 6; id++)
        {
            reader.Send(Line(id, "get-status"));
        }

        await Task.Run(() => DetachTests.WaitFor(() => writer.Frames().Count == 6, "all six replies"));
        reader.Close();
        run.Within("the loop to end");

        Assert.Equal(1, writer.MaxConcurrentWrites);
        Assert.Equal(
            [1L, 2L, 3L, 4L, 5L, 6L],
            writer.Frames().Select(frame => frame.GetProperty("id").GetInt64()).Order().ToArray());
    }

    // Test 39 (loop part): EOF runs the coordinator's end-of-input at once and never waits for
    // an accepted request first; the run still ends within the bounded post-exit settle.
    [Fact]
    public async Task EofIsBoundedWhileAnAcceptedRequestIsBlockedAndUsesTheConfiguredPolicy()
    {
        var (loop, reader, writer, dispatcher, coordinator) = Build(ServeEofPolicy.DetachVisible);
        using var _ = reader;
        var run = await StartSlowAsync(loop, reader, writer, dispatcher);
        var clock = Stopwatch.StartNew();

        reader.Close();
        await Task.Run(() => DetachTests.WaitFor(() => coordinator.EofCount == 1, "the EOF cleanup"));
        var cleanupAfter = clock.Elapsed;

        Assert.Equal(ServeEofPolicy.DetachVisible, coordinator.EofPolicy);
        Assert.Equal(0, coordinator.ShutdownCount);
        Assert.False(dispatcher.Slow.Task.IsCompleted, "the blocked command is still pending when the cleanup ran");
        Assert.True(cleanupAfter < TimeSpan.FromSeconds(1), $"EOF cleanup waited {cleanupAfter}");
        run.Within("the loop to end without waiting for the blocked command");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the loop took {clock.Elapsed} to end after EOF");
    }

    // Test 61' (daemon part): a save reply that lands after the host stopped waiting is written
    // as one frame on its own id, and NOTHING is chained to it - no shutdown, no detach, no exit.
    [Fact]
    public async Task ALateSaveReplyIsOneFrameOnItsOwnIdAndTriggersNoExit()
    {
        var (loop, reader, writer, dispatcher, coordinator) = Build();
        using var _ = reader;
        var run = loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);
        reader.Send(Line(7, "save-model"));
        await Task.Run(() => DetachTests.WaitFor(() => dispatcher.Commands.Contains("save-model"), "the save to start"));

        await Task.Delay(150); // the host has long given up waiting
        dispatcher.Slow.SetResult(Result.Ok(new ControlEcho("saved")));
        await Task.Run(() => DetachTests.WaitFor(() => writer.Frame(7) is not null, "the late save reply"));

        Assert.Equal("saved", writer.Frame(7)!.Value.GetProperty("data").GetProperty("value").GetString());
        Assert.Equal(0, coordinator.ShutdownCount);
        Assert.Equal(0, coordinator.DetachCount);
        Assert.Equal(0, coordinator.EofCount);
        reader.Close();
        run.Within("the loop to end");
    }
}

/// <summary>#747-D C7a / C2: the bounded exit and the EOF policy, on the real coordinator.</summary>
public sealed class CoordinatorControlTests
{
    private static ServeShutdownCoordinator Coordinator(
        ProbeSession session,
        ProbeWorker worker,
        ProbeWatchdog? watchdog = null,
        TimeSpan? envelope = null,
        TimeSpan? busyWait = null,
        ShutdownIfUnchangedRegistry? c10 = null) => new(
            worker,
            session,
            watchdog ?? new ProbeWatchdog(),
            envelope ?? TimeSpan.FromSeconds(2),
            busyWait ?? TimeSpan.FromMilliseconds(100),
            c10);

    // C7a: a busy STA gets the blocked sequence after the busy wait, with the gate wait spent.
    [Fact]
    public async Task ABusyStaTakesTheBlockedSequenceAfterTheBusyWaitAndNotAfterTheQueue()
    {
        var session = new ProbeSession();
        var worker = new ProbeWorker(busy: true);

        var result = Coordinator(session, worker).ShutdownAsync().Within("the shutdown");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.BlockedShutdowns);
        Assert.Equal(TimeSpan.Zero, session.GateWaitPassed);
        Assert.Equal(0, session.NormalShutdowns);
        Assert.Equal(1, worker.Disposed);
    }

    // The busy wait is the wait for the STA to START the queued exit: a healthy slow exit
    // (a graceful ETABS close) is never cut short by the blocked sequence.
    [Fact]
    public async Task AHealthySlowExitIsNotCutShortByTheBusyWait()
    {
        var session = new ProbeSession
        {
            NormalShutdown = () =>
            {
                Thread.Sleep(450);
                return Shutdowns.Ok();
            }
        };

        var result = Coordinator(session, new ProbeWorker(busy: false)).ShutdownAsync().Within("the shutdown");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.NormalShutdowns);
        Assert.Equal(0, session.BlockedShutdowns);
    }

    // The busy wait is part of the envelope, not on top of it (card C7, the 22 s ceiling).
    [Fact]
    public async Task TheBusyWaitIsInsideTheEnvelopeAndNeverAddedOnTopOfIt()
    {
        var session = new ProbeSession
        {
            BlockedShutdown = () =>
            {
                Thread.Sleep(5_000);
                return Shutdowns.Ok();
            }
        };
        var envelope = TimeSpan.FromMilliseconds(2000);
        var clock = Stopwatch.StartNew();

        var result = Coordinator(session, new ProbeWorker(busy: true), envelope: envelope, busyWait: TimeSpan.FromMilliseconds(1200))
            .ShutdownAsync()
            .Within("the bounded shutdown");

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownState.ProcessExitUnconfirmed, result.Data!.State);
        Assert.True(
            clock.Elapsed < envelope + TimeSpan.FromMilliseconds(700),
            $"took {clock.Elapsed}: the 1200 ms busy wait was added on top of the {envelope} envelope");
    }

    [Fact]
    public async Task EofWithAnUnreachableBusyStaIsBoundedByTheEnvelope()
    {
        var session = new ProbeSession { BlockedShutdown = () => { Thread.Sleep(5_000); return Shutdowns.Ok(); } };
        var clock = Stopwatch.StartNew();

        var result = Coordinator(session, new ProbeWorker(busy: true), envelope: TimeSpan.FromMilliseconds(600))
            .EndOfInputAsync(ServeEofPolicy.Exit)
            .Within("EOF");

        Assert.False(result.Success);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"EOF took {clock.Elapsed}");
    }

    // C2: detach-visible detaches a visible instance and never exits it.
    [Fact]
    public async Task EofWithDetachVisibleDetachesAVisibleInstanceAndExitsNothing()
    {
        var session = new ProbeSession { UserVisible = true };
        var worker = new ProbeWorker(busy: false);

        var result = Coordinator(session, worker).EndOfInputAsync(ServeEofPolicy.DetachVisible).Within("EOF");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.Detaches);
        Assert.Equal(0, session.NormalShutdowns);
        Assert.Equal(0, session.BlockedShutdowns);
        Assert.Equal(Features.GetStatus.Models.EtabsDisposition.Detached, result.Data!.Disposition);
    }

    // Mutation target: exiting after a failed detach.
    [Theory]
    [InlineData(DetachOutcome.Failed, "ETABS_DETACH_FAILED", Features.GetStatus.Models.EtabsDisposition.Owned)]
    [InlineData(DetachOutcome.Uncertain, "ETABS_DETACH_UNCERTAIN", Features.GetStatus.Models.EtabsDisposition.Detaching)]
    public async Task AFailedDetachOnEofEndsTheDaemonWithTheRecordIntactAndExitsNothing(
        DetachOutcome outcome,
        string code,
        Features.GetStatus.Models.EtabsDisposition disposition)
    {
        var session = new ProbeSession
        {
            UserVisible = true,
            DetachBehavior = () => new(outcome, code, "no", null, null)
        };
        var coordinator = Coordinator(session, new ProbeWorker(busy: false));

        var result = coordinator.EndOfInputAsync(ServeEofPolicy.DetachVisible).Within("EOF");
        var finalCleanup = coordinator.ShutdownAsync().Within("the lifecycle's own cleanup");

        Assert.False(result.Success);
        Assert.Equal(disposition, result.Data!.Disposition);
        Assert.True(result.Data.RecordRetained);
        Assert.Same(result, finalCleanup);
        Assert.Equal(0, session.NormalShutdowns);
        Assert.Equal(0, session.BlockedShutdowns);
    }

    [Theory]
    [InlineData(ServeEofPolicy.Exit, true)]
    [InlineData(ServeEofPolicy.DetachVisible, false)]
    public async Task EofTakesTheBoundedExitUnlessAVisibleInstanceIsToBeDetached(ServeEofPolicy policy, bool visible)
    {
        var session = new ProbeSession { UserVisible = visible };

        var result = Coordinator(session, new ProbeWorker(busy: false)).EndOfInputAsync(policy).Within("EOF");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.NormalShutdowns);
        Assert.Equal(0, session.Detaches);
    }

    // A rolled-back detach may be asked for again; an Uncertain one is final and shared.
    [Fact]
    public async Task ARolledBackDetachMayBeRetriedAndAnUncertainOneIsSharedNotRerun()
    {
        var attempts = 0;
        var session = new ProbeSession
        {
            DetachBehavior = () => ++attempts == 1
                ? new(DetachOutcome.Failed, "ETABS_DETACH_FAILED", "rolled back", null, null)
                : new(DetachOutcome.Uncertain, "ETABS_DETACH_UNCERTAIN", "unknown", null, null)
        };
        var coordinator = Coordinator(session, new ProbeWorker(busy: false));

        var first = coordinator.DetachAsync().Within("the first detach");
        var second = coordinator.DetachAsync().Within("the retry");
        var third = coordinator.DetachAsync().Within("the shared answer");

        Assert.Contains("ETABS_DETACH_FAILED", first.Error, StringComparison.Ordinal);
        Assert.Contains("ETABS_DETACH_UNCERTAIN", second.Error, StringComparison.Ordinal);
        Assert.Same(second, third);
        Assert.Equal(2, session.Detaches);
    }

    // A prior explicit detach decides EOF too.
    [Fact]
    public async Task AnExplicitDetachBeforeEofLeavesNothingForTheEofToExit()
    {
        var session = new ProbeSession { UserVisible = true };
        var coordinator = Coordinator(session, new ProbeWorker(busy: false));
        _ = coordinator.DetachAsync().Within("the detach");

        var result = coordinator.EndOfInputAsync(ServeEofPolicy.Exit).Within("EOF");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.Detaches);
        Assert.Equal(0, session.NormalShutdowns);
        Assert.Equal(0, session.BlockedShutdowns);
    }

    // Card v2.4 §2: a detach or an explicit shutdown abandons a pending shutdown-if-unchanged.
    [Fact]
    public async Task ADetachOrAShutdownAbandonsAPendingShutdownIfUnchanged()
    {
        foreach (var useDetach in new[] { true, false })
        {
            var registry = new ShutdownIfUnchangedRegistry();
            var operation = registry.Begin(5)!;
            var session = new ProbeSession();
            var coordinator = Coordinator(session, new ProbeWorker(busy: false), c10: registry);

            if (useDetach)
            {
                _ = coordinator.DetachAsync().Within("the detach");
            }
            else
            {
                _ = coordinator.ShutdownAsync().Within("the shutdown");
            }

            Assert.Equal(ShutdownIfUnchangedState.Abandoned, operation.State);
            Assert.False(operation.TryEnterExiting());
        }
    }

    // Card v2.4 §2: a C10 that already won the exit is untouched by a shutdown, and the explicit
    // shutdown then follows the ordinary exit rules (the blocked/kill sequence on a busy STA).
    [Fact]
    public async Task AnExplicitShutdownDuringAnExitingC10FollowsTheC7aExitRules()
    {
        var registry = new ShutdownIfUnchangedRegistry();
        var operation = registry.Begin(5)!;
        Assert.True(operation.TryEnterExiting());
        var session = new ProbeSession();

        var result = Coordinator(session, new ProbeWorker(busy: true), c10: registry)
            .ShutdownAsync()
            .Within("the shutdown");

        Assert.True(result.Success, result.Error);
        Assert.Equal(ShutdownIfUnchangedState.Exiting, operation.State);
        Assert.Equal(1, session.BlockedShutdowns);
    }
}
