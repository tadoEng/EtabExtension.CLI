// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.Core;
using EtabSharp.DatabaseTables.Models;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;

/// <inheritdoc cref="IEtabsTableQueryService"/>
public class EtabsTableQueryService : IEtabsTableQueryService
{
    private readonly ETABSApplication _app;
    private readonly ILogger<EtabsTableQueryService> _logger;

    public EtabsTableQueryService(ETABSApplication app, ILogger<EtabsTableQueryService> logger)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Primary entry point ──────────────────────────────────────────────────

    public async Task<TableQueryResult> QueryAsync(TableQueryRequest request)
    {
        if (request is null)
            return TableQueryResult.Fail(string.Empty, "request cannot be null");

        _logger.LogInformation(
            "QueryAsync: table='{TableKey}' cases={CasesMode} combos={CombosMode} groups={Groups}",
            request.TableKey,
            DescribeFilter(request.LoadCases),
            DescribeFilter(request.LoadCombos),
            request.Groups?.Length ?? 0);

        try
        {
            // ── Step 1: apply load selection ──────────────────────────────────
            await ApplyLoadSelectionAsync(request);

            // ── Step 2: fetch — once per group, or once for the whole model ───
            var groups = request.Groups is { Length: > 0 }
                ? request.Groups
                : new string?[] { null };

            List<string> fieldKeys = new();
            int tableVersion = 0;
            var allRows = new List<Dictionary<string, string>>();
            var groupsQueried = new List<string>();

            foreach (var group in groups)
            {
                var raw = await GetTableArrayAsync(request.TableKey, group, request.FieldKeys);

                if (!raw.IsSuccess)
                {
                    _logger.LogWarning(
                        "Group '{Group}' returned no data for '{TableKey}': {Error}",
                        group ?? "(none)", request.TableKey, raw.ErrorMessage);
                    continue;
                }

                if (fieldKeys.Count == 0)
                {
                    fieldKeys = raw.FieldKeysIncluded;
                    tableVersion = raw.TableVersion;
                }

                if (raw.NumberOfRecords > 0)
                {
                    allRows.AddRange(raw.GetStructuredData());
                    if (group is not null) groupsQueried.Add(group);
                }
                else
                    _logger.LogDebug("Group '{Group}' returned 0 rows for '{TableKey}'",
                        group ?? "(none)", request.TableKey);
            }

            // ── Step 3: de-duplicate rows from overlapping groups ─────────────
            var dedupedRows = DeduplicateRows(allRows, fieldKeys);
            var beforeFilter = dedupedRows.Count;

            // ── Step 4: discard rows where every value is empty ───────────────
            if (request.DiscardEmptyRows)
                dedupedRows = dedupedRows
                    .Where(row => row.Values.Any(v => !string.IsNullOrEmpty(v)))
                    .ToList();

            var discarded = beforeFilter - dedupedRows.Count;
            if (discarded > 0)
                _logger.LogDebug("Discarded {Count} empty row(s) from '{TableKey}'",
                    discarded, request.TableKey);

            _logger.LogInformation(
                "QueryAsync complete: table='{TableKey}' rows={Rows} discarded={Discarded}",
                request.TableKey, dedupedRows.Count, discarded);

            return new TableQueryResult
            {
                IsSuccess = true,
                TableKey = request.TableKey,
                GroupsQueried = groupsQueried,
                FieldKeys = fieldKeys,
                RowCount = dedupedRows.Count,
                Rows = dedupedRows,
                DiscardedRowCount = discarded,
                TableVersion = tableVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryAsync failed for table '{TableKey}'", request.TableKey);
            return TableQueryResult.Fail(request.TableKey, ex.Message);
        }
        finally
        {
            // ── Step 5: reset selection — leave ETABS in a clean state ────────
            await ResetSelectionAsync();
        }
    }

    public async Task ClearLoadSelectionAsync() => await ResetSelectionAsync();

    // ── Model enumeration ────────────────────────────────────────────────────

    private string[] GetAllLoadCaseNames()
    {
        try { return _app.Model.LoadCases.GetNameList() ?? Array.Empty<string>(); }
        catch (Exception ex)
        {
            _logger.LogWarning("GetAllLoadCaseNames failed: {Error}", ex.Message);
            return Array.Empty<string>();
        }
    }

    private string[] GetAllLoadComboNames()
    {
        try { return _app.Model.LoadCombinations.GetNameList() ?? Array.Empty<string>(); }
        catch (Exception ex)
        {
            _logger.LogWarning("GetAllLoadComboNames failed: {Error}", ex.Message);
            return Array.Empty<string>();
        }
    }

    // ── Load selection ───────────────────────────────────────────────────────

    /// <summary>
    /// RULE — same for both categories:
    ///   null      → select NOTHING  (do not call Set)
    ///   ["*"]     → select ALL      (enumerate model, call Set with full list)
    ///   ["X","Y"] → select EXACTLY  (call Set with those names)
    /// </summary>
    private async Task ApplyLoadSelectionAsync(TableQueryRequest request)
    {
        await Task.CompletedTask;

        ApplyFilter(
            filter: request.LoadCases,
            getAll: GetAllLoadCaseNames,
            setSelected: n => _app.Model.DatabaseTables.SetLoadCasesSelectedForDisplay(n),
            category: "cases");

        ApplyFilter(
            filter: request.LoadCombos,
            getAll: GetAllLoadComboNames,
            setSelected: n => _app.Model.DatabaseTables.SetLoadCombinationsSelectedForDisplay(n),
            category: "combos");
    }

    private void ApplyFilter(
        string[]? filter,
        Func<string[]> getAll,
        Action<string[]> setSelected,
        string category)
    {
        if (filter is null)
        {
            _logger.LogDebug("{Category}: null → nothing selected", category);
            return;
        }

        if (IsWildcard(filter))
        {
            var all = getAll();
            if (all.Length > 0)
            {
                setSelected(all);
                _logger.LogDebug("{Category}: wildcard → selected ALL {Count}", category, all.Length);
            }
            else
                _logger.LogDebug("{Category}: wildcard but none found in model", category);
            return;
        }

        setSelected(filter);
        _logger.LogDebug("{Category}: selected {Count} specific — [{Names}]",
            category, filter.Length, string.Join(", ", filter));
    }

    /// <summary>
    /// Resets ETABS display selection back to all cases and all combos after
    /// every query so the next table in the session starts from a known clean state.
    /// </summary>
    private async Task ResetSelectionAsync()
    {
        await Task.CompletedTask;

        var allCases = GetAllLoadCaseNames();
        if (allCases.Length > 0)
            _app.Model.DatabaseTables.SetLoadCasesSelectedForDisplay(allCases);

        var allCombos = GetAllLoadComboNames();
        if (allCombos.Length > 0)
            _app.Model.DatabaseTables.SetLoadCombinationsSelectedForDisplay(allCombos);

        _logger.LogDebug("Reset: {Cases} cases, {Combos} combos", allCases.Length, allCombos.Length);
    }

    // ── Low-level raw fetch ──────────────────────────────────────────────────

    public async Task<TableDataArrayResult> GetTableArrayAsync(
        string tableKey,
        string? groupName = null,
        string[]? fieldKeys = null)
    {
        await Task.CompletedTask;

        if (string.IsNullOrEmpty(tableKey))
            throw new ArgumentException("tableKey cannot be null or empty", nameof(tableKey));

        var group = groupName ?? string.Empty;
        _logger.LogDebug("GetTableArray: '{TableKey}' group='{Group}'", tableKey, group);

        var result = _app.Model.DatabaseTables.GetTableForDisplayArray(tableKey, fieldKeys, group);

        if (!result.IsSuccess)
            _logger.LogWarning("GetTableArray '{TableKey}': {Error}", tableKey, result.ErrorMessage);
        else
            _logger.LogDebug("GetTableArray '{TableKey}': {Rows} rows × {Cols} cols",
                tableKey, result.NumberOfRecords, result.FieldKeysIncluded.Count);

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsWildcard(string[] filter) =>
        filter.Length == 1 && filter[0] == TableQueryRequest.Wildcard;

    private static string DescribeFilter(string[]? filter) => filter switch
    {
        null => "nothing (null)",
        var f when f.Length == 1 && f[0] == "*" => "ALL (*)",
        _ => $"{filter.Length} specific"
    };

    private static List<Dictionary<string, string>> DeduplicateRows(
        List<Dictionary<string, string>> rows,
        List<string> fieldKeys)
    {
        if (rows.Count == 0) return rows;

        var seen = new HashSet<string>();
        var result = new List<Dictionary<string, string>>(rows.Count);

        foreach (var row in rows)
        {
            var key = string.Join('\x1F', fieldKeys.Select(f =>
                row.TryGetValue(f, out var v) ? v : string.Empty));
            if (seen.Add(key)) result.Add(row);
        }

        return result;
    }
}
