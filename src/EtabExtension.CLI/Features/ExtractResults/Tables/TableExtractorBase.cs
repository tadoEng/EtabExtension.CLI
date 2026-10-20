// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Base class that handles the common extract → write → report flow.
///
/// Subclasses only need to override <see cref="EtabsTableKey"/> and optionally
/// <see cref="BuildRequest"/> when the default (pass filter straight through)
/// is not enough.
///
/// Each concrete extractor lives in its own file and can document its own
/// expected columns and Rust-side filter conventions in its own XML doc.
/// </summary>
public abstract class TableExtractorBase : ITableExtractor
{
    protected ILogger Logger { get; }

    protected TableExtractorBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <inheritdoc />
    public abstract string Slug { get; }

    /// <inheritdoc />
    public abstract string Label { get; }

    /// <inheritdoc />
    /// Default is true — results tables require an analyzed, locked model.
    /// Geometry/definition extractors override this to false.
    public virtual bool RequiresAnalysis => true;

    /// <summary>
    /// The ETABS database table key exactly as ETABS expects it.
    /// e.g. "Base Reactions", "Story Forces", "Pier Forces"
    /// </summary>
    protected abstract string EtabsTableKey { get; }

    /// <summary>
    /// Builds the <see cref="TableQueryRequest"/> from the caller-supplied filter.
    ///
    /// The default implementation maps every filter field 1-to-1.
    /// Override when you want to ignore certain filter fields
    /// (e.g. geometry tables that have no load case dependency).
    /// </summary>
    protected virtual TableQueryRequest BuildRequest(TableFilter filter) =>
        new(EtabsTableKey)
        {
            LoadCases = filter.LoadCases,
            LoadCombos = filter.LoadCombos,
            Groups = filter.Groups,
            FieldKeys = filter.FieldKeys,
        };

    /// <inheritdoc />
    public async Task<TableExtractionOutcome> ExtractAsync(
        TableFilter filter,
        string outputDir,
        IEtabsTableQueryService queryService,
        IParquetService parquet)
    {
        var sw = Stopwatch.StartNew();
        Logger.LogInformation("Extracting '{Label}' ({TableKey})", Label, EtabsTableKey);

        // Set once a write has been started. From then on a failure may have left a
        // truncated file at the path (the writer streams straight into it), and a reader
        // must not mistake that for this run's table.
        string? writing = null;

        try
        {
            var queryResult = await queryService.QueryAsync(BuildRequest(filter));

            if (!queryResult.IsSuccess)
            {
                Logger.LogError("'{Label}' query failed: {Error}", Label, queryResult.ErrorMessage);
                return TableExtractionOutcome.Fail(queryResult.ErrorMessage ?? "Query failed");
            }

            if (queryResult.RowCount == 0)
            {
                Logger.LogWarning("'{Label}' returned 0 rows — skipping parquet write", Label);
                return new TableExtractionOutcome
                {
                    Success = true,  // empty result is not an error
                    RowCount = 0,
                    DiscardedRowCount = queryResult.DiscardedRowCount,
                    ExtractionTimeMs = sw.ElapsedMilliseconds
                };
            }

            // Reconstruct flat data from structured rows (respects empty-row filtering)
            var flatData = queryResult.Rows
                .SelectMany(row => queryResult.FieldKeys.Select(f =>
                    row.TryGetValue(f, out var v) ? v : string.Empty))
                .ToList();

            var outputFile = TableExtractionRunner.OutputPathFor(outputDir, Slug);
            writing = outputFile;
            var writeResult = await parquet.WriteAsync(outputFile, queryResult.FieldKeys, flatData);

            if (!writeResult.Success)
            {
                Logger.LogError("'{Label}' parquet write failed: {Error}", Label, writeResult.Error);
                return TableExtractionOutcome.Fail(
                    $"Parquet write failed: {writeResult.Error}{RemovePartialOutput(outputFile)}");
            }

            sw.Stop();
            Logger.LogInformation(
                "'{Label}' done: {Rows} rows → {File} ({Ms} ms)",
                Label, writeResult.RowCount, outputFile, sw.ElapsedMilliseconds);

            return new TableExtractionOutcome
            {
                Success = true,
                OutputFile = outputFile,
                RowCount = writeResult.RowCount,
                DiscardedRowCount = queryResult.DiscardedRowCount,
                Columns = writeResult.Columns,
                ExtractionTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "'{Label}' extraction threw an exception", Label);
            var removal = writing is null ? string.Empty : RemovePartialOutput(writing);
            return TableExtractionOutcome.Fail($"{ex.Message}{removal}");
        }
    }

    /// <summary>
    /// Removes whatever a failed write left at <paramref name="path"/>. Returns an empty
    /// string when the path is clean, or a sentence to append to the failure when it
    /// could not be cleaned, so the outcome never claims a clean failure it did not get.
    /// </summary>
    private static string RemovePartialOutput(string path)
    {
        try
        {
            File.Delete(path);
            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $" A partially written file could not be removed ({path}): {ex.Message}. " +
                   "It must NOT be read as this run's result.";
        }
    }
}
