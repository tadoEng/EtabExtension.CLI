// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabSharp.DatabaseTables.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;

/// <summary>
/// Single input object for a table query.
///
/// LOAD SELECTION — null vs ["*"] vs specific names:
///
///   null / omitted   → select NOTHING for that category.
///                      Use for geometry tables (Story Definitions, Pier Section
///                      Properties) that have no load dependency.
///
///   ["*"]            → select ALL items of that category from the model.
///                      Use <see cref="TableFilter.Wildcard"/> constant.
///
///   ["X","Y",...]    → select exactly those named items.
///
/// EXAMPLES:
///
///   // All cases + all combos — use wildcard sentinel
///   new TableQueryRequest("Base Reactions")
///   {
///       LoadCases  = [TableFilter.Wildcard],
///       LoadCombos = [TableFilter.Wildcard]
///   }
///
///   // Specific cases only, no combos
///   new TableQueryRequest("Story Forces")
///   {
///       LoadCases = ["DEAD", "LIVE", "EQX", "EQY"]
///   }
///
///   // Specific combos only, no cases — with group scope
///   new TableQueryRequest("Pier Forces")
///   {
///       LoadCombos = ["ENV-LRFD-MAX", "ENV-LRFD-MIN"],
///       Groups     = ["Piers"]
///   }
///
///   // Geometry table — no load selection at all
///   new TableQueryRequest("Story Definitions")
///
///   // All cases + 1 combo — mixed wildcard and specific
///   new TableQueryRequest("Joint Drifts")
///   {
///       LoadCases  = [TableFilter.Wildcard],
///       LoadCombos = ["ENV-DBE"],
///       Groups     = ["DriftJoints"]
///   }
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
    /// Load case names to select before fetching.
    /// null          → select nothing (no case rows in output).
    /// ["*"]         → select all cases in the model.
    /// ["X","Y",...]  → select exactly those cases.
    /// </summary>
    public string[]? LoadCases { get; init; }

    /// <summary>
    /// Load combination names to select before fetching.
    /// null          → select nothing (no combo rows in output).
    /// ["*"]         → select all combos in the model.
    /// ["X","Y",...]  → select exactly those combos.
    /// </summary>
    public string[]? LoadCombos { get; init; }

    /// <summary>
    /// ETABS group names to scope the query.
    /// Multiple groups are fetched separately and merged (duplicates removed).
    /// null → entire model (no group filter).
    /// </summary>
    public string[]? Groups { get; init; }

    /// <summary>
    /// Specific columns to retrieve.
    /// null → all columns.
    /// </summary>
    public string[]? FieldKeys { get; init; }

    /// <summary>
    /// When true (default), rows where every value is null or empty
    /// are removed from the result after fetching.
    /// </summary>
    public bool DiscardEmptyRows { get; init; } = true;
}

/// <summary>
/// Result of a <see cref="TableQueryRequest"/>.
/// </summary>
public record TableQueryResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }

    public string TableKey { get; init; } = string.Empty;

    /// <summary>Groups that contributed rows. Empty when no group filter was used.</summary>
    public List<string> GroupsQueried { get; init; } = new();

    /// <summary>Ordered list of field keys (column headers).</summary>
    public List<string> FieldKeys { get; init; } = new();

    /// <summary>Row count after empty-row filtering.</summary>
    public int RowCount { get; init; }

    /// <summary>
    /// Structured rows: one Dictionary per row mapping fieldKey → value string.
    /// </summary>
    public List<Dictionary<string, string>> Rows { get; init; } = new();

    /// <summary>Rows dropped because every value was empty.</summary>
    public int DiscardedRowCount { get; init; }

    public int TableVersion { get; init; }

    public static TableQueryResult Fail(string tableKey, string error) => new()
    {
        IsSuccess = false,
        TableKey = tableKey,
        ErrorMessage = error
    };
}

/// <summary>
/// Reads ETABS database tables via a unified <see cref="TableQueryRequest"/>.
///
/// FLOW PER QUERY:
///   1. Resolve load selection (null=nothing / ["*"]=all / specific names).
///   2. Fetch the table once per group, or once with no group.
///   3. Merge and de-duplicate rows across groups.
///   4. Discard rows where every field value is empty (configurable).
///   5. Reset ETABS display selection back to all-selected.
///
/// Most callers should use <see cref="QueryAsync"/> directly.
/// <see cref="GetTableArrayAsync"/> is available for low-level access.
/// </summary>
public interface IEtabsTableQueryService
{
    /// <summary>
    /// Executes a full query: resolves load selection, fetches and merges
    /// across groups, discards empty rows, then resets display state.
    /// </summary>
    Task<TableQueryResult> QueryAsync(TableQueryRequest request);

    /// <summary>
    /// Resets ETABS display selection to all cases and all combos.
    /// Called automatically by <see cref="QueryAsync"/> — only needed if
    /// using <see cref="GetTableArrayAsync"/> directly.
    /// </summary>
    Task ClearLoadSelectionAsync();

    /// <summary>
    /// Raw fetch — no filtering, no group merge, no empty-row discard.
    /// Caller is responsible for setting up display selection beforehand
    /// and calling <see cref="ClearLoadSelectionAsync"/> afterward.
    /// </summary>
    Task<TableDataArrayResult> GetTableArrayAsync(
        string tableKey,
        string? groupName = null,
        string[]? fieldKeys = null);
}
