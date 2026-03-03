// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.ExtractMaterials.Models;

public record ExtractMaterialsData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputFile")]
    public string OutputFile { get; init; } = string.Empty;

    [JsonPropertyName("tableKey")]
    public string TableKey { get; init; } = string.Empty;

    [JsonPropertyName("rowCount")]
    public int RowCount { get; init; }

    /// <summary>
    /// The unit system that was active during extraction.
    /// All numeric values in the parquet file are in these units.
    /// </summary>
    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }

    [JsonPropertyName("extractionTimeMs")]
    public long ExtractionTimeMs { get; init; }
}
