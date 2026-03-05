// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.ExtractResults.Models;

/// <summary>
/// Full extraction request passed in from Rust CLI.
///
/// Rust reads an Excel config file, populates this object, then serialises it
/// as a JSON argument.  Only tables with a non-null entry in Tables are extracted.
///
/// JSON EXAMPLE:
/// {
///   "filePath":  "C:\\models\\building.edb",
///   "outputDir": "C:\\output\\results",
///   "tables": {
///     // All cases + all combos
///     "baseReactions":  { "loadCases": ["*"], "loadCombos": ["*"] },
///
///     // Specific cases only, no combos
///     "storyForces":    { "loadCases": ["DEAD","LIVE","EQX","EQY"] },
///
///     // Specific combos only, no cases
///     "pierForces":     { "loadCombos": ["ENV-LRFD-MAX","ENV-LRFD-MIN"], "groups": ["Piers"] },
///
///     // 2 cases + 1 combo
///     "jointDrifts":    { "loadCases": ["EQX","EQY"], "loadCombos": ["ENV-DBE"], "groups": ["DriftJoints"] },
///
///     // Geometry table — no load filter needed
///     "storyDefinitions":      {},
///     "pierSectionProperties": {}
///   }
/// }
/// </summary>
public record ExtractResultsRequest
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    [JsonPropertyName("tables")]
    public TableSelections Tables { get; init; } = new();
}

/// <summary>
/// Declares which tables to extract and their individual filters.
/// null property = skip that table entirely.
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
/// Filter for a single table extraction.
///
/// LOAD SELECTION RULES — same logic for both LoadCases and LoadCombos:
///
///   null or omitted      → select NOTHING for that category.
///                          No rows for that category will appear in the output.
///
///   ["*"]                → select ALL items of that category from the model.
///                          Use <see cref="TableFilter.All"/> as a convenience.
///
///   ["DEAD","LIVE",...]  → select exactly those named items.
///
/// The wildcard "*" is the only special value. Any other string is treated as
/// a literal name. Mixed arrays like ["DEAD","*"] are not supported — if "*"
/// is present, all items are selected regardless of other entries.
///
/// EXAMPLES:
///   {}                                               // geometry tables — no load filter
///   { "loadCases": ["*"], "loadCombos": ["*"] }     // all cases + all combos
///   { "loadCases": ["DEAD","LIVE"] }                 // 2 cases, no combos
///   { "loadCombos": ["ENV-LRFD"] }                   // no cases, 1 combo
///   { "loadCases": ["EQX"], "loadCombos": ["ENV"] }  // 1 case + 1 combo
/// </summary>
public record TableFilter
{
    /// <summary>Wildcard sentinel — pass as the only element to select all items.</summary>
    public const string Wildcard = "*";

    /// <summary>Convenience factory: select all load cases and all combos.</summary>
    public static TableFilter All => new()
    {
        LoadCases = [Wildcard],
        LoadCombos = [Wildcard],
    };

    /// <summary>
    /// Load case names to select.
    /// null / omitted = select nothing.
    /// ["*"]          = select all cases in the model.
    /// ["X","Y"]      = select exactly those cases.
    /// </summary>
    [JsonPropertyName("loadCases")]
    public string[]? LoadCases { get; init; }

    /// <summary>
    /// Load combination names to select.
    /// null / omitted = select nothing.
    /// ["*"]          = select all combos in the model.
    /// ["X","Y"]      = select exactly those combos.
    /// </summary>
    [JsonPropertyName("loadCombos")]
    public string[]? LoadCombos { get; init; }

    /// <summary>
    /// ETABS group names to scope the query.
    /// Multiple groups are fetched and merged (duplicates removed).
    /// null = entire model.
    /// </summary>
    [JsonPropertyName("groups")]
    public string[]? Groups { get; init; }

    /// <summary>Specific columns to include. null = all columns.</summary>
    [JsonPropertyName("fieldKeys")]
    public string[]? FieldKeys { get; init; }
}
