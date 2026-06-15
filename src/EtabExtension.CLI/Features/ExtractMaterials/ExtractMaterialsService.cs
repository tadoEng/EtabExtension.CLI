// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using System.Diagnostics;

namespace EtabExtension.CLI.Features.ExtractMaterials;

/// <summary>
/// Extracts one ETABS database table to a .parquet file via a hidden ETABS instance (Mode B).
///
/// ALIGNED WITH ExtractResultsService:
///   • Takes an ExtractMaterialsRequest (not loose parameters).
///   • Output dir, not output file — filename is {tableSlug}.parquet derived from the table key.
///   • Unit preset via request.Units — not hardcoded.
///   • LoadCases / LoadCombos intentionally null — material/geometry tables have no load dependency.
///   • 0-row result is Result.Ok (not an error), OutputFile is null in that case.
///   • Progress to Console.Error; JSON result to stdout via ExitWithResult().
/// </summary>
public class ExtractMaterialsService : IExtractMaterialsService
{
    private const string DefaultMaterialTableKey = "Material List by Story";
    private const string DefaultMaterialTableSlug = "material_list_by_story";

    private readonly IParquetService _parquet;
    private readonly IEtabsTableServicesFactory _tableFactory;
    private readonly IExtractResultsService _extractResultsService;
    private readonly IEtabsBootstrapService _bootstrap;

    public ExtractMaterialsService(
        IParquetService parquet,
        IEtabsTableServicesFactory tableFactory,
        IExtractResultsService extractResultsService,
        IEtabsBootstrapService bootstrap)
    {
        _parquet = parquet;
        _tableFactory = tableFactory;
        _extractResultsService = extractResultsService;
        _bootstrap = bootstrap;
    }

    public async Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(
        ExtractMaterialsRequest request)
    {
        var tableKey = string.IsNullOrWhiteSpace(request.TableKey)
            ? DefaultMaterialTableKey
            : request.TableKey;

        if (string.Equals(tableKey, DefaultMaterialTableKey, StringComparison.OrdinalIgnoreCase))
            return await ExtractViaCombinedResultsPathAsync(request, tableKey);

        return await ExtractViaLegacyPathAsync(request, tableKey);
    }

    private async Task<Result<ExtractMaterialsData>> ExtractViaCombinedResultsPathAsync(
        ExtractMaterialsRequest request,
        string tableKey)
    {
        var combinedRequest = new ExtractResultsRequest
        {
            FilePath = request.FilePath,
            OutputDir = request.OutputDir,
            Units = request.Units,
            Tables = new TableSelections
            {
                MaterialListByStory = new TableFilter
                {
                    FieldKeys = request.FieldKeys,
                }
            }
        };

        var result = await _extractResultsService.ExtractAsync(combinedRequest);
        if (!result.Success || result.Data is null)
            return Result.Fail<ExtractMaterialsData>(result.Error ?? $"Failed to load table '{tableKey}'.");

        if (!result.Data.Tables.TryGetValue(DefaultMaterialTableSlug, out var outcome))
            return Result.Fail<ExtractMaterialsData>(
                $"Combined extraction did not return '{DefaultMaterialTableSlug}'.");

        if (!outcome.Success)
            return Result.Fail<ExtractMaterialsData>(
                $"Failed to load table '{tableKey}': {outcome.Error}");

        return Result.Ok(new ExtractMaterialsData
        {
            FilePath = request.FilePath,
            OutputFile = outcome.OutputFile,
            TableKey = tableKey,
            RowCount = outcome.RowCount,
            DiscardedRowCount = outcome.DiscardedRowCount,
            Units = result.Data.Units,
            ExtractionTimeMs = outcome.ExtractionTimeMs
        });
    }

