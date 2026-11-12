using System.Diagnostics;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

// Card I5, V5, A7, A8 tests 19/20: the blocked-shutdown RESPONSE is bounded end to end, and
// the bound sits under the Rust response budget by a margin. The Rust side pins its 30 s as a
// literal and parses the same constants line from docs/contracts/sidecar-v1/shutdown-timing.txt.
internal static class TimingContract
{
    /// <summary>The Rust SHUTDOWN_RESPONSE_BUDGET, pinned as a literal on this side too.</summary>
    public static readonly TimeSpan RustResponseBudget = TimeSpan.FromSeconds(30);

    public const string ConstantsLine =
        "etab-cli-blocked-shutdown gate-wait-ms=2000 graceful-ms=5000 forced-ms=10000 " +
        "guard-teardown-ms=2000 record-ops-ms=500 worker-join-ms=2000 worst-case-ms=21500 " +
        "ceiling-ms=22000";
}

public sealed class BlockedShutdownContractTests
{
    [Fact]
    public void BlockedShutdownWorstCaseStaysUnderTheRustBudgetMinusMargin()
    {
        var worst = ServeShutdownCoordinator.BlockedShutdownWorstCase;
        var ceiling = ServeShutdownCoordinator.BlockedShutdownCeiling;

        Assert.Equal(TimeSpan.FromSeconds(22), ceiling);
        Assert.True(worst <= ceiling, $"configured worst case {worst} exceeds the {ceiling} ceiling");
        Assert.True(
            TimingContract.RustResponseBudget - ceiling >= TimeSpan.FromSeconds(5),
            "the Rust response budget must exceed the C# ceiling by at least 5 s");

        // The phases the worst case is made of, pinned individually.
        Assert.Equal(TimeSpan.FromSeconds(2), EtabsSession.BlockedShutdownGateWait);
        Assert.Equal(TimeSpan.FromSeconds(5), ManagedEtabsShutdownMachine.BlockedGracefulExitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), ManagedEtabsShutdownMachine.ForcedExitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), StaExecutionWorker.DefaultDisposeJoinTimeout);

        // The NORMAL path stays at 10 s: the Rust 20 s CLI_SHUTDOWN_ENVELOPE assumes it.
        Assert.Equal(TimeSpan.FromSeconds(10), ManagedEtabsShutdownMachine.GracefulExitTimeout);
    }

    // The same line is the literal in the Rust repository contract fixture.
    [Fact]
    public void TheContractConstantsLineMatchesTheSharedFixtureLiteral() =>
        Assert.Equal(TimingContract.ConstantsLine, ServeShutdownCoordinator.ContractConstantsLine);
}

internal sealed class StuckSession(ManualResetEventSlim never) : IEtabsSession
{
    public bool IsStarted => true;
    public int? ProcessId => 4242;
    public ETABSApplication GetOrStart() => throw new NotSupportedException();
    public IManagedEtabsApplication GetOrStartOwned() => throw new NotSupportedException();
    public Result RevealForExplicitUserRequest() => throw new NotSupportedException();
    public Result CertifyNoUnconsentedExposure() => Result.Ok();

    public void MarkVisibilityStage(string stage)
    {
    }

    public ManagedEtabsShutdownResult Shutdown() => throw new NotSupportedException();

    public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked()
    {
        never.Wait();
        throw new InvalidOperationException("unreachable");
    }

    public void Dispose()
    {
    }
}

internal sealed class BlockedWatchdog : IEtabsModalDialogWatchdog
{
    public bool IsBlocked => true;

    public Task<object> GuardAsync(Func<int?> ownedPid, Func<Task<object>> work, CancellationToken ct) => work();

    public Result BlockedFailure() => Result.Fail("blocked");
}

// Each real-time test lives in its own class so xunit runs them in parallel.
public sealed class BlockedShutdownOuterTimeoutTimingTests
{
    // The outer-timeout path with a stuck worker: backstop + worker join, measured in real time.
    [Fact]
    public async Task TheBlockedShutdownResponseIsBoundedEndToEndByTheCeilingOnTheOuterTimeoutPath()
    {
        using var never = new ManualResetEventSlim(false);
        var worker = new StaExecutionWorker();
        _ = worker.ExecuteAsync(() => new TaskCompletionSource<int>().Task);
        var coordinator = new ServeShutdownCoordinator(worker, new StuckSession(never), new BlockedWatchdog());
        var watch = Stopwatch.StartNew();

        var result = await coordinator.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(40));
        watch.Stop();
        never.Set();

