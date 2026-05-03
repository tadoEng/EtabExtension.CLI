using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract.Models;

public record ModelMetadata
{
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

    [JsonPropertyName("loadCases")]
    public List<LoadCaseInfo> LoadCases { get; init; } = [];

    [JsonPropertyName("loadCombinations")]
    public List<LoadComboInfo> LoadCombinations { get; init; } = [];

    [JsonPropertyName("stories")]
    public List<StoryInfo> Stories { get; init; } = [];

    [JsonPropertyName("groups")]
    public List<string> Groups { get; init; } = [];

    [JsonPropertyName("collectedAt")]
    public DateTimeOffset CollectedAt { get; init; }
}

public record LoadCaseInfo(string Name, string CaseType);
public record LoadComboInfo(string Name, string ComboType, List<string> ConstituentCases);
public record StoryInfo(string Name, double Elevation);

internal static class ModelMetadataUnits
{
    /// <summary>
    /// Returns a human-readable units string (e.g. "kip/ft/F").
    /// This is for display only — it is NOT a canonical preset string and
    /// cannot be round-tripped back through <see cref="EtabsUnitPreset.Resolve"/>.
    /// </summary>
    public static string FormatDisplay(UnitInfo units) =>
        $"{units.Force}/{units.Length}/{units.Temperature}";
}