    private async Task<Result<ExtractMaterialsData>> ExtractViaLegacyPathAsync(
        ExtractMaterialsRequest request,
        string tableKey)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDir))
            return Result.Fail<ExtractMaterialsData>("OutputDir cannot be empty");

        var tableSlug = ToSlug(tableKey);
        var outputFile = Path.Combine(request.OutputDir, $"{tableSlug}.parquet");

        Directory.CreateDirectory(request.OutputDir);

        Console.Error.WriteLine(
            $"ℹ extract-materials: '{tableKey}' → {outputFile}");

        var sw = Stopwatch.StartNew();

        var bootstrapResult = await _bootstrap.BootstrapAsync(request.FilePath, request.Units);
        if (!bootstrapResult.Success || bootstrapResult.Data is null)
            return Result.Fail<ExtractMaterialsData>(bootstrapResult.Error ?? "Bootstrap failed");

        using var context = bootstrapResult.Data;
        var app = context.App;

        try
        {
            // ── Fetch table ───────────────────────────────────────────────────
            // Material / geometry tables have no load case or combo dependency.
            // LoadCases and LoadCombos are null → EtabsTableQueryService does NOT
            // call SetLoadCasesSelectedForDisplay / SetLoadCombinationsSelectedForDisplay.
            Console.Error.WriteLine($"ℹ Fetching table: '{tableKey}'");

            var queryService = _tableFactory.CreateQueryService(app);
            var queryResult = await queryService.QueryAsync(
                new TableQueryRequest(tableKey)
                {
                    FieldKeys = request.FieldKeys,
                    // LoadCases / LoadCombos intentionally omitted (null)
                });

            if (!queryResult.IsSuccess)
                return Result.Fail<ExtractMaterialsData>(
                    $"Failed to load table '{tableKey}': {queryResult.ErrorMessage}");

            if (queryResult.FieldKeys.Count == 0)
                return Result.Fail<ExtractMaterialsData>(
                    $"Table '{tableKey}' returned no fields — check that the table key is correct.");

            Console.Error.WriteLine(
                $"ℹ Table: {queryResult.RowCount} rows × {queryResult.FieldKeys.Count} cols" +
                $" [{string.Join(", ", queryResult.FieldKeys)}]");

            // Preview first 3 rows to help with debugging
            foreach (var (row, idx) in queryResult.Rows.Take(3).Select((r, i) => (r, i)))
                Console.Error.WriteLine(
                    $"  Row {idx + 1}: {string.Join(" | ", row.Select(kv => $"{kv.Key}={kv.Value}"))}");

            // ── 0-row is valid — empty model or filter returned nothing ────────
            if (queryResult.RowCount == 0)
            {
                Console.Error.WriteLine("⚠ Table returned 0 rows — parquet file not written");
                sw.Stop();
                return Result.Ok(new ExtractMaterialsData
                {
                    FilePath = request.FilePath,
                    OutputFile = null,
                    TableKey = tableKey,
                    RowCount = 0,
                    DiscardedRowCount = queryResult.DiscardedRowCount,
                    Units = context.Units?.Active,
                    ExtractionTimeMs = sw.ElapsedMilliseconds
                });
            }

            // ── Write parquet ─────────────────────────────────────────────────
            var flatData = queryResult.Rows
                .SelectMany(row => queryResult.FieldKeys.Select(f =>
                    row.TryGetValue(f, out var v) ? v : string.Empty))
                .ToList();

            var writeResult = await _parquet.WriteAsync(outputFile, queryResult.FieldKeys, flatData);
            if (!writeResult.Success)
                return Result.Fail<ExtractMaterialsData>($"Parquet write failed: {writeResult.Error}");

            sw.Stop();
            Console.Error.WriteLine(
                $"✓ {writeResult.RowCount} rows → {outputFile} ({sw.ElapsedMilliseconds} ms)");

            return Result.Ok(new ExtractMaterialsData
            {
                FilePath = request.FilePath,
                OutputFile = outputFile,
                TableKey = tableKey,
                RowCount = writeResult.RowCount,
                DiscardedRowCount = queryResult.DiscardedRowCount,
                Units = context.Units?.Active,
                ExtractionTimeMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<ExtractMaterialsData>($"Fatal error: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a table key to a snake_case filename slug.
    /// "Material List by Story" → "material_list_by_story"
    /// </summary>
    private static string ToSlug(string tableKey) =>
        tableKey.ToLowerInvariant()
                .Replace(' ', '_')
                .Replace('/', '_')
                .Replace('-', '_');
}
