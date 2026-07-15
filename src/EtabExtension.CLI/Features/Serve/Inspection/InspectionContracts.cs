using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.Serve.Inspection;

public sealed class InspectWallPropertyRequest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class ResolveAreaTargetsRequest
{
    [JsonPropertyName("sourceProperty")]
    public string SourceProperty { get; init; } = string.Empty;
}

public sealed record InspectionUnitData(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("code")] int Code);

public sealed record ManagedIdentityData(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("processStartTimeUtc")] DateTimeOffset ProcessStartTimeUtc,
    [property: JsonPropertyName("executablePath")] string ExecutablePath,
    [property: JsonPropertyName("managedLaunchRecordId")] Guid ManagedLaunchRecordId);

public sealed record AnalysisResultsStateData(
    [property: JsonPropertyName("hasResults")] bool HasResults,
    [property: JsonPropertyName("caseCount")] int CaseCount,
    [property: JsonPropertyName("finishedCaseCount")] int FinishedCaseCount);

public sealed record GetModelStateData(
    [property: JsonPropertyName("modelPath")] string ModelPath,
    [property: JsonPropertyName("presentUnits")] InspectionUnitData PresentUnits,
    [property: JsonPropertyName("originalUnits")] InspectionUnitData OriginalUnits,
    [property: JsonPropertyName("executionUnits")] InspectionUnitData ExecutionUnits,
    [property: JsonPropertyName("isLocked")] bool IsLocked,
    [property: JsonPropertyName("analysisResults")] AnalysisResultsStateData AnalysisResults,
    [property: JsonPropertyName("savedFileFingerprint")] string? SavedFileFingerprint,
    [property: JsonPropertyName("identity")] ManagedIdentityData Identity);

public sealed record ListWallPropertiesData(
    [property: JsonPropertyName("names")] IReadOnlyList<string> Names,
    [property: JsonPropertyName("originalUnits")] InspectionUnitData OriginalUnits,
    [property: JsonPropertyName("executionUnits")] InspectionUnitData ExecutionUnits);

public sealed record WallShellDesignData(
    [property: JsonPropertyName("materialProperty")] string MaterialProperty,
    [property: JsonPropertyName("steelLayoutOption")] int SteelLayoutOption,
    [property: JsonPropertyName("designCoverTopDir1")] double DesignCoverTopDir1,
    [property: JsonPropertyName("designCoverTopDir2")] double DesignCoverTopDir2,
    [property: JsonPropertyName("designCoverBotDir1")] double DesignCoverBotDir1,
    [property: JsonPropertyName("designCoverBotDir2")] double DesignCoverBotDir2);

public sealed record InspectWallPropertyData(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("wallPropType")] string WallPropType,
    [property: JsonPropertyName("shellType")] string ShellType,
    [property: JsonPropertyName("materialProperty")] string MaterialProperty,
    [property: JsonPropertyName("thickness")] double Thickness,
    [property: JsonPropertyName("color")] int Color,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("guid")] string GlobalId,
    [property: JsonPropertyName("modifiers")] IReadOnlyList<double> Modifiers,
    [property: JsonPropertyName("shellDesign")] WallShellDesignData ShellDesign,
    [property: JsonPropertyName("originalUnits")] InspectionUnitData OriginalUnits,
    [property: JsonPropertyName("executionUnits")] InspectionUnitData ExecutionUnits);

public sealed record ResolvedAreaTargetData(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("story")] string Story,
    [property: JsonPropertyName("pier")] string Pier,
    [property: JsonPropertyName("guid")] string GlobalId,
    [property: JsonPropertyName("designOrientation")] string DesignOrientation);

public sealed record ResolveAreaTargetsData(
    [property: JsonPropertyName("sourceProperty")] string SourceProperty,
    [property: JsonPropertyName("targets")] IReadOnlyList<ResolvedAreaTargetData> Targets,
    [property: JsonPropertyName("originalUnits")] InspectionUnitData OriginalUnits,
    [property: JsonPropertyName("executionUnits")] InspectionUnitData ExecutionUnits);
