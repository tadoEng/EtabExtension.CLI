using System.Text.Json.Nodes;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>#747-D fix round 2 (Codex recheck): real sessions and full scope teardown, never ProbeSession.</summary>
public sealed class FixRound2Tests747d
{
    private const string Working = @"D:\Project\.etabs-ext\main\working\model.edb";

    private static ServiceProvider ScopeOver(IEtabsSession session)
    {
        var services = new ServiceCollection();
        services.AddScoped<IEtabsSession>(_ => session);
        return services.BuildServiceProvider();
    }

    // P1-1: after the EOF decision, a late in-flight failure cannot end the instance by ANY path.
    [Fact]
    public void ALateRequestFailureAfterTheEofDecisionExitsAndKillsNothingEvenThroughScopeDisposal()
    {
        var rig = new SessionRig();
        _ = rig.Session.GetOrStartOwned();
        Assert.True(rig.Session.RevealForExplicitUserRequest().Success);
        rig.Session.BeforeDetachWrite = () => throw new IOException("pre-write failure"); // the EOF detach rolls back
        using var provider = ScopeOver(rig.Session);
        var scope = provider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IEtabsSession>();
        var coordinator = new ServeShutdownCoordinator(
            new ProbeWorker(busy: false), session, new ProbeWatchdog(), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200));
        Assert.False(coordinator.EndOfInputAsync(ServeEofPolicy.DetachVisible).Within("EOF").Success);

        // (a) a reveal already in flight fails after the decision: EndUncertainSession path.
        rig.L.Api.UnhideException = new InvalidOperationException("COM");
        var late = rig.Session.RevealForExplicitUserRequest();
        // (b) a request whose identity verification fails: the GetOrStartOwned cleanup path.
        rig.L.Census.Live = null;
        Assert.ThrowsAny<Exception>(() => rig.Session.GetOrStartOwned());
        scope.Dispose();

        Assert.False(late.Success);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.False(rig.L.Process.HasExited);
    }

    // P1-2: the PRODUCTION factory with an unreadable identity refuses; nothing is exited or killed.
    [Fact]
    public void TheProductionC10FactoryWithAnUnreadableIdentityRefusesAndEndsNothing()
    {
        var rig = new SessionRig();
        _ = rig.Session.GetOrStartOwned();
        rig.L.Census.Live = null; // the identity can no longer be read
        var factory = new SessionShutdownIfUnchangedApiFactory(new UnsupportedChangeSignalSource());
        var service = new ShutdownIfUnchangedService(
            rig.Session,
            new ProbeWatchdog(),
            factory,
            new ShutdownIfUnchangedRegistry(),
            new FixedStamps(null));

        var direct = Assert.Throws<InvalidOperationException>(() => factory.Create(rig.Session));
        var answer = service.RunAsync(
            new ShutdownIfUnchangedRequest { FilePath = Working, DiscardConfirmed = true },
            1,
            check => Task.Run(check)).Within("the command");

        Assert.Contains("identity", direct.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            ShutdownIfUnchangedCodes.CheckInconclusive,
            Assert.IsType<Result>(answer, exactMatch: false).Error,
            StringComparison.Ordinal);
        Assert.Equal(0, rig.L.Process.KillCount);
        Assert.Equal(0, rig.L.Api.ExitCount);
        Assert.True(rig.Session.IsStarted);
        Assert.False(rig.L.Process.HasExited);
    }

    // P2-3: a stuck exit, failed forced cleanup, then the full scope teardown returns within its bound.
    [Fact]
    public void DisposalAfterABoundedUnconfirmedShutdownDoesNotWaitOnTheStuckGateAndKeepsTheEvidence()
    {
        using var never = new ManualResetEventSlim(false);
        var rig = new SessionRig(
            new LauncherRig(process: new FakeSpawnedProcess(Kit.Identity) { RefusesToExit = true }),
            processExitsOnApplicationExit: false);
        _ = rig.Session.GetOrStartOwned();
        rig.L.Api.OnExit = () => never.Wait(TimeSpan.FromSeconds(30)); // the exit never returns, holding the gate
        using var provider = ScopeOver(rig.Session);
        var scope = provider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IEtabsSession>();
        var coordinator = new ServeShutdownCoordinator(
            new ProbeWorker(busy: false),
            session,
            new ProbeWatchdog(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(200),
            null,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(1500));

        var answer = coordinator.ShutdownAsync().Within("the bounded shutdown");
        Kit.OnDedicatedThread(() =>
        {
            scope.Dispose();
            return 0;
        }).Within("the scope teardown");

        Assert.False(answer.Success);
        Assert.False(answer.Data!.ProcessExitConfirmed);
        Assert.True(answer.Data.RecordRetained);
        Assert.NotNull(rig.L.Records.Record); // the recovery record is untouched
        never.Set();
    }

    // P2-4: the unconfirmed-exit payload carries UNKNOWN evidence, never "exited: false".
    [Fact]
    public void TheUnconfirmedExitPayloadSaysUnknownAndNeverAssertsThatNothingWasExited()
    {
        using var never = new ManualResetEventSlim(false);
        var session = new ProbeSession { NormalShutdown = () => { never.Wait(TimeSpan.FromSeconds(30)); return Shutdowns.Ok(); } };
        var service = new ShutdownIfUnchangedService(
            session,
            new ProbeWatchdog(),
            new FixedApiFactory(new EventedFileApi([]) { CurrentPath = Working }),
            new ShutdownIfUnchangedRegistry(),
            null,
            TimeSpan.FromMilliseconds(300),
            null,
            TimeSpan.FromMilliseconds(600));

        var answer = service.RunAsync(
            new ShutdownIfUnchangedRequest { FilePath = Working, DiscardConfirmed = true },
            1,
            check => Task.Run(check)).Within("the exit answer");

        var error = Assert.IsType<Result>(answer, exactMatch: false).Error!;
        Assert.Contains(ShutdownIfUnchangedService.ExitUnconfirmedCode, error, StringComparison.Ordinal);
        var detail = JsonNode.Parse(error[(error.IndexOf(" detail=", StringComparison.Ordinal) + " detail=".Length)..])!.AsObject();
        Assert.Equal("unknown", detail["exited"]!.GetValue<string>());
        Assert.False(detail["exitConfirmed"]!.GetValue<bool>());
        Assert.DoesNotContain("\"exited\":false", error, StringComparison.Ordinal);
        never.Set();
    }
}
