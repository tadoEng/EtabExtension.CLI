// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using EtabSharp.System.Models;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public class ExtractMaterialsService : IExtractMaterialsService
{
    private readonly IParquetService _parquet;

    public ExtractMaterialsService(IParquetService parquet)
    {
        _parquet = parquet;
    }

    public async Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(
        string filePath,
        string outputPath,
        string tableKey = "Material List by Story")
    {
        if (!File.Exists(filePath))
            return Result.Fail<ExtractMaterialsData>($"File not found: {filePath}");

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

            // Set units to US kip/ft/F — same as demo script — so values are predictable
            // This is safe: we're in a hidden instance with no user session
            app.Model.Units.SetPresentUnits(Units.US_Kip_Ft);
            Console.Error.WriteLine("ℹ Units set to US kip/ft/F");

            // ── Pull table via DatabaseTables API — mirrors demo script exactly ──
            Console.Error.WriteLine($"ℹ Fetching table: '{tableKey}'");
            var tableResult = app.Model.DatabaseTables.GetTableForDisplayArray(tableKey);

            if (!tableResult.IsSuccess)
                return Result.Fail<ExtractMaterialsData>(
                    $"Failed to load table '{tableKey}': " +
                    $"ret={tableResult.ReturnCode}, error='{tableResult.ErrorMessage}'");

            List<string> fields = tableResult.FieldKeysIncluded;
            List<string> flatData = tableResult.TableData;

            int columnCount = fields.Count;
            if (columnCount == 0)
                return Result.Fail<ExtractMaterialsData>(
                    $"Table '{tableKey}' returned no fields. Is the model analyzed?");

            int rowCount = flatData.Count / columnCount;

            Console.Error.WriteLine(
                $"ℹ Table '{tableKey}': {rowCount} rows × {columnCount} cols " +
                $"[{string.Join(", ", fields)}]");

            // Preview first rows to stderr — same as demo script PrintTableSummary
            int previewCount = Math.Min(3, rowCount);
            for (int r = 0; r < previewCount; r++)
            {
                var pairs = fields.Select((f, c) => $"{f}={flatData[r * columnCount + c]}");
                Console.Error.WriteLine($"  Row {r + 1}: {string.Join(" | ", pairs)}");
            }

            // ── Write parquet via shared service ──────────────────────────────
            Console.Error.WriteLine($"ℹ Writing parquet: {outputPath}");
            var writeResult = await _parquet.WriteAsync(outputPath, fields, flatData);

            if (!writeResult.Success)
                return Result.Fail<ExtractMaterialsData>(
                    $"Parquet write failed: {writeResult.Error}");

            stopwatch.Stop();
            Console.Error.WriteLine(
                $"✓ Wrote {writeResult.RowCount} rows ({stopwatch.ElapsedMilliseconds} ms)");

            return Result.Ok(new ExtractMaterialsData
            {
                FilePath = filePath,
                OutputFile = outputPath,
                TableKey = tableKey,
                RowCount = writeResult.RowCount,
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
}
