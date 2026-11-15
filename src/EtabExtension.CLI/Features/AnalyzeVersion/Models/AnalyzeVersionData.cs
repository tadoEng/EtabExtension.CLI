using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.AnalyzeVersion.Models;

/// <summary>The unit evidence of one analysis (analyze-version card M3, "Success data units").</summary>
public sealed record AnalyzeVersionUnits
{
    /// <summary>The model's own units: checked <c>GetDatabaseUnits_2</c> on the open scratch.</summary>
    [JsonPropertyName("database")] public required UnitBlock Database { get; init; }

    /// <summary>The version text's units the host expects (cross-check).</summary>
    [JsonPropertyName("expected")] public required UnitBlock Expected { get; init; }

    [JsonPropertyName("presentBefore")] public required UnitBlock PresentBefore { get; init; }

    /// <summary>The checked present-units read taken immediately before extraction.</summary>
    [JsonPropertyName("tables")] public required UnitBlock Tables { get; init; }

    /// <summary>The present units when the operation ended: the verified restore read-back, or <c>tables</c> when nothing was set.</summary>
    [JsonPropertyName("presentAfter")] public required UnitBlock PresentAfter { get; init; }

    [JsonPropertyName("changed")] public bool Changed { get; init; }

    /// <summary>True after a verified restore, null when no unit was changed (never omitted).</summary>
    [JsonPropertyName("restored")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? Restored { get; init; }

    [JsonPropertyName("policy")] public required string Policy { get; init; }
}

/// <summary>
/// The <c>analyze-version</c> success data: the analyze-and-extract fields plus the unit
/// evidence. <c>units</c> here is the evidence block, not the legacy single-unit snapshot.
/// </summary>
public sealed record AnalyzeVersionData
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("outputDir")] public string OutputDir { get; init; } = string.Empty;
    [JsonPropertyName("casesRequested")] public List<string>? CasesRequested { get; init; }
    [JsonPropertyName("caseCount")] public long CaseCount { get; init; }
    [JsonPropertyName("finishedCaseCount")] public long FinishedCaseCount { get; init; }
    [JsonPropertyName("analysisTimeMs")] public long AnalysisTimeMs { get; init; }
    [JsonPropertyName("tables")] public Dictionary<string, TableExtractionOutcome> Tables { get; init; } = [];
    [JsonPropertyName("totalRowCount")] public long TotalRowCount { get; init; }
    [JsonPropertyName("succeededCount")] public long SucceededCount { get; init; }
    [JsonPropertyName("failedCount")] public long FailedCount { get; init; }
    [JsonPropertyName("extractionTimeMs")] public long ExtractionTimeMs { get; init; }
    [JsonPropertyName("metadata")] public ModelMetadata? Metadata { get; init; }
    [JsonPropertyName("metadataPath")] public string? MetadataPath { get; init; }
    [JsonPropertyName("metrics")] public RunMetrics? Metrics { get; init; }
    [JsonPropertyName("metricsPath")] public string? MetricsPath { get; init; }
    [JsonPropertyName("units")] public required AnalyzeVersionUnits Units { get; init; }
    [JsonPropertyName("totalElapsedMs")] public long TotalElapsedMs { get; init; }
}

/// <summary>The nine typed failure tokens of <c>analyze-version</c> (card M3, AV-R6).</summary>
public static class AnalyzeVersionCodes
{
    public const string NotStarted = "ANALYSIS_NOT_STARTED";
    public const string DocumentMismatch = "ANALYSIS_DOCUMENT_MISMATCH";
    public const string Failed = "ANALYSIS_FAILED";
    public const string UnitsReadFailed = "ANALYSIS_UNITS_READ_FAILED";
    public const string ModelUnitsDisagree = "ANALYSIS_MODEL_UNITS_DISAGREE";
    public const string UnitsMismatch = "ANALYSIS_UNITS_MISMATCH";
    public const string UnitsSetFailed = "ANALYSIS_UNITS_SET_FAILED";
    public const string UnitsRestoreUnverified = "ANALYSIS_UNITS_RESTORE_UNVERIFIED";
    public const string ExtractFailed = "ANALYSIS_EXTRACT_FAILED";

    public static readonly IReadOnlyList<string> All =
    [
        NotStarted, DocumentMismatch, Failed, UnitsReadFailed, ModelUnitsDisagree,
        UnitsMismatch, UnitsSetFailed, UnitsRestoreUnverified, ExtractFailed
    ];
}
