namespace EtabExtension.CLI.Features.ExtractResults.Models;

internal static class ExtractionProfiles
{
    public const string Full = "full";
    public const string Results = "results";
    public const string Geometry = "geometry";
    public const string Snapshot = "snapshot";

    public static TableSelections Build(string? profile)
    {
        return Normalise(profile) switch
        {
            Results => ResultsTables(),
            Geometry => GeometryTables(),
            Snapshot => SnapshotTables(),
            _ => FullTables()
        };
    }

    public static TableSelections Resolve(TableSelections requested, string? profile, string defaultProfile)
    {
        if (!IsEmpty(requested))
        {
            return requested;
        }

        return Build(string.IsNullOrWhiteSpace(profile) ? defaultProfile : profile);
    }

    public static string Normalise(string? profile)
    {
        var value = string.IsNullOrWhiteSpace(profile)
            ? Full
            : profile.Trim().ToLowerInvariant();

        return value switch
        {
            "all" => Full,
            Full => Full,
            Results => Results,
            Geometry => Geometry,
            Snapshot => Snapshot,
            _ => Full
        };
    }

    private static TableSelections FullTables() => new()
    {
        MaterialListByStory = new TableFilter(),
        MaterialPropertiesConcreteData = new TableFilter(),
        GroupAssignments = new TableFilter(),
        StoryDefinitions = new TableFilter(),
        PierSectionProperties = new TableFilter(),
        BaseReactions = TableFilter.All,
        StoryForces = TableFilter.All,
        JointDrifts = TableFilter.All,
        PierForces = TableFilter.All,
        ModalParticipatingMassRatios = new TableFilter()
    };

    private static TableSelections ResultsTables() => new()
    {
        BaseReactions = TableFilter.All,
        StoryForces = TableFilter.All,
        JointDrifts = TableFilter.All,
        PierForces = TableFilter.All,
        ModalParticipatingMassRatios = new TableFilter()
    };

    private static TableSelections GeometryTables() => new()
    {
        StoryDefinitions = new TableFilter(),
        PierSectionProperties = new TableFilter(),
        GroupAssignments = new TableFilter(),
        MaterialPropertiesConcreteData = new TableFilter(),
        MaterialListByStory = new TableFilter()
    };

    private static TableSelections SnapshotTables() => GeometryTables();

    private static bool IsEmpty(TableSelections tables) =>
        tables.MaterialListByStory is null
        && tables.MaterialPropertiesConcreteData is null
        && tables.GroupAssignments is null
        && tables.StoryDefinitions is null
        && tables.BaseReactions is null
        && tables.StoryForces is null
        && tables.JointDrifts is null
        && tables.PierForces is null
        && tables.PierSectionProperties is null
        && tables.ModalParticipatingMassRatios is null;
}
