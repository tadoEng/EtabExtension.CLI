using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ServeShutdownCoordinatorTests
{
    [Fact]
    public async Task ConcurrentAndRepeatedShutdownIsOneStaCleanupThenOneWorkerDisposal()
    {
        var events = new List<string>();
        var terminal = Success(forced: true);
        var session = new FakeSession(terminal, events);
        var worker = new ControlledWorker(events);
        var coordinator = new ServeShutdownCoordinator(worker, session, NotBlocked);

        var first = coordinator.ShutdownAsync();
        var concurrent = coordinator.ShutdownAsync();

        Assert.Same(first, concurrent);
        Assert.Equal(1, worker.ExecuteCount);
        Assert.Equal(0, session.ShutdownCount);
        Assert.Equal(0, worker.DisposeCount);
        Assert.Equal(["worker-queued"], events);

        await worker.RunQueuedAsync();
        var firstResult = await first;
        var concurrentResult = await concurrent;

        Assert.True(firstResult.Success, firstResult.Error);
        Assert.Same(firstResult, concurrentResult);
        Assert.Same(terminal.Data, firstResult.Data);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
        Assert.Equal(
            [
                "worker-queued",
                "worker-start",
                "session-cleanup",
                "worker-complete",
                "worker-dispose"
            ],
            events);

        var repeated = await coordinator.ShutdownAsync();
        await coordinator.DisposeAsync();

        Assert.Same(firstResult, repeated);
        Assert.Equal(1, worker.ExecuteCount);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task TypedShutdownFailurePreservesTerminalDataAndBoundedStableError()
    {
        var data = new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            ProcessExitConfirmed: false,
            Forced: true,
            RecordRetained: true,
            ApplicationExitReturnCode: 7,
            OwnedPid: 42);
        var terminal = new ManagedEtabsShutdownResult(
            false,
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            new string('e', 2300) + "\r\n",
            data);
        var worker = new ControlledWorker([]);
        var coordinator = new ServeShutdownCoordinator(
            worker,
            new FakeSession(terminal, []),
            NotBlocked);

        var shutdown = coordinator.ShutdownAsync();
        await worker.RunQueuedAsync();
        var result = await shutdown;

        Assert.False(result.Success);
        Assert.Same(data, result.Data);
        Assert.Contains(
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(result.Error!, char.IsControl);
        Assert.True(result.Error!.Length <= 2048);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task CallerCancellationOnlyAbandonsThatWaitAndDoesNotCancelSafetyCleanup()
    {
        var terminal = Success(forced: false);
        var session = new FakeSession(terminal, []);
        var worker = new ControlledWorker([]);
        var coordinator = new ServeShutdownCoordinator(worker, session, NotBlocked);
        var shutdown = coordinator.ShutdownAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => shutdown.WaitAsync(cancellation.Token));
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, session.ShutdownCount);
        Assert.Equal(0, worker.DisposeCount);

        await worker.RunQueuedAsync();
        var result = await coordinator.ShutdownAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsyncStartsAndAwaitsTheSameShutdownPath()
    {
        var session = new FakeSession(Success(forced: false), []);
        var worker = new ControlledWorker([]);
        var coordinator = new ServeShutdownCoordinator(worker, session, NotBlocked);

        var disposal = coordinator.DisposeAsync().AsTask();

        Assert.Equal(1, worker.ExecuteCount);
        Assert.False(disposal.IsCompleted);
        Assert.Equal(0, session.ShutdownCount);
        Assert.Equal(0, worker.DisposeCount);

        await worker.RunQueuedAsync();
        await disposal;
        await coordinator.DisposeAsync();

        Assert.Equal(1, worker.ExecuteCount);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
    }

    // ── P2-1: shutdown while a command is blocked on an ETABS dialog ────────────

    /// <summary>
    /// The DD-5 hang at shutdown, reproduced without ETABS: a REAL STA worker is stuck
    /// inside a call (as it is inside a cFile.OpenFile a modal box holds), and the REAL
    /// watchdog has abandoned that command. Shutdown must not queue behind the stuck call: it
    /// runs the bounded session shutdown on a fresh STA thread and answers within the
    /// envelope with the exit confirmed, while the worker is still stuck.
    /// </summary>
    [Fact]
    public async Task ShutdownWhileACommandIsBlockedOnADialogDoesNotQueueBehindTheStuckWorker()
    {
        using var release = new ManualResetEventSlim();
        var worker = new StaExecutionWorker(TimeSpan.FromMilliseconds(200));
        try
        {
            var stuck = worker.ExecuteAsync<object>(() =>
            {
                release.Wait();
                return Task.FromResult<object>(Result.Ok());
            });
            var watchdog = new EtabsModalDialogWatchdog(
                new AlwaysOneDialog(),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromSeconds(1));
            var opened = Assert.IsType<Result>(await watchdog.GuardAsync(
                () => 42, () => stuck, TestContext.Current.CancellationToken));
            Assert.StartsWith("[ETABS_MODAL_DIALOG] ", opened.Error, StringComparison.Ordinal);
            Assert.True(watchdog.IsBlocked);

            var session = new FakeSession(Success(forced: true), []);
            var coordinator = new ServeShutdownCoordinator(worker, session, watchdog);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var result = await coordinator.ShutdownAsync()
                .WaitAsync(ServeShutdownCoordinator.BlockedShutdownEnvelope, TestContext.Current.CancellationToken);

            Assert.True(clock.Elapsed < ServeShutdownCoordinator.BlockedShutdownEnvelope);
            Assert.True(result.Success, result.Error);
            Assert.True(result.Data!.ProcessExitConfirmed);
            Assert.Equal(0, session.ShutdownCount);
            Assert.Equal(1, session.BlockedShutdownCount);
            Assert.Equal(ApartmentState.STA, session.BlockedShutdownApartment);
            Assert.Equal("etab-cli-serve-blocked-shutdown", session.BlockedShutdownThreadName);

            // The answer did not wait for the stuck call, and disposal did not either.
            Assert.False(stuck.IsCompleted);
            Assert.False(worker.HasStopped);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// The envelope is the backstop: a blocked-session shutdown that itself hangs still
    /// answers, as an unconfirmed exit, instead of hanging the daemon.
    /// </summary>
    [Fact]
    public async Task ABlockedSessionShutdownThatHangsAnswersUnconfirmedWithinTheEnvelope()
    {
        using var hang = new ManualResetEventSlim();
        var worker = new ControlledWorker([]);
        var session = new FakeSession(Success(forced: false), []) { BlockedShutdownGate = hang };
        var coordinator = new ServeShutdownCoordinator(
            worker,
            session,
            new FixedWatchdog(blocked: true),
            TimeSpan.FromMilliseconds(200));

        try
        {
            var result = await coordinator.ShutdownAsync()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.False(result.Data!.ProcessExitConfirmed);
            Assert.True(result.Data.RecordRetained);
            Assert.Equal(ManagedEtabsShutdownState.ProcessExitUnconfirmed, result.Data.State);
            Assert.Contains(
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                result.Error,
                StringComparison.Ordinal);
            Assert.Equal(0, worker.ExecuteCount);
            Assert.Equal(1, worker.DisposeCount);
        }
        finally
        {
            hang.Set();
        }
    }

    /// <summary>Mutation guard: an unblocked session keeps the normal STA-worker shutdown.</summary>
    [Fact]
    public async Task AnUnblockedSessionStillShutsDownOnTheStaWorker()
    {
        var session = new FakeSession(Success(forced: false), []);
        var worker = new ControlledWorker([]);
        var coordinator = new ServeShutdownCoordinator(worker, session, NotBlocked);

        var shutdown = coordinator.ShutdownAsync();
        await worker.RunQueuedAsync();
        var result = await shutdown;

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, worker.ExecuteCount);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(0, session.BlockedShutdownCount);
    }

    /// <summary>Disposal of a worker stuck inside a call returns after its join timeout.</summary>
    [Fact]
    public async Task DisposingAWorkerStuckInACallReturnsAfterItsJoinTimeout()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var worker = new StaExecutionWorker(TimeSpan.FromMilliseconds(100));
        var stuck = worker.ExecuteAsync<object>(() =>
        {
            entered.Set();
            release.Wait();
            return Task.FromResult<object>(Result.Ok());
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        try
        {
            var dispose = Task.Run(worker.Dispose, TestContext.Current.CancellationToken);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(worker.HasStopped);
        }
        finally
        {
            release.Set();
        }

        // Once the stuck call returns, the abandoned thread ends on its own.
        await stuck.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!worker.HasStopped && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(worker.HasStopped);
    }

    [Fact]
    public void CoordinatorRegistrationIsScoped()
    {
        var services = new ServiceCollection();

        services.AddServeFeature();

        var registration = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IServeShutdownCoordinator));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(ServeShutdownCoordinator), registration.ImplementationType);
    }

    private static ManagedEtabsShutdownResult Success(bool forced)
    {
        var data = new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: false,
            ApplicationExitReturnCode: 0,
            OwnedPid: 42);
        return new(true, null, null, data);
    }

    private static readonly IEtabsModalDialogWatchdog NotBlocked = new FixedWatchdog(blocked: false);

    private sealed class FixedWatchdog(bool blocked) : IEtabsModalDialogWatchdog
    {
        public bool IsBlocked => blocked;

        public Task<object> GuardAsync(Func<int?> ownedPid, Func<Task<object>> work, CancellationToken ct) =>
            work();

        public Result BlockedFailure() => Result.Fail("[ETABS_MODAL_DIALOG] blocked");
    }

    private sealed class AlwaysOneDialog : IEtabsDialogWindowSource
    {
        public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid) =>
            [new(7, "ETABS", ["Unexpected error in the model file."])];

        public bool TryDismiss(EtabsDialogWindow dialog) => false;
    }

    private sealed class ControlledWorker(List<string> events) : IStaExecutionWorker
    {
        private IQueuedWork? _queued;

        public int ExecuteCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            ExecuteCount++;
            events.Add("worker-queued");
            var queued = new QueuedWork<T>(action, events);
            _queued = queued;
            return queued.Task;
        }

        public Task RunQueuedAsync() =>
            (_queued ?? throw new InvalidOperationException("No work was queued")).RunAsync();

        public void Dispose()
        {
            DisposeCount++;
            events.Add("worker-dispose");
        }

        private interface IQueuedWork
        {
            Task RunAsync();
        }

        private sealed class QueuedWork<T>(
            Func<Task<T>> action,
            List<string> events) : IQueuedWork
        {
            private readonly TaskCompletionSource<T> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<T> Task => _completion.Task;

            public async Task RunAsync()
            {
                events.Add("worker-start");
                try
                {
                    var result = await action();
                    events.Add("worker-complete");
                    _completion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(exception);
                }
            }
        }
    }

    private sealed class FakeSession(
        ManagedEtabsShutdownResult terminal,
        List<string> events) : IEtabsSession
    {
        /// <summary>CLI #24 certification the test can force to fail.</summary>
        public Result ExposureCertification { get; set; } = Result.Ok();

        public List<string> Stages { get; } = [];

        public Result CertifyNoUnconsentedExposure() => ExposureCertification;

        public void MarkVisibilityStage(string stage) => Stages.Add(stage);

        public int ShutdownCount { get; private set; }
        public bool IsStarted => true;
        public int? ProcessId => terminal.Data.OwnedPid;
        public ETABSApplication GetOrStart() => throw new NotSupportedException();
        public IManagedEtabsApplication GetOrStartOwned() => throw new NotSupportedException();
        public Result RevealForExplicitUserRequest() => throw new NotSupportedException();

        public ManagedEtabsShutdownResult Shutdown()
        {
            ShutdownCount++;
            events.Add("session-cleanup");
            return terminal;
        }

        public int BlockedShutdownCount { get; private set; }
        public ApartmentState? BlockedShutdownApartment { get; private set; }
        public string? BlockedShutdownThreadName { get; private set; }

        /// <summary>Holds the blocked-session shutdown, to exercise the envelope.</summary>
        public ManualResetEventSlim? BlockedShutdownGate { get; init; }

        public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked()
        {
            BlockedShutdownCount++;
            BlockedShutdownApartment = Thread.CurrentThread.GetApartmentState();
            BlockedShutdownThreadName = Thread.CurrentThread.Name;
            BlockedShutdownGate?.Wait();
            return terminal;
        }

        public void Dispose() { }
    }
}
