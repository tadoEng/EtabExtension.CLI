// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractMaterials.Models;
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
    private readonly IParquetService _parquet;
    private readonly IEtabsTableServicesFactory _tableFactory;

    public ExtractMaterialsService(
        IParquetService parquet,
        IEtabsTableServicesFactory tableFactory)
    {
        _parquet = parquet;
        _tableFactory = tableFactory;
    }

    public async Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(
        ExtractMaterialsRequest request)
    {
        // ── Pre-flight ────────────────────────────────────────────────────────
        if (!File.Exists(request.FilePath))
            return Result.Fail<ExtractMaterialsData>($"File not found: {request.FilePath}");

        if (string.IsNullOrWhiteSpace(request.OutputDir))
            return Result.Fail<ExtractMaterialsData>("OutputDir cannot be empty");

        // ── Resolve units (fail fast before starting ETABS) ───────────────────
        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null)
            return Result.Fail<ExtractMaterialsData>(unitsError);

        var tableKey = string.IsNullOrWhiteSpace(request.TableKey)
                             ? "Material List by Story"
                             : request.TableKey;
        var tableSlug = ToSlug(tableKey);
        var outputFile = Path.Combine(request.OutputDir, $"{tableSlug}.parquet");

        Directory.CreateDirectory(request.OutputDir);

        Console.Error.WriteLine(
            $"ℹ extract-materials: '{tableKey}' → {outputFile}");

        ETABSApplication? app = null;
        var sw = Stopwatch.StartNew();

        try
        {
            // ── Start ETABS ───────────────────────────────────────────────────
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
                return Result.Fail<ExtractMaterialsData>("Failed to start ETABS hidden instance.");

            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            // ── Open file ─────────────────────────────────────────────────────
            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(request.FilePath)}");
            int openRet = app.Model.Files.OpenFile(request.FilePath);
            if (openRet != 0)
                return Result.Fail<ExtractMaterialsData>($"OpenFile failed (ret={openRet})");

            // ── Normalise units ───────────────────────────────────────────────
            var unitService = new EtabsUnitService(app);
            var unitSnapshot = await unitService.ReadAndNormaliseAsync(targetUnits);
            Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));

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
                    Units = unitSnapshot.Active,
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
                Units = unitSnapshot.Active,
                ExtractionTimeMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<ExtractMaterialsData>($"Fatal error: {ex.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
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
