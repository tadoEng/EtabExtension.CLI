// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.ExtractMaterials.Models;

public record ExtractMaterialsData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputFile")]
    public string OutputFile { get; init; } = string.Empty;

    /// <summary>ETABS database table key that was extracted.</summary>
    [JsonPropertyName("tableKey")]
    public string TableKey { get; init; } = string.Empty;

    [JsonPropertyName("rowCount")]
    public int RowCount { get; init; }

    [JsonPropertyName("extractionTimeMs")]
    public long ExtractionTimeMs { get; init; }
}
