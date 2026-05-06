using EtabExtension.CLI.Features.ExtractResults.Models;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract.Models;

public record AnalyzeAndExtractRequest
{
    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("cases")]
    public List<string>? Cases { get; init; }

    [JsonPropertyName("tables")]
    public TableSelections Tables { get; init; } = new();

    [JsonPropertyName("metadataOutputPath")]
    public string? MetadataOutputPath { get; init; }

    [JsonPropertyName("metricsOutputPath")]
    public string? MetricsOutputPath { get; init; }

    [JsonPropertyName("extractionProfile")]
    public string? ExtractionProfile { get; init; }
}
