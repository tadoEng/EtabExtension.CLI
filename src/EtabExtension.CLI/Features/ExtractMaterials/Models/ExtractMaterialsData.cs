// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.ExtractMaterials.Models;

public record ExtractMaterialsData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// Path to the written .parquet file.
    /// null when the table returned 0 rows (file is not written for empty tables).
    /// </summary>
    [JsonPropertyName("outputFile")]
    public string? OutputFile { get; init; }

    [JsonPropertyName("tableKey")]
    public string TableKey { get; init; } = string.Empty;

    [JsonPropertyName("rowCount")]
    public int RowCount { get; init; }

    /// <summary>Rows dropped because every value was empty.</summary>
    [JsonPropertyName("discardedRowCount")]
    public int DiscardedRowCount { get; init; }

    /// <summary>
    /// Units active during extraction (after normalisation).
    /// All numeric values in the parquet file are in these units.
    /// </summary>
    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }

    [JsonPropertyName("extractionTimeMs")]
    public long ExtractionTimeMs { get; init; }
}
