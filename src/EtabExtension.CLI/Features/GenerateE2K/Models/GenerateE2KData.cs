using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.GenerateE2K.Models;

public record GenerateE2KData
{
    [JsonPropertyName("inputFile")]
    public string InputFile { get; init; } = string.Empty;

    [JsonPropertyName("outputFile")]
    public string OutputFile { get; init; } = string.Empty;

    [JsonPropertyName("fileSizeBytes")]
    public long FileSizeBytes { get; init; }

    [JsonPropertyName("generationTimeMs")]
    public long GenerationTimeMs { get; init; }
}
