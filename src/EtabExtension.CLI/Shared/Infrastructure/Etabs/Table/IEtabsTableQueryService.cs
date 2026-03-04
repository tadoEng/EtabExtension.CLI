// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using EtabSharp.DatabaseTables.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;

/// <summary>
/// Reads ETABS database tables via a single unified <see cref="TableQueryRequest"/>.
///
/// The service:
///   1. Applies load-case / combo / pattern selection from the request.
///   2. Fetches the table once per group (or once with no group if none given).
///   3. Merges and de-duplicates rows across groups.
///   4. Discards rows where every field value is empty (configurable).
///   5. Resets load selection so the model is left in a clean state.
///
/// Low-level Select* / GetTableArrayAsync are kept for callers that need
/// fine-grained control, but most feature services should use QueryAsync.
/// </summary>
public interface IEtabsTableQueryService
{
    // ── Primary entry point ──────────────────────────────────────────────────

    /// <summary>
    /// Executes a unified query: applies all filters, fetches and merges across
    /// groups, discards empty rows, then resets filters.
    /// </summary>
    Task<TableQueryResult> QueryAsync(TableQueryRequest request);

    // ── Low-level primitives (for advanced callers) ──────────────────────────

    Task SelectLoadCasesAsync(IEnumerable<string> caseNames);
    Task SelectLoadCombosAsync(IEnumerable<string> comboNames);
    Task SelectLoadPatternsAsync(IEnumerable<string> patternNames);

    /// <summary>Resets all load selection so subsequent queries return full data.</summary>
    Task ClearLoadSelectionAsync();

    /// <summary>
    /// Raw fetch with no empty-row filtering or group merge.
    /// Callers are responsible for any prior Select* calls.
    /// </summary>
    Task<TableDataArrayResult> GetTableArrayAsync(
        string tableKey,
        string? groupName = null,
        string[]? fieldKeys = null);
}
