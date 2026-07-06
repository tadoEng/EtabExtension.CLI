using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using EtabSharp.System.Models;
using System.Diagnostics;
using System.Text.Json;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract;

public class AnalyzeAndExtractService : IAnalyzeAndExtractService
{
    public static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IEtabsTableServicesFactory _tableFactory;
    private readonly TableExtractorRegistry _registry;
    private readonly IParquetService _parquet;

    public AnalyzeAndExtractService(
        IEtabsTableServicesFactory tableFactory,
        TableExtractorRegistry registry,
        IParquetService parquet)
    {
        _tableFactory = tableFactory;
        _registry = registry;
        _parquet = parquet;
    }

    // One-shot: start a hidden ETABS, run, dispose it. Unchanged behavior.
    public async Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractAsync(
        string filePath,
        string outputDir,
        AnalyzeAndExtractRequest request)
    {
        var prep = Prepare(filePath, outputDir, request);
        if (prep.Error is not null)
        {
            return Result.Fail<AnalyzeAndExtractData>(prep.Error);
        }

        Console.Error.WriteLine($"ℹ analyze-and-extract: {filePath}");
        var metricsBuilder = new RunMetricsBuilder("analyze-and-extract", filePath, outputDir);
        var totalSw = Stopwatch.StartNew();
        ETABSApplication? app = null;
        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = metricsBuilder.Measure("startEtabs", () => ETABSWrapper.CreateNew());
            if (app is null)
            {
                return Result.Fail<AnalyzeAndExtractData>("Failed to start ETABS hidden instance.");
            }

            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            return await ExecuteAsync(
                app, filePath, outputDir, request, prep.Tables!, prep.TargetUnits!, metricsBuilder, totalSw);
        }
        catch (Exception ex)
        {
            return Result.Fail<AnalyzeAndExtractData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    // Daemon: run against the shared serve-session ETABS. Never creates or
    // disposes the app — the session owns its lifecycle.
    public async Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractOnAppAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        AnalyzeAndExtractRequest request)
    {
        var prep = Prepare(filePath, outputDir, request);
        if (prep.Error is not null)
        {
            return Result.Fail<AnalyzeAndExtractData>(prep.Error);
        }

        Console.Error.WriteLine($"ℹ analyze-and-extract (shared session): {filePath}");
        var metricsBuilder = new RunMetricsBuilder("analyze-and-extract", filePath, outputDir);
        var totalSw = Stopwatch.StartNew();
        try
        {
            return await ExecuteAsync(
                app, filePath, outputDir, request, prep.Tables!, prep.TargetUnits!, metricsBuilder, totalSw);
        }
        catch (Exception ex)
        {
            return Result.Fail<AnalyzeAndExtractData>($"ETABS COM error: {ex.Message}");
        }
    }

    private readonly record struct Preparation(string? Error, TableSelections? Tables, Units? TargetUnits);

    private Preparation Prepare(string filePath, string outputDir, AnalyzeAndExtractRequest request)
    {
        if (!File.Exists(filePath))
        {
            return new Preparation($"File not found: {filePath}", null, default);
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return new Preparation("OutputDir cannot be empty", null, default);
        }

        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null)
        {
            return new Preparation(unitsError, null, default);
        }

        var tables = ExtractionProfiles.Resolve(
            request.Tables,
            request.ExtractionProfile,
            ExtractionProfiles.Full);
        var planned = _registry.Entries
            .Where(e => e.FilterSelector(tables) is not null)
            .ToList();

        if (planned.Count == 0)
        {
            return new Preparation(
                "No tables selected — all TableSelections properties are null. " +
                "Set at least one table filter in the request.",
                null,
                default);
        }

