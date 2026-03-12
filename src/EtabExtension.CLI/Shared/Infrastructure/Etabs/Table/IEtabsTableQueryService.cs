// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

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
///                      Use <see cref="Wildcard"/> constant.
///
///   ["X","Y",...]    → select exactly those named items.
///
/// EXAMPLES:
///
///   // All cases + all combos
///   new TableQueryRequest("Base Reactions")
///   {
///       LoadCases  = [TableQueryRequest.Wildcard],
///       LoadCombos = [TableQueryRequest.Wildcard]
///   }
///
///   // Specific cases only, no combos
///   new TableQueryRequest("Story Forces")
///   {
///       LoadCases = ["DEAD", "LIVE", "EQX", "EQY"]
///   }
///
///   // Geometry table — no load selection at all
///   new TableQueryRequest("Story Definitions")
/// </summary>
public record TableQueryRequest
{
    /// <summary>
    /// Wildcard sentinel — pass as the single element of LoadCases or LoadCombos
    /// to select ALL items of that category from the model.
    /// e.g. LoadCases = [TableQueryRequest.Wildcard]
    ///
    /// NOTE: This constant lives in the infrastructure layer intentionally.
    /// Features that define their own TableFilter models (ExtractResults) mirror
    /// it as TableFilter.Wildcard = "*" for JSON serialisation convenience, but
    /// the actual ETABS behaviour is driven by this value in EtabsTableQueryService.
    /// </summary>
    public const string Wildcard = "*";

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

    /// <summary>Specific columns to retrieve. null → all columns.</summary>
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

    /// <summary>Structured rows: one Dictionary per row mapping fieldKey → value string.</summary>
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
/// </summary>
public interface IEtabsTableQueryService
{
    Task<TableQueryResult> QueryAsync(TableQueryRequest request);
    Task ClearLoadSelectionAsync();
    Task<TableDataArrayResult> GetTableArrayAsync(
        string tableKey,
        string? groupName = null,
        string[]? fieldKeys = null);
}
