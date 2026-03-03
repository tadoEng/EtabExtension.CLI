// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public class ExtractMaterialsService : IExtractMaterialsService
{
    public async Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(
        string filePath,
        string outputPath)
    {
        if (!File.Exists(filePath))
            return Result.Fail<ExtractMaterialsData>($"File not found: {filePath}");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

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

            Console.Error.WriteLine("ℹ Extracting material takeoff...");
            int numItems = 0;
            string[] storyName = [], matProp = [], matType = [];
            double[] dryWeight = [], volume = [];

            int ret = app.SapModel.Results.MaterialTakeoff(
                ref numItems, ref storyName, ref matProp,
                ref matType, ref dryWeight, ref volume);

            if (ret != 0)
                return Result.Fail<ExtractMaterialsData>($"MaterialTakeoff failed (ret={ret}). Model may not be analyzed.");

            Console.Error.WriteLine($"ℹ Writing {numItems} rows to parquet...");
            await WriteParquetAsync(outputPath, numItems, storyName, matProp, matType, volume, dryWeight);

            stopwatch.Stop();
            Console.Error.WriteLine($"✓ Wrote {numItems} rows ({stopwatch.ElapsedMilliseconds} ms)");

            return Result.Ok(new ExtractMaterialsData
            {
                FilePath = filePath,
                OutputFile = outputPath,
                RowCount = numItems,
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

    private static async Task WriteParquetAsync(
        string outputPath,
        int numItems,
        string[] storyName,
        string[] matProp,
        string[] matType,
        double[] volume,
        double[] dryWeight)
    {
        var schema = new ParquetSchema(
            new DataField<string>("storyName"),
            new DataField<string>("materialName"),
            new DataField<string>("materialType"),
            new DataField<double>("volumeM3"),
            new DataField<double>("massKg")
        );

        await using var stream = File.Create(outputPath);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream);
        await using var group = writer.CreateRowGroup();

        await group.WriteColumnAsync(new DataColumn(schema.DataFields[0], storyName[..numItems]));
        await group.WriteColumnAsync(new DataColumn(schema.DataFields[1], matProp[..numItems]));
        await group.WriteColumnAsync(new DataColumn(schema.DataFields[2], matType[..numItems]));
        await group.WriteColumnAsync(new DataColumn(schema.DataFields[3], volume[..numItems]));
        await group.WriteColumnAsync(new DataColumn(schema.DataFields[4], dryWeight[..numItems]));
    }
}
