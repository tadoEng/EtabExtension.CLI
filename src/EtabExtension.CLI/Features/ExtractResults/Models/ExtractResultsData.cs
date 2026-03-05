// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.ExtractResults.Models;

/// <summary>
/// Top-level result returned to Rust after extraction.
/// </summary>
public record ExtractResultsData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    /// <summary>
    /// Per-table outcome.  Key = table slug (e.g. "base_reactions").
    /// Only tables that were requested appear here.
    /// </summary>
    [JsonPropertyName("tables")]
    public Dictionary<string, TableExtractionOutcome> Tables { get; init; } = new();

    /// <summary>Total rows written across all tables.</summary>
    [JsonPropertyName("totalRowCount")]
    public int TotalRowCount { get; init; }

    /// <summary>Number of tables that succeeded.</summary>
    [JsonPropertyName("succeededCount")]
    public int SucceededCount { get; init; }

    /// <summary>Number of tables that failed or were skipped.</summary>
    [JsonPropertyName("failedCount")]
    public int FailedCount { get; init; }

    /// <summary>Units active during extraction (after normalisation).</summary>
    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }

    [JsonPropertyName("extractionTimeMs")]
    public long ExtractionTimeMs { get; init; }
}

/// <summary>
/// Outcome for a single table extraction.
/// </summary>
public record TableExtractionOutcome
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("outputFile")]
    public string? OutputFile { get; init; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; init; }

    [JsonPropertyName("discardedRowCount")]
    public int DiscardedRowCount { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("extractionTimeMs")]
    public long ExtractionTimeMs { get; init; }

    public static TableExtractionOutcome Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}
