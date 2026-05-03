using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
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

    public async Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractAsync(
        AnalyzeAndExtractRequest request)
    {
        if (!File.Exists(request.FilePath))
        {
            return Result.Fail<AnalyzeAndExtractData>($"File not found: {request.FilePath}");
        }

        if (string.IsNullOrWhiteSpace(request.OutputDir))
        {
            return Result.Fail<AnalyzeAndExtractData>("OutputDir cannot be empty");
        }

        var (_, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null)
        {
            return Result.Fail<AnalyzeAndExtractData>(unitsError);
        }

        var planned = _registry.Entries
            .Where(e => e.FilterSelector(request.Tables) is not null)
            .ToList();

        if (planned.Count == 0)
        {
            return Result.Fail<AnalyzeAndExtractData>(
                "No tables selected — all TableSelections properties are null. " +
                "Set at least one table filter in the request.");
        }

        Directory.CreateDirectory(request.OutputDir);
        Console.Error.WriteLine($"ℹ analyze-and-extract: {request.FilePath}");

        ETABSApplication? app = null;
        var totalSw = Stopwatch.StartNew();

        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
            {
                return Result.Fail<AnalyzeAndExtractData>("Failed to start ETABS hidden instance.");
            }

            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            var openResult = await EtabsSessionHelpers.OpenFileAsync(app, request.FilePath);
            if (!openResult.Success)
            {
                return Result.Fail<AnalyzeAndExtractData>(openResult.Error ?? "OpenFile failed");
            }

            var unitSnapshot = await EtabsSessionHelpers.NormaliseUnitsAsync(app, request.Units);

            var analysisResult = await EtabsSessionHelpers.RunAnalysisOnOpenModelAsync(
                app,
                request.FilePath,
                request.Cases,
                unitSnapshot);

            if (!analysisResult.Success || analysisResult.Data is null)
            {
                return Result.Fail<AnalyzeAndExtractData>(
                    analysisResult.Error ?? "Analysis failed");
            }

            bool isAnalyzed = app.Model.Analyze.AreAllCasesFinished();
            bool isLocked = app.Model.ModelInfo.IsLocked();

            var extractionSw = Stopwatch.StartNew();
            var outcomes = await EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
                app,
                request.Tables,
                request.OutputDir,
                isAnalyzed,
                isLocked,
                _tableFactory,
                _registry,
                _parquet);
            extractionSw.Stop();

            ModelMetadata? metadata = null;
            string? metadataPath = null;
            try
            {
                metadata = await EtabsSessionHelpers.CollectModelMetadataAsync(
                    app,
                    request.FilePath,
                    unitSnapshot);

                metadataPath = Path.Combine(request.OutputDir, "model-metadata.json");
                Console.Error.WriteLine("ℹ Writing model-metadata.json");
                var metadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
                await File.WriteAllTextAsync(metadataPath, metadataJson);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"⚠ Metadata collection failed: {ex.Message}");
                metadata = null;
                metadataPath = null;
            }

            totalSw.Stop();

            var succeeded = outcomes.Values.Count(o => o.Success);
            var failed = outcomes.Values.Count(o => !o.Success);
            var totalRows = outcomes.Values.Sum(o => o.RowCount);

            Console.Error.WriteLine(
                $"✓ Done: {succeeded}/{outcomes.Count} tables, {totalRows} rows ({totalSw.ElapsedMilliseconds} ms)");

            return Result.Ok(new AnalyzeAndExtractData
            {
                FilePath = request.FilePath,
                OutputDir = request.OutputDir,
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
                Units = unitSnapshot.Active,
                TotalElapsedMs = totalSw.ElapsedMilliseconds
            });
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
}
