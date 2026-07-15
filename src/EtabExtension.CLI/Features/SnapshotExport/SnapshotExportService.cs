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
using EtabSharp.System.Models;
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

    // One-shot: start a hidden ETABS, export, dispose it. Unchanged behavior.
    public async Task<Result<SnapshotExportData>> SnapshotExportAsync(
        string filePath,
        string outputDir,
        SnapshotExportRequest request)
    {
        var prep = Prepare(filePath, outputDir, request);
        if (prep.Error is not null)
        {
            return Result.Fail<SnapshotExportData>(prep.Error);
        }

        Console.Error.WriteLine($"ℹ snapshot-export: {filePath}");
        var metricsBuilder = new RunMetricsBuilder("snapshot-export", filePath, outputDir);
        var totalSw = Stopwatch.StartNew();
        ETABSApplication? app = null;
        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = metricsBuilder.Measure("startEtabs", () => ETABSWrapper.CreateNew());
            if (app is null)
            {
                return Result.Fail<SnapshotExportData>("Failed to start ETABS hidden instance.");
            }

            EtabsSessionHelpers.HideIfVisible(app);
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            return await ExecuteAsync(app, filePath, outputDir, request, prep, metricsBuilder, totalSw);
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

    // Daemon: run against the shared serve-session ETABS (no create/dispose).
    public async Task<Result<SnapshotExportData>> SnapshotExportOnAppAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        SnapshotExportRequest request)
    {
        var prep = Prepare(filePath, outputDir, request);
        if (prep.Error is not null)
        {
            return Result.Fail<SnapshotExportData>(prep.Error);
        }

        Console.Error.WriteLine($"ℹ snapshot-export (shared session): {filePath}");
        var metricsBuilder = new RunMetricsBuilder("snapshot-export", filePath, outputDir);
        var totalSw = Stopwatch.StartNew();
        try
        {
            return await ExecuteAsync(app, filePath, outputDir, request, prep, metricsBuilder, totalSw);
        }
        catch (Exception ex)
        {
            return Result.Fail<SnapshotExportData>($"ETABS COM error: {ex.Message}");
        }
    }

    private readonly record struct Preparation(
        string? Error,
        TableSelections? Tables,
        Units? TargetUnits,
        string E2kFile,
        string MaterialsDir,
        string MetadataPath,
        string MetricsPath);

    private Preparation Prepare(string filePath, string outputDir, SnapshotExportRequest request)
    {
        if (!File.Exists(filePath))
        {
            return new Preparation($"File not found: {filePath}", null, default, "", "", "", "");
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return new Preparation("OutputDir cannot be empty", null, default, "", "", "", "");
        }

        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null)
        {
            return new Preparation(unitsError, null, default, "", "", "", "");
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

        return new Preparation(null, tables, targetUnits, e2kFile, materialsDir, metadataPath, metricsPath);
    }

    private async Task<Result<SnapshotExportData>> ExecuteAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        SnapshotExportRequest request,
        Preparation prep,
        RunMetricsBuilder metricsBuilder,
        Stopwatch totalSw)
    {
        var openResult = await metricsBuilder.MeasureAsync(
            "openModel",
            () => EtabsSessionHelpers.OpenFileAsync(app, filePath));
        if (!openResult.Success)
        {
            return Result.Fail<SnapshotExportData>(openResult.Error ?? "OpenFile failed");
        }

        var unitSnapshot = await metricsBuilder.MeasureAsync(
            "normaliseUnits",
            () => EtabsSessionHelpers.NormaliseUnitsAsync(app, prep.TargetUnits!));

        Console.Error.WriteLine("ℹ Exporting to .e2k...");
        var exportRet = metricsBuilder.Measure(
            "exportE2k",
            () => app.Model.Files.ExportFile(prep.E2kFile, eFileTypeIO.TextFile));
        if (exportRet != 0 || !File.Exists(prep.E2kFile))
        {
            return Result.Fail<SnapshotExportData>($"ExportFile failed (ret={exportRet})");
        }

        var e2kSize = new FileInfo(prep.E2kFile).Length;
        Console.Error.WriteLine($"✓ Exported ({e2kSize / 1024.0:F1} KB)");

        var isAnalyzed = app.Model.Analyze.GetCaseStatus().Any(cs => cs.IsFinished);
        var isLocked = app.Model.ModelInfo.IsLocked();
        var outcomes = await metricsBuilder.MeasureAsync(
            "extractTables",
            () => EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
                app,
                prep.Tables!,
                prep.MaterialsDir,
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
                () => EtabsSessionHelpers.CollectModelMetadataAsync(app, filePath, unitSnapshot));

            Console.Error.WriteLine("ℹ Writing model-metadata.json");
            var metadataJson = JsonSerializer.Serialize(
                metadata,
                AnalyzeAndExtractService.MetadataJsonOptions);
            await metricsBuilder.MeasureAsync(
                "writeMetadata",
                () => File.WriteAllTextAsync(prep.MetadataPath, metadataJson));
            writtenMetadataPath = prep.MetadataPath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Metadata collection failed: {ex.Message}");
        }

        totalSw.Stop();
        var metrics = metricsBuilder.Build(totalSw.ElapsedMilliseconds);
        Console.Error.WriteLine("ℹ Writing run-metrics.json");
        var metricsJson = JsonSerializer.Serialize(metrics, AnalyzeAndExtractService.MetadataJsonOptions);
        await File.WriteAllTextAsync(prep.MetricsPath, metricsJson);

        var succeeded = outcomes.Values.Count(o => o.Success);
        var failed = outcomes.Values.Count(o => !o.Success);
        var totalRows = outcomes.Values.Sum(o => o.RowCount);

        Console.Error.WriteLine(
            $"✓ Done: E2K + {succeeded}/{outcomes.Count} tables, {totalRows} rows ({totalSw.ElapsedMilliseconds} ms)");

        return Result.Ok(new SnapshotExportData
        {
            FilePath = filePath,
            OutputDir = outputDir,
            E2KFile = prep.E2kFile,
            E2KSizeBytes = e2kSize,
            MaterialsDir = prep.MaterialsDir,
            Tables = outcomes,
            TotalRowCount = totalRows,
            SucceededCount = succeeded,
            FailedCount = failed,
            Metadata = metadata,
            MetadataPath = writtenMetadataPath,
            Metrics = metrics,
            MetricsPath = prep.MetricsPath,
            Units = unitSnapshot.Active,
            TotalElapsedMs = totalSw.ElapsedMilliseconds
        });
    }

    private static string SafeFileName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
