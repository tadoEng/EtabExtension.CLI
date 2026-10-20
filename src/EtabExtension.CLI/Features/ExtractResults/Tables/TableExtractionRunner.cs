// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Runs every PLANNED table of one extraction attempt, and guarantees the attempt cannot
/// leave an earlier run's bytes behind as if they were its own.
///
/// <para><b>CLI #33.</b> Extraction reuses the caller's output directory, and every table
/// lands at a fixed <c>&lt;slug&gt;.parquet</c>. The Rust calculation path resolves results
/// by exactly those names, with no run manifest. So when the current attempt produced no
/// file for a table — zero rows, a failed query, a failed or interrupted write, a results
/// table skipped because the model has no analysis — the previous run's file stayed
/// where it was, still plausible and still readable. A downstream reader then consumed
/// run N's numbers as run N+1's evidence. A missing file is loud; a stale one is
/// silent.</para>
///
/// <para>The repair is ordering, not inspection. Before ANY planned table is attempted,
/// each planned table's exact output path is removed. From then on, the only file that
/// can exist at that path is one written by this attempt. Several things follow:</para>
/// <list type="bullet">
/// <item><description>Every non-writing outcome leaves the path absent.</description></item>
/// <item><description>A run that dies halfway leaves later tables absent rather than stale.</description></item>
/// <item><description>No mtime or content heuristic is involved.</description></item>
/// </list>
///
/// <para>Scope is deliberately narrow. Only <c>&lt;slug&gt;.parquet</c> of a table this
/// request PLANNED is touched. That is an exact path the CLI owns, computed by
/// <see cref="OutputPathFor"/>. Unplanned tables, other files, and subdirectories in the
/// caller's directory are never removed, and nothing is deleted recursively.</para>
/// </summary>
public static class TableExtractionRunner
{
    /// <summary>
    /// The one place a table's output path is spelled. The extractor that writes the
    /// file and the invalidation that removes it must agree on it exactly.
    /// </summary>
    public static string OutputPathFor(string outputDir, string slug) =>
        Path.Combine(outputDir, $"{slug}.parquet");

    /// <summary>
    /// Attempts every planned table and returns one outcome per planned slug.
    ///
    /// <para>A table whose stale output cannot be removed is NOT attempted. Its outcome
    /// is a failure naming the file. Running it anyway could not make the path
    /// trustworthy: a write that fails for the same reason would leave the old bytes in
    /// place under a fresh outcome.</para>
    /// </summary>
    public static async Task<Dictionary<string, TableExtractionOutcome>> RunAsync(
        IReadOnlyList<TableRegistration> entries,
        TableSelections tables,
        string outputDir,
        bool isAnalyzed,
        IEtabsTableQueryService queryService,
        IParquetService parquet)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentNullException.ThrowIfNull(queryService);
        ArgumentNullException.ThrowIfNull(parquet);

        var planned = new List<(ITableExtractor Extractor, TableFilter Filter)>();
        foreach (var entry in entries)
        {
            var filter = entry.FilterSelector(tables);
            if (filter is not null)
            {
                planned.Add((entry.Extractor, filter));
            }
        }

        // ALL planned paths are invalidated before the first table runs, not each one just
        // before its own attempt: if the attempt dies after table 2, tables 3..n must be
        // absent, not left holding the previous run's bytes.
        var unremovable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (extractor, _) in planned)
        {
            var failure = RemovePreviousOutput(outputDir, extractor.Slug);
            if (failure is not null)
            {
                unremovable[extractor.Slug] = failure;
            }
        }

        var outcomes = new Dictionary<string, TableExtractionOutcome>(StringComparer.Ordinal);
        foreach (var (extractor, filter) in planned)
        {
            Console.Error.WriteLine(
                $"ℹ [{outcomes.Count + 1}/{planned.Count}] Extracting: {extractor.Label}");

            TableExtractionOutcome outcome;
            if (unremovable.TryGetValue(extractor.Slug, out var removalFailure))
            {
                outcome = TableExtractionOutcome.Fail(removalFailure);
                Console.Error.WriteLine("  ⚠ Not extracted — a previous run's output could not be removed");
            }
            // Result tables require analysis RESULTS (≥1 finished case), not a locked UI
            // state. The lock half of this gate previously skipped extraction when ETABS did
            // not leave the model locked post-run.
            else if (extractor.RequiresAnalysis && !isAnalyzed)
            {
                outcome = TableExtractionOutcome.Fail(
                    "Model has no analysis results. Run analysis first (run-analysis command).");
                Console.Error.WriteLine("  ⚠ Skipped — model not analyzed");
            }
            else
            {
                outcome = await extractor.ExtractAsync(filter, outputDir, queryService, parquet);
            }

            outcomes[extractor.Slug] = outcome;
            var status = outcome.Success
                ? $"✓ {outcome.RowCount} rows → {Path.GetFileName(outcome.OutputFile ?? "(empty)")} ({outcome.ExtractionTimeMs} ms)"
                : $"✗ FAILED: {outcome.Error}";
            Console.Error.WriteLine($"  {status}");
        }

        return outcomes;
    }

    /// <summary>
    /// Removes the previous run's output for one table, or explains why it could not.
    /// A path that does not exist is already in the state this wants.
    /// </summary>
    private static string? RemovePreviousOutput(string outputDir, string slug)
    {
        var path = OutputPathFor(outputDir, slug);
        try
        {
            File.Delete(path);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            // No directory, so no earlier output either.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return
                $"A previous run's output for '{slug}' could not be removed ({path}): {ex.Message}. " +
                "The table was not extracted, and that file must NOT be read as this run's result.";
        }
    }
}
