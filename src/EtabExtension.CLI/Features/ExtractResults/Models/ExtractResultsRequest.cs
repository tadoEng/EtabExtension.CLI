// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
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
///   "units":     "US_Kip_Ft",
///   "tables": {
///     "materialListByStory":      {},
///     "baseReactions":           { "loadCases": ["*"], "loadCombos": ["*"] },
///     "storyForces":             { "loadCases": ["DEAD","LIVE","EQX","EQY"] },
///     "pierForces":              { "loadCombos": ["ENV-LRFD-MAX","ENV-LRFD-MIN"], "groups": ["Piers"] },
///     "jointDrifts":             { "loadCases": ["EQX","EQY"], "loadCombos": ["ENV-DBE"], "groups": ["DriftJoints"] },
///     "storyDefinitions":        {},
///     "pierSectionProperties":   {},
///     "modalParticipatingMassRatios": {}
///   }
/// }
/// </summary>
public record ExtractResultsRequest
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    /// <summary>
    /// Unit preset to normalise to before extraction.
    /// null / omitted → defaults to "US_Kip_Ft".
    /// Valid values: US_Kip_Ft, US_Kip_In, US_Lb_Ft, US_Lb_In,
    ///               SI_kN_m, SI_kN_mm, SI_N_m, SI_N_mm, SI_kgf_m, SI_tonf_m
    /// </summary>
    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("tables")]
    public TableSelections Tables { get; init; } = new();
}

/// <summary>
/// Declares which tables to extract and their individual filters.
/// null property = skip that table entirely.
/// </summary>
public record TableSelections
{
    /// <summary>
    /// Material/geometry table. Only FieldKeys is honoured.
    /// </summary>
    [JsonPropertyName("materialListByStory")]
    public TableFilter? MaterialListByStory { get; init; }

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

    [JsonPropertyName("modalParticipatingMassRatios")]
    public TableFilter? ModalParticipatingMassRatios { get; init; }
}

/// <summary>
/// Per-table load filter for JSON deserialisation from Rust.
///
/// LOAD SELECTION RULES — same logic for both LoadCases and LoadCombos:
///
///   null or omitted      → select NOTHING for that category.
///   ["*"]                → select ALL items of that category from the model.
///   ["DEAD","LIVE",...]  → select exactly those named items.
///
/// The wildcard "*" is the only special value; it mirrors
/// <see cref="TableQueryRequest.Wildcard"/> which drives the actual ETABS behaviour.
/// Mixed arrays like ["DEAD","*"] are not supported.
///
/// EXAMPLES:
///   {}                                               // geometry tables
///   { "loadCases": ["*"], "loadCombos": ["*"] }     // all cases + all combos
///   { "loadCases": ["DEAD","LIVE"] }                 // 2 cases, no combos
///   { "loadCombos": ["ENV-LRFD"] }                   // no cases, 1 combo
/// </summary>
public record TableFilter
{
    /// <summary>
    /// Wildcard sentinel for JSON serialisation.
    /// Mirrors <see cref="TableQueryRequest.Wildcard"/> — same value, separate constant
    /// so feature models don't take a compile-time dependency on the infra layer.
    /// </summary>
    public const string Wildcard = TableQueryRequest.Wildcard;

    /// <summary>Convenience factory: select all load cases and all combos.</summary>
    public static TableFilter All => new() { LoadCases = [Wildcard], LoadCombos = [Wildcard] };

    [JsonPropertyName("loadCases")]
    public string[]? LoadCases { get; init; }

    [JsonPropertyName("loadCombos")]
    public string[]? LoadCombos { get; init; }

    [JsonPropertyName("groups")]
    public string[]? Groups { get; init; }

    [JsonPropertyName("fieldKeys")]
    public string[]? FieldKeys { get; init; }
}
