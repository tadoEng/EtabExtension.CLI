using System.Text.Json;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

// WP-C0 (core-loop §2.2-2.4): per-daemon record and sibling census. ETABS-free.
public sealed class SiblingCensusTests
{
    private const string EtabsExe = @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe";
    private static readonly DateTimeOffset Started = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static ManagedProcessIdentity Identity(int pid, DateTimeOffset? started = null) =>
        new(pid, started ?? Started, EtabsExe);

    private static ManagedEtabsSessionRecord RecordOf(ManagedProcessIdentity identity) => new(
        1,
        identity.Pid,
        identity.ProcessStartTimeUtc,
        identity.ExecutablePath,
        Guid.NewGuid(),
        Started);


    // ── Sibling census (§2.3) ──────────────────────────────────────────────────

    [Fact]
    public void TwoIdentifiedEtabsWithAMatchingSiblingRecordAreManaged()
    {
        var observation = EtabsSiblingCensus.Classify(
            new EtabsProcessObservation([Identity(42), Identity(77)], 0),
            RecordOf(Identity(77)));

        Assert.Equal(EtabsInstanceOwnership.Managed, EtabsOwnershipResolver.Resolve(observation, 42));
        Assert.Equal([77], observation.Siblings.Select(identity => identity.Pid));
    }

    [Fact]
    public void ASiblingRecordWithAnotherStartTimeLeavesThatEtabsExternal()
    {
        var observation = EtabsSiblingCensus.Classify(
            new EtabsProcessObservation([Identity(42), Identity(77)], 0),
            RecordOf(Identity(77, Started.AddSeconds(3))));

        Assert.Empty(observation.Siblings);
        Assert.Equal(EtabsInstanceOwnership.External, EtabsOwnershipResolver.Resolve(observation, 42));
    }

    /// <summary>Mutation guard: a pid alone never makes a sibling (pids are reused).</summary>
    [Fact]
    public void ASiblingIsNeverMatchedByPidAlone()
    {
        var reused = new ManagedProcessIdentity(77, Started, @"D:\Other\ETABS.exe");
        var observation = EtabsSiblingCensus.Classify(
            new EtabsProcessObservation([Identity(42), reused], 0),
            RecordOf(Identity(77)));

        Assert.Empty(observation.Siblings);
        Assert.Equal(EtabsInstanceOwnership.External, EtabsOwnershipResolver.Resolve(observation, 42));
    }

    [Fact]
    public void AnUnidentifiedEtabsIsAmbiguousEvenBesideAMatchedSibling()
    {
        var observation = EtabsSiblingCensus.Classify(
            new EtabsProcessObservation([Identity(42), Identity(77)], 1),
            RecordOf(Identity(77)));

        Assert.Equal(EtabsInstanceOwnership.Ambiguous, EtabsOwnershipResolver.Resolve(observation, 42));
    }

    [Fact]
    public void OnlyTheSiblingRunningIsNoneAndNotRunning()
    {
        var observation = EtabsSiblingCensus.Classify(
            new EtabsProcessObservation([Identity(77)], 0),
            RecordOf(Identity(77)));

        Assert.Equal(EtabsInstanceOwnership.None, EtabsOwnershipResolver.Resolve(observation, null));
        var status = EtabsStatusOwnership.Decorate(Result.Ok(new GetStatusData()), observation, null);
        Assert.False(status.Data!.IsRunning);
        Assert.Equal([77], status.Data.SiblingPids);
        Assert.Equal([77], status.Data.ObservedPids);
    }

    [Fact]
    public void StatusReportsSiblingPidsAndKeepsTheWholeCensusInObservedPids()
    {
        var observation = EtabsSiblingCensus.Classify(
            new EtabsProcessObservation([Identity(77), Identity(42)], 0),
            RecordOf(Identity(77)));

        var status = EtabsStatusOwnership.Decorate(Result.Ok(new GetStatusData()), observation, 42);

        Assert.Equal(EtabsInstanceOwnership.Managed, status.Data!.Ownership);
        Assert.Equal(42, status.Data.Pid);
        Assert.Equal([42, 77], status.Data.ObservedPids);
        Assert.Equal([77], status.Data.SiblingPids);
        var json = JsonSerializer.Serialize(status.Data, ServeJson.Options);
        Assert.Contains("\"siblingPids\":[77]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoUnknownEtabsAreExternalWithoutNamingEitherPid()
    {
        var status = EtabsStatusOwnership.Decorate(
            Result.Ok(new GetStatusData()),
            new EtabsProcessObservation([Identity(42), Identity(99), Identity(98)], 0),
            42);

        Assert.Equal(EtabsInstanceOwnership.External, status.Data!.Ownership);
        Assert.Null(status.Data.Pid);
    }

    [Fact]
    public void TheSiblingAwareInspectorClassifiesEveryCensusFromAFreshRecordRead()
    {
        var reads = 0;
        var inspector = new SiblingAwareProcessInspector(
            new StaticCensus(new EtabsProcessObservation([Identity(42), Identity(77)], 0)),
            new CountingSibling(() =>
            {
                reads++;
                return RecordOf(Identity(77));
            }));

        Assert.Equal([77], inspector.ObserveEtabs().Siblings.Select(identity => identity.Pid));
        Assert.Equal([42], inspector.ObserveEtabs().NonSibling.Select(identity => identity.Pid));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void TheReadyHandshakeAnnouncesTheRecordPath()
    {
        var handshake = ServeHandshake.FromAssembly(
            ServeHandshake.MetadataAssembly, 7, EtabsExe, ["get-status"]) with
        {
            RecordPath = @"C:\records\analysis.json"
        };

        var json = JsonSerializer.Serialize(handshake, ServeJson.Options);

        Assert.Contains("\"recordPath\":\"C:\\\\records\\\\analysis.json\"", json, StringComparison.Ordinal);
        Assert.Contains("\"startup\":\"ready\"", json, StringComparison.Ordinal);
    }


    private sealed class StaticCensus(EtabsProcessObservation observation) : IProcessInspector
    {
        public EtabsProcessObservation ObserveEtabs() => observation;
        public ManagedProcessIdentity? Find(int pid) => null;
        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected) => null;

        public ExactProcessTerminationResult TerminateExact(
            ManagedProcessIdentity expected,
            TimeSpan timeout) => throw new NotSupportedException();
    }

    private sealed class CountingSibling(Func<ManagedEtabsSessionRecord?> read) : ISiblingRecordSource
    {
        public ManagedEtabsSessionRecord? Read() => read();
    }
}

