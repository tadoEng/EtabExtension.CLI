// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.ExtractResults.Models;

/// <summary>
/// Full extraction request passed in from Rust CLI.
///
/// Rust reads an Excel config file and populates this object, then serialises
/// it as a JSON argument or temp file.  Every table-level filter is optional —
/// a null list means "use whatever ETABS shows by default" (effectively all).
///
/// EXAMPLE JSON (from Rust):
/// {
///   "filePath": "C:\\models\\building.edb",
///   "outputDir": "C:\\output\\results",
///   "tables": {
///     "storyDefinitions": {},
///     "baseReactions":    { "loadCases": ["DEAD","LIVE","EQX","EQY","WIND_X","WIND_Y"] },
///     "storyForces":      { "loadCases": ["DEAD","LIVE","EQX","EQY","WIND_X","WIND_Y"] },
///     "jointDrifts":      { "loadCases": ["EQX","EQY","WIND_X","WIND_Y"], "groups": ["AllJoints"] },
///     "pierForces":       { "loadCombos": ["ENV-LRFD-MAX","ENV-LRFD-MIN"], "groups": ["Piers"] },
///     "pierSectionProperties": {}
///   }
/// }
/// </summary>
public record ExtractResultsRequest
{
    /// <summary>Path to the .edb file to open in a hidden ETABS instance.</summary>
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// Directory where all .parquet output files will be written.
    /// Each table produces one file named {tableSlug}.parquet.
    /// </summary>
    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    /// <summary>
    /// Per-table extraction config.  Only tables with a non-null entry here
    /// will be extracted.  An empty TableFilter {} means "extract with no filter".
    /// </summary>
    [JsonPropertyName("tables")]
    public TableSelections Tables { get; init; } = new();
}

/// <summary>
/// Declares which tables to extract and their individual filters.
/// Each property is null by default — null means "skip this table".
/// An empty <see cref="TableFilter"/> means "extract with no filter".
/// </summary>
public record TableSelections
{
    [JsonPropertyName("storyDefinitions")]
    public TableFilter? StoryDefinitions { get; init; }

    [JsonPropertyName("baseReactions")]
    public TableFilter? BaseReactions { get; init; }

    [JsonPropertyName("storyForces")]
    public TableFilter? StoryForces { get; init; }

    [JsonPropertyName("jointDrifts")]
    public TableFilter? JointDrifts { get; init; }

    [JsonPropertyName("pierForces")]
    public TableFilter? PierForces { get; init; }

    [JsonPropertyName("pierSectionProperties")]
    public TableFilter? PierSectionProperties { get; init; }
}

/// <summary>
/// Filter applied to a single table extraction.
/// All fields are optional — only those provided are applied.
/// </summary>
public record TableFilter
{
    /// <summary>Load case names to select before fetching. Null = all cases.</summary>
    [JsonPropertyName("loadCases")]
    public string[]? LoadCases { get; init; }

    /// <summary>Load combination names to select before fetching. Null = all combos.</summary>
    [JsonPropertyName("loadCombos")]
    public string[]? LoadCombos { get; init; }

    /// <summary>Load pattern names to select before fetching. Null = all patterns.</summary>
    [JsonPropertyName("loadPatterns")]
    public string[]? LoadPatterns { get; init; }

    /// <summary>
    /// ETABS group names to scope the query.
    /// When multiple groups are given, results are fetched per-group and merged.
    /// Null = entire model.
    /// </summary>
    [JsonPropertyName("groups")]
    public string[]? Groups { get; init; }

    /// <summary>Specific field keys (columns) to include. Null = all fields.</summary>
    [JsonPropertyName("fieldKeys")]
    public string[]? FieldKeys { get; init; }
}
