using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract.Models;

public record AnalyzeAndExtractData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    [JsonPropertyName("casesRequested")]
    public List<string>? CasesRequested { get; init; }

    [JsonPropertyName("caseCount")]
    public long CaseCount { get; init; }

    [JsonPropertyName("finishedCaseCount")]
    public long FinishedCaseCount { get; init; }

    [JsonPropertyName("analysisTimeMs")]
    public long AnalysisTimeMs { get; init; }

    [JsonPropertyName("tables")]
    public Dictionary<string, TableExtractionOutcome> Tables { get; init; } = [];

    [JsonPropertyName("totalRowCount")]
    public long TotalRowCount { get; init; }

    [JsonPropertyName("succeededCount")]
    public long SucceededCount { get; init; }

    [JsonPropertyName("failedCount")]
    public long FailedCount { get; init; }

    [JsonPropertyName("extractionTimeMs")]
    public long ExtractionTimeMs { get; init; }

    [JsonPropertyName("metadata")]
    public ModelMetadata? Metadata { get; init; }

    [JsonPropertyName("metadataPath")]
    public string? MetadataPath { get; init; }

    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }

    [JsonPropertyName("totalElapsedMs")]
    public long TotalElapsedMs { get; init; }
}
