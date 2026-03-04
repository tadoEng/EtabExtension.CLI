// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using EtabSharp.System.Models;
using System.Diagnostics;

namespace EtabExtension.CLI.Features.ExtractMaterials;

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
        string filePath,
        string outputPath,
        string tableKey = "Material List by Story")
    {
        if (!File.Exists(filePath))
            return Result.Fail<ExtractMaterialsData>($"File not found: {filePath}");

        // If the caller passed a directory (or a path with no .parquet extension),
        // derive a filename from the .edb stem + table key so the output is always
        // a concrete file path.  Examples:
        //   --output "C:\out"                 → C:\out\1350_FS.material_list_by_story.parquet
        //   --output "C:\out\results.parquet" → C:\out\results.parquet  (unchanged)
        outputPath = ResolveOutputPath(outputPath, filePath, tableKey);
        Console.Error.WriteLine($"ℹ Output: {outputPath}");

        ETABSApplication? app = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
                return Result.Fail<ExtractMaterialsData>("Failed to start ETABS hidden instance.");

            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
                return Result.Fail<ExtractMaterialsData>($"OpenFile failed (ret={openRet})");

            // ── Unit normalisation ────────────────────────────────────────────
            var unitService = new EtabsUnitService(app);
            var unitSnapshot = await unitService.ReadAndNormaliseAsync(EtabSharp.System.Models.Units.US_Kip_Ft);
            Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));

            // ── Table extraction via query service ────────────────────────────
            // Material List by Story has no load case / combo dependency.
            Console.Error.WriteLine($"ℹ Fetching table: '{tableKey}'");

            var queryService = _tableFactory.CreateQueryService(app);
            var queryResult = await queryService.QueryAsync(new TableQueryRequest(tableKey));

            if (!queryResult.IsSuccess)
                return Result.Fail<ExtractMaterialsData>(
                    $"Failed to load table '{tableKey}': {queryResult.ErrorMessage}");

            if (queryResult.FieldKeys.Count == 0)
                return Result.Fail<ExtractMaterialsData>(
                    $"Table '{tableKey}' returned no fields.");

            Console.Error.WriteLine(
                $"ℹ Table: {queryResult.RowCount} rows x {queryResult.FieldKeys.Count} cols" +
                $" [{string.Join(", ", queryResult.FieldKeys)}]");

            // Preview first 3 rows to stderr
            foreach (var (row, idx) in queryResult.Rows.Take(3).Select((r, i) => (r, i)))
            {
                var preview = row.Select(kv => $"{kv.Key}={kv.Value}");
                Console.Error.WriteLine($"  Row {idx + 1}: {string.Join(" | ", preview)}");
            }

            // ── Rebuild flat data for the Parquet writer ──────────────────────
            // Reconstructed from structured rows so empty-row filtering is reflected.
            var flatData = queryResult.Rows
                .SelectMany(row => queryResult.FieldKeys.Select(f =>
                    row.TryGetValue(f, out var v) ? v : string.Empty))
                .ToList();

            // ── Write parquet ─────────────────────────────────────────────────
            var writeResult = await _parquet.WriteAsync(outputPath, queryResult.FieldKeys, flatData);
            if (!writeResult.Success)
                return Result.Fail<ExtractMaterialsData>($"Parquet write failed: {writeResult.Error}");

            stopwatch.Stop();
            Console.Error.WriteLine(
                $"✓ {writeResult.RowCount} rows → {outputPath} ({stopwatch.ElapsedMilliseconds} ms)");

            return Result.Ok(new ExtractMaterialsData
            {
                FilePath = filePath,
                OutputFile = outputPath,
                TableKey = tableKey,
                RowCount = writeResult.RowCount,
                Units = unitSnapshot.Active,
                ExtractionTimeMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<ExtractMaterialsData>($"Error: {ex.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a concrete .parquet file path.
    /// When <paramref name="outputPath"/> is a directory (already exists as one,
    /// or has no file extension), a filename is derived from the .edb stem and
    /// the table key: "{edbStem}.{snake_table_key}.parquet"
    /// </summary>
    private static string ResolveOutputPath(string outputPath, string edbPath, string tableKey)
    {
        var isDirectory = Directory.Exists(outputPath)
                          || string.IsNullOrEmpty(Path.GetExtension(outputPath));

        if (!isDirectory)
            return outputPath;

        var edbStem = Path.GetFileNameWithoutExtension(edbPath);
        var tableSlug = tableKey
            .ToLowerInvariant()
            .Replace(' ', '_')
            .Replace('/', '_');

        return Path.Combine(outputPath, $"{edbStem}.{tableSlug}.parquet");
    }
}
