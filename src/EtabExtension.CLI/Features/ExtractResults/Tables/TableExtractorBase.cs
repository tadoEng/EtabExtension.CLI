// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
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
    protected readonly ILogger Logger;

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
            LoadPatterns = filter.LoadPatterns,
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

            var outputFile = Path.Combine(outputDir, $"{Slug}.parquet");
            var writeResult = await parquet.WriteAsync(outputFile, queryResult.FieldKeys, flatData);

            if (!writeResult.Success)
            {
                Logger.LogError("'{Label}' parquet write failed: {Error}", Label, writeResult.Error);
                return TableExtractionOutcome.Fail($"Parquet write failed: {writeResult.Error}");
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
                ExtractionTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "'{Label}' extraction threw an exception", Label);
            return TableExtractionOutcome.Fail(ex.Message);
        }
    }
}
