// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace EtabExtension.CLI.Features.ExtractResults;

/// <summary>
/// Orchestrates multi-table extraction from a single hidden ETABS instance.
///
/// FLOW:
///   1. Start hidden ETABS instance (Mode B).
///   2. Open .edb file.
///   3. Normalise units (from request.Units, default US_Kip_Ft).
///   4. For each entry in the registry:
///      a. Skip if the filter is null (table not requested).
///      b. Skip results tables when model is not analyzed/locked.
///      c. Call extractor.ExtractAsync().
///   5. Return ExtractResultsData with per-table outcomes.
///
/// PARTIAL FAILURES:
///   A single table failure does NOT abort the run. All requested tables are
///   attempted. Rust inspects the per-table outcomes to decide next steps.
///
/// TOP-LEVEL FAILURES (returned as Result.Fail):
///   • .edb file not found
///   • Unrecognised unit preset
///   • ETABS failed to start or open the file
/// </summary>
public class ExtractResultsService : IExtractResultsService
{
    private readonly IEtabsTableServicesFactory _tableFactory;
    private readonly IParquetService _parquet;
    private readonly TableExtractorRegistry _registry;
    private readonly IEtabsBootstrapService _bootstrap;
    private readonly ILogger<ExtractResultsService> _logger;

    public ExtractResultsService(
        IEtabsTableServicesFactory tableFactory,
        IParquetService parquet,
        TableExtractorRegistry registry,
        IEtabsBootstrapService bootstrap,
        ILogger<ExtractResultsService> logger)
    {
        _tableFactory = tableFactory;
        _parquet = parquet;
        _registry = registry;
        _bootstrap = bootstrap;
        _logger = logger;
    }

    public async Task<Result<ExtractResultsData>> ExtractAsync(ExtractResultsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDir))
            return Result.Fail<ExtractResultsData>("OutputDir cannot be empty");

        Directory.CreateDirectory(request.OutputDir);

        var planned = _registry.Entries
            .Where(e => e.FilterSelector(request.Tables) is not null)
            .ToList();

        if (planned.Count == 0)
            return Result.Fail<ExtractResultsData>(
                "No tables selected — all TableSelections properties are null. " +
                "Set at least one table filter in the request.");

        Console.Error.WriteLine(
            $"ℹ extract-results: {planned.Count} table(s) requested → {request.OutputDir}");

        var totalSw = Stopwatch.StartNew();

        var bootstrapResult = await _bootstrap.BootstrapAsync(request.FilePath, request.Units);
        if (!bootstrapResult.Success || bootstrapResult.Data is null)
            return Result.Fail<ExtractResultsData>(bootstrapResult.Error ?? "Bootstrap failed");

        using var context = bootstrapResult.Data;
        var app = context.App;

        try
        {
            // ── Check analysis state ──────────────────────────────────────────
            bool isAnalyzed = app.Model.Analyze.AreAllCasesFinished();
            bool isLocked = app.Model.ModelInfo.IsLocked();

            if (!isAnalyzed || !isLocked)
                Console.Error.WriteLine(
                    $"⚠ Model is {(isLocked ? "locked" : "unlocked")} / " +
                    $"{(isAnalyzed ? "analyzed" : "NOT analyzed")} — " +
                    "results tables will be skipped (geometry tables will still run)");
            else
                Console.Error.WriteLine("ℹ Model is analyzed and locked — all tables available");

            // ── Extract ───────────────────────────────────────────────────────
            var queryService = _tableFactory.CreateQueryService(app);
            var outcomes = new Dictionary<string, TableExtractionOutcome>();

            foreach (var entry in _registry.Entries)
            {
                var filter = entry.FilterSelector(request.Tables);
                if (filter is null)
                {
                    _logger.LogDebug("Skipping '{Label}' (not in request)", entry.Extractor.Label);
                    continue;
                }

                Console.Error.WriteLine(
                    $"ℹ [{outcomes.Count + 1}/{planned.Count}] Extracting: {entry.Extractor.Label}");

                TableExtractionOutcome outcome;
                if (entry.Extractor.RequiresAnalysis && (!isAnalyzed || !isLocked))
                {
                    outcome = TableExtractionOutcome.Fail(
                        "Model has no analysis results. Run analysis first (run-analysis command).");
                    Console.Error.WriteLine("  ⚠ Skipped — model not analyzed");
                }
                else
                {
                    outcome = await entry.Extractor.ExtractAsync(
                        filter, request.OutputDir, queryService, _parquet);
                }

                outcomes[entry.Extractor.Slug] = outcome;
                var status = outcome.Success
                    ? $"✓ {outcome.RowCount} rows → {Path.GetFileName(outcome.OutputFile ?? "(empty)")} ({outcome.ExtractionTimeMs} ms)"
                    : $"✗ FAILED: {outcome.Error}";
                Console.Error.WriteLine($"  {status}");
            }

            totalSw.Stop();

            var succeeded = outcomes.Values.Count(o => o.Success);
            var failed = outcomes.Values.Count(o => !o.Success);
            var totalRows = outcomes.Values.Sum(o => o.RowCount);

            Console.Error.WriteLine(
                $"✓ Done: {succeeded}/{outcomes.Count} tables succeeded, " +
                $"{totalRows} total rows ({totalSw.ElapsedMilliseconds} ms)");

            if (failed > 0)
                Console.Error.WriteLine(
                    $"⚠ Failed tables: {string.Join(", ", outcomes.Where(kv => !kv.Value.Success).Select(kv => kv.Key))}");

            return Result.Ok(new ExtractResultsData
            {
                FilePath = request.FilePath,
                OutputDir = request.OutputDir,
                Tables = outcomes,
                TotalRowCount = totalRows,
                SucceededCount = succeeded,
                FailedCount = failed,
                Units = context.Units?.Active,
                ExtractionTimeMs = totalSw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractResults fatal error");
            return Result.Fail<ExtractResultsData>($"Fatal error: {ex.Message}");
        }
    }
}
