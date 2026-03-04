// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;

/// <summary>
/// Result of a table edit operation.
/// Wraps ApplyEditedTablesResult and adds a summary of what changed.
/// </summary>
public record TableEditResult
{
    /// <summary>True when Apply succeeded with no fatal errors.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Human-readable error when IsSuccess is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The table that was edited.</summary>
    public string TableKey { get; init; } = string.Empty;

    /// <summary>Number of rows that were actually modified.</summary>
    public int RowsModified { get; init; }

    /// <summary>Human-readable summary of each change made, for audit logging.</summary>
    public List<string> ChangeLog { get; init; } = new();

    /// <summary>Fatal error count from ETABS Apply.</summary>
    public int FatalErrors { get; init; }

    /// <summary>Warning count from ETABS Apply.</summary>
    public int Warnings { get; init; }

    /// <summary>Full ETABS import log (populated when fillImportLog=true).</summary>
    public string ImportLog { get; init; } = string.Empty;

    public static TableEditResult Fail(string tableKey, string error) => new()
    {
        IsSuccess = false,
        TableKey = tableKey,
        ErrorMessage = error
    };

    public override string ToString() =>
        IsSuccess
            ? $"OK — {RowsModified} row(s) modified in '{TableKey}'"
            : $"FAILED — '{TableKey}': {ErrorMessage}";
}
