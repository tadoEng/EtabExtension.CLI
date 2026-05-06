using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.SnapshotExport.Models;

public record SnapshotExportData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    [JsonPropertyName("e2kFile")]
    public string E2KFile { get; init; } = string.Empty;

    [JsonPropertyName("e2kSizeBytes")]
    public long E2KSizeBytes { get; init; }

    [JsonPropertyName("materialsDir")]
    public string MaterialsDir { get; init; } = string.Empty;

    [JsonPropertyName("tables")]
    public Dictionary<string, TableExtractionOutcome> Tables { get; init; } = [];

    [JsonPropertyName("totalRowCount")]
    public long TotalRowCount { get; init; }

    [JsonPropertyName("succeededCount")]
    public long SucceededCount { get; init; }

    [JsonPropertyName("failedCount")]
    public long FailedCount { get; init; }

    [JsonPropertyName("metadata")]
    public ModelMetadata? Metadata { get; init; }

    [JsonPropertyName("metadataPath")]
    public string? MetadataPath { get; init; }

    [JsonPropertyName("metrics")]
    public RunMetrics? Metrics { get; init; }

    [JsonPropertyName("metricsPath")]
    public string? MetricsPath { get; init; }

    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }

    [JsonPropertyName("totalElapsedMs")]
    public long TotalElapsedMs { get; init; }
}
