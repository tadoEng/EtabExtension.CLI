using EtabExtension.CLI.Features.ExtractResults.Models;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.SnapshotExport.Models;

public record SnapshotExportRequest
{
    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("e2kFileName")]
    public string E2KFileName { get; init; } = "model.e2k";

    [JsonPropertyName("materialsDirName")]
    public string MaterialsDirName { get; init; } = "materials";

    [JsonPropertyName("metadataFileName")]
    public string MetadataFileName { get; init; } = "model-metadata.json";

    [JsonPropertyName("metricsFileName")]
    public string MetricsFileName { get; init; } = "run-metrics.json";

    [JsonPropertyName("extractionProfile")]
    public string? ExtractionProfile { get; init; }

    [JsonPropertyName("tables")]
    public TableSelections Tables { get; init; } = new();
}
