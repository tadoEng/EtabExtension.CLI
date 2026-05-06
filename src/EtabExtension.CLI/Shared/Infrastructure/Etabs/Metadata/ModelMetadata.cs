using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;

public record ModelMetadata
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 2;

    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("etabsVersion")]
    public string EtabsVersion { get; init; } = string.Empty;

    [JsonPropertyName("isAnalyzed")]
    public bool IsAnalyzed { get; init; }

    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; init; }

    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("loadPatterns")]
    public List<LoadPatternInfo> LoadPatterns { get; init; } = [];

    [JsonPropertyName("loadCases")]
    public List<LoadCaseInfo> LoadCases { get; init; } = [];

    [JsonPropertyName("loadCombinations")]
    public List<LoadComboInfo> LoadCombinations { get; init; } = [];

    [JsonPropertyName("stories")]
    public List<StoryInfo> Stories { get; init; } = [];

    [JsonPropertyName("groups")]
    public List<string> Groups { get; init; } = [];

    [JsonPropertyName("groupDetails")]
    public List<GroupInfo> GroupDetails { get; init; } = [];

    [JsonPropertyName("materials")]
    public List<MaterialInfo> Materials { get; init; } = [];

    [JsonPropertyName("frameSections")]
    public List<FrameSectionInfo> FrameSections { get; init; } = [];

    [JsonPropertyName("areaSections")]
    public List<AreaSectionInfo> AreaSections { get; init; } = [];

    [JsonPropertyName("warnings")]
    public List<MetadataWarning> Warnings { get; init; } = [];

    [JsonPropertyName("collectedAt")]
    public DateTimeOffset CollectedAt { get; init; }
}

public record LoadPatternInfo(string Name, string LoadType, double SelfWeightMultiplier);

public record LoadCaseInfo(string Name, string CaseType);

public record LoadComboInfo(
    string Name,
    string ComboType,
    List<string> ConstituentCases,
    List<LoadComboItemInfo>? Items = null);

public record LoadComboItemInfo(
    string Name,
    string ItemType,
    double ScaleFactor,
    int ModeNumber);

public record StoryInfo(
    string Name,
    double Elevation,
    double? Height = null,
    bool? IsMasterStory = null,
    string? SimilarToStory = null);

public record GroupInfo(string Name, int AssignmentCount);

public record MaterialInfo
{
    public string Name { get; init; } = string.Empty;
    public string MaterialType { get; init; } = string.Empty;
    public string? SymType { get; init; }
    [JsonPropertyName("guid")]
    public string? MaterialGuid { get; init; }
    public double? ElasticModulus { get; init; }
    public double? PoissonRatio { get; init; }
    public double? ThermalExpansion { get; init; }
    public double? ShearModulus { get; init; }
    public double? WeightPerVolume { get; init; }
    public double? MassPerVolume { get; init; }
    public double? ConcreteFc { get; init; }
    public double? SteelFy { get; init; }
    public double? SteelFu { get; init; }
    public double? RebarFy { get; init; }
    public double? RebarFu { get; init; }
}

public record FrameSectionInfo(string Name, string SectionType);

public record AreaSectionInfo(string Name, string PropertyType);

public record MetadataWarning(string Category, string Message);

internal static class ModelMetadataUnits
{
    public static string FormatDisplay(UnitInfo units) =>
        $"{units.Force}/{units.Length}/{units.Temperature}";
}
