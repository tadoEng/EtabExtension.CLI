using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using ETABSv1;
using System.Diagnostics;
using System.Text.Json;

namespace EtabExtension.CLI.Features.SnapshotExport;

public class SnapshotExportService : ISnapshotExportService
{
    private readonly IEtabsTableServicesFactory _tableFactory;
    private readonly TableExtractorRegistry _registry;
    private readonly IParquetService _parquet;

    public SnapshotExportService(
        IEtabsTableServicesFactory tableFactory,
        TableExtractorRegistry registry,
        IParquetService parquet)
    {
        _tableFactory = tableFactory;
        _registry = registry;
        _parquet = parquet;
    }

    public async Task<Result<SnapshotExportData>> SnapshotExportAsync(
        string filePath,
        string outputDir,
        SnapshotExportRequest request)
    {
        if (!File.Exists(filePath))
        {
            return Result.Fail<SnapshotExportData>($"File not found: {filePath}");
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return Result.Fail<SnapshotExportData>("OutputDir cannot be empty");
        }

        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null)
        {
            return Result.Fail<SnapshotExportData>(unitsError);
        }

        var tables = ExtractionProfiles.Resolve(
            request.Tables,
            request.ExtractionProfile,
            ExtractionProfiles.Snapshot);

        Directory.CreateDirectory(outputDir);
        var e2kFile = Path.Combine(outputDir, SafeFileName(request.E2KFileName, "model.e2k"));
        var materialsDir = Path.Combine(outputDir, SafeFileName(request.MaterialsDirName, "materials"));
        var metadataPath = Path.Combine(outputDir, SafeFileName(request.MetadataFileName, "model-metadata.json"));
        var metricsPath = Path.Combine(outputDir, SafeFileName(request.MetricsFileName, "run-metrics.json"));
        Directory.CreateDirectory(materialsDir);

        Console.Error.WriteLine($"ℹ snapshot-export: {filePath}");

        ETABSApplication? app = null;
        var totalSw = Stopwatch.StartNew();
        var metricsBuilder = new RunMetricsBuilder("snapshot-export", filePath, outputDir);

        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = metricsBuilder.Measure("startEtabs", () => ETABSWrapper.CreateNew());
            if (app is null)
            {
                return Result.Fail<SnapshotExportData>("Failed to start ETABS hidden instance.");
            }

            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            var openResult = await metricsBuilder.MeasureAsync(
                "openModel",
                () => EtabsSessionHelpers.OpenFileAsync(app, filePath));
            if (!openResult.Success)
            {
                return Result.Fail<SnapshotExportData>(openResult.Error ?? "OpenFile failed");
            }

            var unitSnapshot = await metricsBuilder.MeasureAsync(
                "normaliseUnits",
                () => EtabsSessionHelpers.NormaliseUnitsAsync(app, targetUnits));

            Console.Error.WriteLine("ℹ Exporting to .e2k...");
            var exportRet = metricsBuilder.Measure(
                "exportE2k",
                () => app.Model.Files.ExportFile(e2kFile, eFileTypeIO.TextFile));
            if (exportRet != 0 || !File.Exists(e2kFile))
            {
                return Result.Fail<SnapshotExportData>($"ExportFile failed (ret={exportRet})");
            }

            var e2kSize = new FileInfo(e2kFile).Length;
            Console.Error.WriteLine($"✓ Exported ({e2kSize / 1024.0:F1} KB)");

            var isAnalyzed = app.Model.Analyze.AreAllCasesFinished();
            var isLocked = app.Model.ModelInfo.IsLocked();
            var outcomes = await metricsBuilder.MeasureAsync(
                "extractTables",
                () => EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
                    app,
                    tables,
                    materialsDir,
                    isAnalyzed,
                    isLocked,
                    _tableFactory,
                    _registry,
                    _parquet));

            ModelMetadata? metadata = null;
            string? writtenMetadataPath = null;
            try
            {
                metadata = await metricsBuilder.MeasureAsync(
                    "collectMetadata",
                    () => EtabsSessionHelpers.CollectModelMetadataAsync(
                        app,
                        filePath,
                        unitSnapshot));

                Console.Error.WriteLine("ℹ Writing model-metadata.json");
                var metadataJson = JsonSerializer.Serialize(
                    metadata,
                    AnalyzeAndExtractService.MetadataJsonOptions);
                await metricsBuilder.MeasureAsync(
                    "writeMetadata",
                    () => File.WriteAllTextAsync(metadataPath, metadataJson));
                writtenMetadataPath = metadataPath;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"⚠ Metadata collection failed: {ex.Message}");
            }

            totalSw.Stop();
            var metrics = metricsBuilder.Build(totalSw.ElapsedMilliseconds);
            Console.Error.WriteLine("ℹ Writing run-metrics.json");
            var metricsJson = JsonSerializer.Serialize(
                metrics,
                AnalyzeAndExtractService.MetadataJsonOptions);
            await File.WriteAllTextAsync(metricsPath, metricsJson);

            var succeeded = outcomes.Values.Count(o => o.Success);
            var failed = outcomes.Values.Count(o => !o.Success);
            var totalRows = outcomes.Values.Sum(o => o.RowCount);

            Console.Error.WriteLine(
                $"✓ Done: E2K + {succeeded}/{outcomes.Count} tables, {totalRows} rows ({totalSw.ElapsedMilliseconds} ms)");

            return Result.Ok(new SnapshotExportData
            {
                FilePath = filePath,
                OutputDir = outputDir,
                E2KFile = e2kFile,
                E2KSizeBytes = e2kSize,
                MaterialsDir = materialsDir,
                Tables = outcomes,
                TotalRowCount = totalRows,
                SucceededCount = succeeded,
                FailedCount = failed,
                Metadata = metadata,
                MetadataPath = writtenMetadataPath,
                Metrics = metrics,
                MetricsPath = metricsPath,
                Units = unitSnapshot.Active,
                TotalElapsedMs = totalSw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<SnapshotExportData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    private static string SafeFileName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
