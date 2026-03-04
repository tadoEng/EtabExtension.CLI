// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using EtabSharp.Core;
using EtabSharp.DatabaseTables.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

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

    /// <inheritdoc />
    public async Task<TableQueryResult> QueryAsync(TableQueryRequest request)
    {
        if (request is null)
            return TableQueryResult.Fail(string.Empty, "request cannot be null");

        _logger.LogInformation(
            "QueryAsync: table='{TableKey}' cases={Cases} combos={Combos} patterns={Patterns} groups={Groups}",
            request.TableKey,
            request.LoadCases?.Length ?? 0,
            request.LoadCombos?.Length ?? 0,
            request.LoadPatterns?.Length ?? 0,
            request.Groups?.Length ?? 0);

        try
        {
            // ── Step 1: apply load selection ─────────────────────────────────
            await ApplyLoadSelectionAsync(request);

            // ── Step 2: fetch — once per group, or once with no group ─────────
            var groups = request.Groups is { Length: > 0 }
                ? request.Groups
                : new string?[] { null };   // single fetch, no group filter

            List<string> fieldKeys = new();
            int tableVersion = 0;
            var allRows = new List<Dictionary<string, string>>();
            var groupsQueried = new List<string>();

            foreach (var group in groups)
            {
                var raw = await GetTableArrayAsync(request.TableKey, group, request.FieldKeys);

                if (!raw.IsSuccess)
                {
                    // A missing/empty group is not fatal — log and skip
                    _logger.LogWarning(
                        "Group '{Group}' returned no data for table '{TableKey}': {Error}",
                        group ?? "(none)", request.TableKey, raw.ErrorMessage);
                    continue;
                }

                // All groups share the same schema; capture from the first hit
                if (fieldKeys.Count == 0)
                {
                    fieldKeys = raw.FieldKeysIncluded;
                    tableVersion = raw.TableVersion;
                }

                if (raw.NumberOfRecords > 0)
                {
                    allRows.AddRange(raw.GetStructuredData());
                    if (group is not null)
                        groupsQueried.Add(group);
                }
                else
                {
                    _logger.LogDebug(
                        "Group '{Group}' returned 0 rows for table '{TableKey}' — discarded",
                        group ?? "(none)", request.TableKey);
                }
            }

            // ── Step 3: de-duplicate rows from overlapping groups ─────────────
            var dedupedRows = DeduplicateRows(allRows, fieldKeys);
            var beforeFilter = dedupedRows.Count;

            // ── Step 4: discard rows where every field value is empty ─────────
            if (request.DiscardEmptyRows)
                dedupedRows = dedupedRows
                    .Where(row => row.Values.Any(v => !string.IsNullOrEmpty(v)))
                    .ToList();

            var discarded = beforeFilter - dedupedRows.Count;

            if (discarded > 0)
                _logger.LogDebug(
                    "Discarded {Count} empty row(s) from table '{TableKey}'",
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
            // ── Step 5: always reset load selection ───────────────────────────
            // Leave the model in a clean state regardless of success/failure.
            await ClearLoadSelectionAsync();
        }
    }

    // ── Low-level load selection ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SelectLoadCasesAsync(IEnumerable<string> caseNames)
    {
        await Task.CompletedTask;
        var names = caseNames?.ToArray() ?? Array.Empty<string>();
        if (names.Length == 0) return;
        _app.Model.DatabaseTables.SetLoadCasesSelectedForDisplay(names);
        _logger.LogDebug("Selected {Count} load case(s): [{Cases}]",
            names.Length, string.Join(", ", names));
    }

    /// <inheritdoc />
    public async Task SelectLoadCombosAsync(IEnumerable<string> comboNames)
    {
        await Task.CompletedTask;
        var names = comboNames?.ToArray() ?? Array.Empty<string>();
        if (names.Length == 0) return;
        _app.Model.DatabaseTables.SetLoadCombinationsSelectedForDisplay(names);
        _logger.LogDebug("Selected {Count} load combo(s): [{Combos}]",
            names.Length, string.Join(", ", names));
    }

    /// <inheritdoc />
    public async Task SelectLoadPatternsAsync(IEnumerable<string> patternNames)
    {
        await Task.CompletedTask;
        var names = patternNames?.ToArray() ?? Array.Empty<string>();
        if (names.Length == 0) return;
        _app.Model.DatabaseTables.SetLoadPatternsSelectedForDisplay(names);
        _logger.LogDebug("Selected {Count} load pattern(s)", names.Length);
    }

    /// <inheritdoc />
    public async Task ClearLoadSelectionAsync()
    {
        // Passing a single empty string tells ETABS to revert to "show all"
        // for that category.  Reset all three so no stale selection lingers.
        await Task.CompletedTask;
        _app.Model.DatabaseTables.SetLoadCasesSelectedForDisplay(new[] { "" });
        _app.Model.DatabaseTables.SetLoadCombinationsSelectedForDisplay(new[] { "" });
        _app.Model.DatabaseTables.SetLoadPatternsSelectedForDisplay(new[] { "" });
        _logger.LogDebug("Load selection cleared (all cases/combos/patterns will be shown)");
    }

    // ── Low-level raw fetch ──────────────────────────────────────────────────

    /// <inheritdoc />
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

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies load case / combo / pattern selection from the request.
    /// Categories with no entries are skipped — ETABS keeps its current
    /// selection for that category, which means "show all" in a fresh session.
    /// </summary>
    private async Task ApplyLoadSelectionAsync(TableQueryRequest request)
    {
        if (request.LoadCases is { Length: > 0 })
            await SelectLoadCasesAsync(request.LoadCases);

        if (request.LoadCombos is { Length: > 0 })
            await SelectLoadCombosAsync(request.LoadCombos);

        if (request.LoadPatterns is { Length: > 0 })
            await SelectLoadPatternsAsync(request.LoadPatterns);
    }

    /// <summary>
    /// Removes exact-duplicate rows that appear when multiple groups overlap
    /// (e.g. a joint belonging to both "WindJoints" and "SeismicJoints").
    /// Equality is determined by all field values in schema order.
    /// </summary>
    private static List<Dictionary<string, string>> DeduplicateRows(
        List<Dictionary<string, string>> rows,
        List<string> fieldKeys)
    {
        if (rows.Count == 0) return rows;

        var seen = new HashSet<string>();
        var result = new List<Dictionary<string, string>>(rows.Count);

        foreach (var row in rows)
        {
            // Build a canonical key from all values in field order
            var key = string.Join('\x1F', fieldKeys.Select(f =>
                row.TryGetValue(f, out var v) ? v : string.Empty));

            if (seen.Add(key))
                result.Add(row);
        }

        return result;
    }
}