        Directory.CreateDirectory(outputDir);
        return new Preparation(null, tables, targetUnits);
    }

    // The ETABS work, run against an already-open, caller-owned app.
    private async Task<Result<AnalyzeAndExtractData>> ExecuteAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        AnalyzeAndExtractRequest request,
        TableSelections tables,
        Units targetUnits,
        RunMetricsBuilder metricsBuilder,
        Stopwatch totalSw)
    {
        var openResult = await metricsBuilder.MeasureAsync(
            "openModel",
            () => EtabsSessionHelpers.OpenFileAsync(app, filePath));
        if (!openResult.Success)
        {
            return Result.Fail<AnalyzeAndExtractData>(openResult.Error ?? "OpenFile failed");
        }

        var unitSnapshot = await metricsBuilder.MeasureAsync(
            "normaliseUnits",
            () => EtabsSessionHelpers.NormaliseUnitsAsync(app, targetUnits));

        var analysisResult = await metricsBuilder.MeasureAsync(
            "runAnalysis",
            () => EtabsSessionHelpers.RunAnalysisOnOpenModelAsync(
                app,
                filePath,
                request.Cases,
                unitSnapshot));

        if (!analysisResult.Success || analysisResult.Data is null)
        {
            return Result.Fail<AnalyzeAndExtractData>(
                analysisResult.Error ?? "Analysis failed");
        }

        // Usable results = AT LEAST ONE finished case (the fixed gate).
        bool isAnalyzed = app.Model.Analyze.GetCaseStatus().Any(cs => cs.IsFinished);
        bool isLocked = app.Model.ModelInfo.IsLocked();

        var extractionSw = Stopwatch.StartNew();
        var outcomes = await metricsBuilder.MeasureAsync(
            "extractTables",
            () => EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
                app,
                tables,
                outputDir,
                isAnalyzed,
                isLocked,
                _tableFactory,
                _registry,
                _parquet));
        extractionSw.Stop();

        ModelMetadata? metadata = null;
        string? metadataPath = null;
        try
        {
            metadata = await metricsBuilder.MeasureAsync(
                "collectMetadata",
                () => EtabsSessionHelpers.CollectModelMetadataAsync(
                    app,
                    filePath,
                    unitSnapshot));

            metadataPath = string.IsNullOrWhiteSpace(request.MetadataOutputPath)
                ? Path.Combine(outputDir, "model-metadata.json")
                : request.MetadataOutputPath;
            var metadataDir = Path.GetDirectoryName(metadataPath);
            if (!string.IsNullOrWhiteSpace(metadataDir))
            {
                Directory.CreateDirectory(metadataDir);
            }
            Console.Error.WriteLine("ℹ Writing model-metadata.json");
            var metadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
            await metricsBuilder.MeasureAsync(
                "writeMetadata",
                () => File.WriteAllTextAsync(metadataPath, metadataJson));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Metadata collection failed: {ex.Message}");
            metadata = null;
            metadataPath = null;
        }

        totalSw.Stop();
        var metrics = metricsBuilder.Build(totalSw.ElapsedMilliseconds);
        var metricsPath = string.IsNullOrWhiteSpace(request.MetricsOutputPath)
            ? Path.Combine(outputDir, "run-metrics.json")
            : request.MetricsOutputPath;
        var metricsDir = Path.GetDirectoryName(metricsPath);
        if (!string.IsNullOrWhiteSpace(metricsDir))
        {
            Directory.CreateDirectory(metricsDir);
        }
        Console.Error.WriteLine("ℹ Writing run-metrics.json");
        var metricsJson = JsonSerializer.Serialize(metrics, MetadataJsonOptions);
        await File.WriteAllTextAsync(metricsPath, metricsJson);

        var succeeded = outcomes.Values.Count(o => o.Success);
        var failed = outcomes.Values.Count(o => !o.Success);
        var totalRows = outcomes.Values.Sum(o => o.RowCount);

        Console.Error.WriteLine(
            $"✓ Done: {succeeded}/{outcomes.Count} tables, {totalRows} rows ({totalSw.ElapsedMilliseconds} ms)");

        return Result.Ok(new AnalyzeAndExtractData
        {
            FilePath = filePath,
            OutputDir = outputDir,
            CasesRequested = analysisResult.Data.CasesRequested,
            CaseCount = analysisResult.Data.CaseCount,
            FinishedCaseCount = analysisResult.Data.FinishedCaseCount,
            AnalysisTimeMs = analysisResult.Data.AnalysisTimeMs,
            Tables = outcomes,
            TotalRowCount = totalRows,
            SucceededCount = succeeded,
            FailedCount = failed,
            ExtractionTimeMs = extractionSw.ElapsedMilliseconds,
            Metadata = metadata,
            MetadataPath = metadataPath,
            Metrics = metrics,
            MetricsPath = metricsPath,
            Units = unitSnapshot.Active,
            TotalElapsedMs = totalSw.ElapsedMilliseconds
        });
    }
}
