// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

namespace EtabExtension.CLI.Shared.Infrastructure.Parquet;

/// <summary>
/// Writes flat ETABS database table data to a Parquet file.
/// All columns are string — ETABS DatabaseTables returns everything as strings.
/// Schema is built dynamically from the field keys returned by GetTableForDisplayArray.
/// </summary>
public interface IParquetService
{
    /// <summary>
    /// Write a flat ETABS table to a .parquet file.
    /// </summary>
    /// <param name="outputPath">Destination .parquet path. Directory is created if missing.</param>
    /// <param name="fieldNames">Column headers from tableResult.FieldKeysIncluded.</param>
    /// <param name="flatData">
    /// Row-major flat list from tableResult.TableData.
    /// Length must equal fieldNames.Count × rowCount.
    /// </param>
    Task<ParquetWriteResult> WriteAsync(
        string outputPath,
        List<string> fieldNames,
        List<string> flatData);
}

public record ParquetWriteResult(
    bool Success,
    int RowCount,
    string OutputPath,
    string? Error = null);
