// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;

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
    /// Structured rows: Dictionary per row mapping fieldKey → value string.
    /// Empty rows are already discarded when DiscardEmptyRows was true.
    /// </summary>
    public List<Dictionary<string, string>> Rows { get; init; } = new();

    /// <summary>How many rows were dropped because all their values were empty.</summary>
    public int DiscardedRowCount { get; init; }

    public int TableVersion { get; init; }

    public static TableQueryResult Fail(string tableKey, string error) => new()
    {
        IsSuccess = false,
        TableKey = tableKey,
        ErrorMessage = error
    };
}
