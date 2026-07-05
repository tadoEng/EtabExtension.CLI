using System.Text.Json.Serialization;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;

namespace EtabExtension.CLI.Features.Serve;

// Serve request payloads. For commands whose one-shot form takes CLI flags
// (--file, --output-dir, --save), those arguments move INTO the request JSON
// here so the whole call is a single line. The Rust client (Phase 2, #189)
// sends these shapes.

/// <summary>Payload for the <c>open-model</c> serve command.</summary>
public sealed class ServeOpenModelRequest
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("save")] public bool Save { get; init; }
}

/// <summary>Payload for the <c>analyze-and-extract</c> serve command.</summary>
public sealed class ServeAnalyzeRequest
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("outputDir")] public string OutputDir { get; init; } = string.Empty;
    [JsonPropertyName("request")] public AnalyzeAndExtractRequest Request { get; init; } = new();
}
