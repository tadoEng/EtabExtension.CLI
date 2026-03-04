// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;

/// <summary>
/// Single input object for a table query.
///
/// Pass whatever you know — load cases, combos, patterns, groups — and the
/// service will apply all of them before fetching.  Any category with no
/// entries is skipped (treated as "show all" for that category).
/// Rows returned with zero data after filtering are automatically discarded.
///
/// EXAMPLES:
///
///   // Base reactions — all load cases, whole model
///   new TableQueryRequest("Base Reactions")
///   {
///       LoadCases = ["DEAD", "LIVE", "EQX", "EQY", "WIND_X", "WIND_Y"]
///   }
///
///   // Pier forces — LRFD combos, "Piers" group only
///   new TableQueryRequest("Pier Forces")
///   {
///       LoadCombos = ["1.2D+1.6L", "1.2D+1.0E+0.5L", "0.9D+1.0E"],
///       Groups     = ["Piers"]
///   }
///
///   // Joint drifts — wind + seismic cases, two groups merged
///   new TableQueryRequest("Joint Drifts")
///   {
///       LoadCases = ["WIND_X", "WIND_Y", "EQX", "EQY"],
///       Groups    = ["WindJoints", "SeismicJoints"],
///       FieldKeys = ["Joint", "OutputCase", "Drift1", "Drift2", "Drift3"]
///   }
///
///   // Materials — no filter (full model, all cases)
///   new TableQueryRequest("Material List by Story")
/// </summary>
public record TableQueryRequest
{
    public TableQueryRequest(string tableKey)
    {
        if (string.IsNullOrWhiteSpace(tableKey))
            throw new ArgumentException("tableKey cannot be null or empty", nameof(tableKey));
        TableKey = tableKey;
    }

    /// <summary>ETABS database table key (required).</summary>
    public string TableKey { get; init; }

    /// <summary>
    /// Load case names to select for display before fetching.
    /// Null or empty → no load-case filter applied (ETABS shows all cases).
    /// </summary>
    public string[]? LoadCases { get; init; }

    /// <summary>
    /// Load combination names to select for display before fetching.
    /// Null or empty → no combo filter applied.
    /// </summary>
    public string[]? LoadCombos { get; init; }

    /// <summary>
    /// Load pattern names to select for display before fetching.
    /// Null or empty → no pattern filter applied.
    /// </summary>
    public string[]? LoadPatterns { get; init; }

    /// <summary>
    /// ETABS group names to scope the query.
    ///
    /// When multiple groups are provided the service fetches each group
    /// separately and merges the rows, de-duplicating by full row equality.
    ///
    /// Null or empty → entire model (no group filter).
    /// </summary>
    public string[]? Groups { get; init; }

    /// <summary>
    /// Specific field keys (columns) to retrieve.
    /// Null → all fields.
    /// </summary>
    public string[]? FieldKeys { get; init; }

    /// <summary>
    /// When true (default), rows where every value is null or empty string
    /// are removed from the result after fetching.
    /// </summary>
    public bool DiscardEmptyRows { get; init; } = true;
}
