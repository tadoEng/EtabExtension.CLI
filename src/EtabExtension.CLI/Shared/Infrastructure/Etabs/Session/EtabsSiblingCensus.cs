namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// The other daemon's managed-session record (<c>serve --sibling-record</c>). An editing and
/// an analysis daemon each own one ETABS; each must see the other's process as a known
/// SIBLING rather than as an external ETABS it has to refuse.
/// </summary>
public interface ISiblingRecordSource
{
    /// <summary>Read fresh on every census: the sibling may start or stop at any time.</summary>
    ManagedEtabsSessionRecord? Read();
}

public sealed class NoSiblingRecordSource : ISiblingRecordSource
{
    public ManagedEtabsSessionRecord? Read() => null;
}

/// <summary>Read-only view of the sibling daemon's record file. Never writes or clears it.</summary>
public sealed class JsonSiblingRecordSource(string path) : ISiblingRecordSource
{
    private readonly JsonSessionRecordStore _store = new(path);

    public string FilePath => _store.FilePath;

    public ManagedEtabsSessionRecord? Read() => _store.Read();
}

public static class EtabsSiblingCensus
{
    /// <summary>
    /// Marks as siblings the identified processes that match the sibling record EXACTLY -
    /// pid, start time and executable path. A pid alone proves nothing: pids are reused, so a
    /// different start time or path is a foreign ETABS and stays in the census as one.
    /// </summary>
    public static EtabsProcessObservation Classify(
        EtabsProcessObservation observation,
        ManagedEtabsSessionRecord? sibling)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (sibling is null)
        {
            return observation with { Siblings = [] };
        }

        return observation with
        {
            Siblings = observation.Identified
                .Where(identity => OrphanSessionCleaner.IdentityMatches(sibling, identity))
                .ToList()
        };
    }
}

/// <summary>
/// Every census this daemon takes goes through here, so the launcher's preflight, its
/// post-start ownership census and its failed-start survivor resolution all exclude the
/// sibling's ETABS - and none of them can ever terminate it.
/// </summary>
public sealed class SiblingAwareProcessInspector(
    IProcessInspector inner,
    ISiblingRecordSource siblings) : IProcessInspector
{
    public EtabsProcessObservation ObserveEtabs() =>
        EtabsSiblingCensus.Classify(inner.ObserveEtabs(), siblings.Read());

    public ManagedProcessIdentity? Find(int pid) => inner.Find(pid);

    public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected) => inner.OpenExact(expected);

    public ExactProcessTerminationResult TerminateExact(
        ManagedProcessIdentity expected,
        TimeSpan timeout) => inner.TerminateExact(expected, timeout);
}
