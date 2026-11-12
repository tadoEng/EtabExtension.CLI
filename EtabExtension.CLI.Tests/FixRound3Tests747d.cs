using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>#747-D fix round 3 (Codex recheck r3): real sessions, full scope teardown.</summary>
public sealed class FixRound3Tests747d
{
    // P1: suppression is a lifecycle state on the SAME CAS word as termination, so a cleanup that
    // observed Ready before the EOF decision cannot win Ready->Terminating afterwards.
    [Fact]
    public void ACleanupPausedAfterObservingReadyLosesToTheEofSuppressionDecision()
    {
        var rig = new SessionRig();
        rig.Session.DetachSettleWait = TimeSpan.FromSeconds(5);
        _ = rig.Session.GetOrStartOwned();
        Assert.True(rig.Session.RevealForExplicitUserRequest().Success);
        using var observed = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var first = 0;
        rig.Session.BetweenFenceObservationAndCas = () =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                observed.Set();
                resume.Signalled("the test to resume the late cleanup");
            }
        };
        var cleanup = Kit.OnDedicatedThread(rig.Session.Shutdown);
        observed.Signalled("the cleanup to observe Ready");

        // EOF: the detach fails (rolled back to Ready) and the decision to leave ETABS running is taken.
        rig.Session.BeforeDetachWrite = () => throw new IOException("pre-write failure");
        var coordinator = new ServeShutdownCoordinator(
            new ProbeWorker(busy: false), rig.Session, new ProbeWatchdog(), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200));
        Assert.False(coordinator.EndOfInputAsync(ServeEofPolicy.DetachVisible).Within("EOF").Success);
        Assert.Equal(LaunchLifecycle.Suppressed, ((EtabsLaunchState)rig.Field("_launch")!).Lifecycle);

        resume.Set();
        var answer = cleanup.Within("the resumed cleanup");

        Assert.False(answer.Success);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.False(rig.L.Process.HasExited);
    }

    private static SessionRig Unkillable() => new(
        new LauncherRig(process: new FakeSpawnedProcess(Kit.Identity) { RefusesToExit = true }),
        processExitsOnApplicationExit: false);

    private static ServiceProvider ScopeOver(IEtabsSession session)
    {
        var services = new ServiceCollection();
        services.AddScoped<IEtabsSession>(_ => session);
        return services.BuildServiceProvider();
    }

    // P2 (a): gate contention. No completed-attempt flag exists; disposal is bounded anyway.
    [Fact]
    public void DisposalWithoutAnyCompletedAttemptIsBoundedWhileAnotherWorkerHoldsTheGate()
    {
        var rig = Unkillable();
        rig.Session.DisposeBudget = TimeSpan.FromSeconds(4);
        _ = rig.Session.GetOrStartOwned();
        using var release = new ManualResetEventSlim(false);
        using var held = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            lock (rig.Gate)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        })
        { IsBackground = true };
        holder.Start();
        held.Signalled("the gate to be held");
        using var provider = ScopeOver(rig.Session);
        var scope = provider.CreateScope();

        Kit.OnDedicatedThread(() =>
        {
            scope.Dispose();
            return 0;
        }).Within("the scope teardown", TimeSpan.FromSeconds(9));

        Assert.NotNull(rig.L.Records.Record); // unresolved: the evidence and the recovery record stay
        Assert.False(rig.L.Process.HasExited);
        release.Set();
    }

    // P2 (b): an exit that never returns, again with no completed-attempt flag.
    [Fact]
    public void DisposalWithoutAnyCompletedAttemptIsBoundedWhenTheExitNeverReturns()
    {
        using var never = new ManualResetEventSlim(false);
        var rig = Unkillable();
        rig.Session.DisposeBudget = TimeSpan.FromSeconds(4);
        _ = rig.Session.GetOrStartOwned();
        rig.L.Api.OnExit = () => never.Wait(TimeSpan.FromSeconds(30));
        using var provider = ScopeOver(rig.Session);
        var scope = provider.CreateScope();

        Kit.OnDedicatedThread(() =>
        {
            scope.Dispose();
            return 0;
        }).Within("the scope teardown", TimeSpan.FromSeconds(9));

        Assert.NotNull(rig.L.Records.Record);
        Assert.False(rig.L.Process.HasExited);
        never.Set();
    }
}