        Assert.False(result.Success);
        Assert.False(result.Data!.ProcessExitConfirmed);
        Assert.Equal(4242, result.Data.OwnedPid);
        Assert.True(
            watch.Elapsed <= ServeShutdownCoordinator.BlockedShutdownCeiling,
            $"the blocked shutdown response took {watch.Elapsed}, over the {ServeShutdownCoordinator.BlockedShutdownCeiling} ceiling");
        Assert.True(
            watch.Elapsed >= ServeShutdownCoordinator.BlockedShutdownEnvelope,
            $"the outer timeout path was not exercised ({watch.Elapsed})");
    }
}

public sealed class BlockedShutdownPhaseMaximumTimingTests
{
    // Every phase of the session blocked sequence driven to its maximum: a stuck exit
    // (blocked graceful 5 s) and a kill that never confirms (forced 10 s), gate free.
    [Fact]
    public void ABlockedSequenceWithEveryPhaseAtItsMaximumStaysUnderTheCeiling()
    {
        var process = new FakeSpawnedProcess(Kit.Identity) { RefusesToExit = true, WaitSleeps = true };
        var rig = new SessionRig(
            new LauncherRig(process: process),
            gateWait: EtabsSession.BlockedShutdownGateWait,
            processExitsOnApplicationExit: false,
            machine: records => new ManagedEtabsShutdownMachine(records));
        using var neverAnswers = new ManualResetEventSlim(false);
        rig.Session.GetOrStartOwned();
        rig.L.Api.OnExit = () => neverAnswers.Wait(TimeSpan.FromSeconds(40));
        var watch = Stopwatch.StartNew();

        var result = Task.Run(rig.Session.ShutdownWhileWorkerBlocked)
            .Within("the blocked sequence at its maximum", TimeSpan.FromSeconds(40));
        watch.Stop();
        neverAnswers.Set();

        Assert.False(result.Success);
        Assert.False(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.RecordRetained);
        Assert.True(
            watch.Elapsed >= ManagedEtabsShutdownMachine.BlockedGracefulExitTimeout
                + ManagedEtabsShutdownMachine.ForcedExitTimeout - TimeSpan.FromMilliseconds(500),
            $"the phases were not driven to their maximum ({watch.Elapsed})");
        Assert.True(
            watch.Elapsed <= ServeShutdownCoordinator.BlockedShutdownCeiling,
            $"took {watch.Elapsed}");
    }
}

public sealed class BlockedShutdownGateHeldTimingTests
{
    // The gate is held by a stuck command: TryEnter 2 s, then the handle-only kill (forced 10 s).
    [Fact]
    public void AHandleOnlyShutdownBehindAHeldGateStaysUnderTheCeiling()
    {
        var process = new FakeSpawnedProcess(Kit.Identity) { RefusesToExit = true, WaitSleeps = true };
        var rig = new SessionRig(
            new LauncherRig(process: process),
            gateWait: EtabsSession.BlockedShutdownGateWait,
            processExitsOnApplicationExit: false);
        rig.Session.GetOrStartOwned();
        var holding = new ManualResetEventSlim(false);
        var letGo = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            lock (rig.Gate)
            {
                holding.Set();
                letGo.Wait(TimeSpan.FromSeconds(40));
            }
        });
        holding.Signalled("the stuck command to take the gate");
        var watch = Stopwatch.StartNew();

        var result = Task.Run(rig.Session.ShutdownWhileWorkerBlocked)
            .Within("the handle-only shutdown at its maximum", TimeSpan.FromSeconds(40));
        watch.Stop();
        letGo.Set();
        holder.Within("the gate to be released");

        Assert.False(result.Success);
        Assert.True(
            watch.Elapsed >= EtabsSession.BlockedShutdownGateWait + ManagedEtabsShutdownMachine.ForcedExitTimeout
                - TimeSpan.FromMilliseconds(500),
            $"the phases were not driven to their maximum ({watch.Elapsed})");
        Assert.True(watch.Elapsed <= ServeShutdownCoordinator.BlockedShutdownCeiling, $"took {watch.Elapsed}");
    }
}
