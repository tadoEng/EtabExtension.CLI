using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using System.Diagnostics;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

internal static class EtabsSessionHelpers
{
    internal static async Task<Result> OpenFileAsync(ETABSApplication app, string filePath)
    {
        await Task.CompletedTask;

        Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
        int openRet = app.Model.Files.OpenFile(filePath);
        if (openRet != 0)
        {
            return Result.Fail($"OpenFile failed (ret={openRet})");
        }

        Console.Error.WriteLine($"✓ Opened ({app.FullVersion})");
        return Result.Ok();
    }

    internal static async Task<UnitSnapshot> NormaliseUnitsAsync(
        ETABSApplication app,
        string? units)
    {
        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(units);
        if (unitsError is not null)
        {
            throw new ArgumentException(unitsError, nameof(units));
        }

        var unitService = new EtabsUnitService(app);
        var unitSnapshot = await unitService.ReadAndNormaliseAsync(targetUnits);
        Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));
        return unitSnapshot;
    }

    internal static async Task<Result<RunAnalysisData>> RunAnalysisOnOpenModelAsync(
        ETABSApplication app,
        string filePath,
        List<string>? cases,
        UnitSnapshot unitSnapshot)
    {
        await Task.CompletedTask;

        var hasSpecificCases = cases is { Count: > 0 };
        var stopwatch = Stopwatch.StartNew();

        if (app.Model.ModelInfo.IsLocked())
        {
            Console.Error.WriteLine("ℹ Clearing analysis lock...");
            app.Model.ModelInfo.SetLocked(false);
        }

        if (hasSpecificCases)
        {
            app.Model.Analyze.SetRunCaseFlag(caseName: string.Empty, run: false, all: true);
            Console.Error.WriteLine("ℹ Set all cases to skip");

            var notFound = new List<string>();
            foreach (var caseName in cases!)
            {
                try
                {
                    app.Model.Analyze.SetRunCaseFlag(caseName: caseName, run: true, all: false);
                    Console.Error.WriteLine($"  ✓ '{caseName}' set to run");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ⚠ '{caseName}' not found: {ex.Message}");
                    notFound.Add(caseName);
                }
            }

            if (notFound.Count == cases!.Count)
            {
                return Result.Fail<RunAnalysisData>(
                    $"None of the requested cases were found: {string.Join(", ", notFound)}");
            }
        }
        else
        {
            Console.Error.WriteLine("ℹ Running all cases (default)");
        }

        Console.Error.WriteLine("ℹ Running analysis...");

        int analysisRet = hasSpecificCases
            ? RunSpecificCases(app)
            : app.Model.Analyze.RunCompleteAnalysis();

        stopwatch.Stop();

        if (analysisRet != 0)
        {
            return Result.Fail<RunAnalysisData>($"Analysis failed (ret={analysisRet})")
                with
            {
                Data = new RunAnalysisData
                {
                    FilePath = filePath,
                    CasesRequested = hasSpecificCases ? cases : null,
                    AnalysisTimeMs = stopwatch.ElapsedMilliseconds
                }
            };
        }

        var caseStatuses = app.Model.Analyze.GetCaseStatus();
        var finishedCount = caseStatuses.Count(cs => cs.IsFinished);

        Console.Error.WriteLine(
            $"✓ Analysis complete ({caseStatuses.Count} cases, {finishedCount} finished, {stopwatch.ElapsedMilliseconds} ms)");

        if (finishedCount == 0)
        {
            return Result.Fail<RunAnalysisData>(
                "Analysis completed, but no cases were marked finished. Check ETABS run-case selections.")
                with
            {
                Data = new RunAnalysisData
                {
                    FilePath = filePath,
                    CasesRequested = hasSpecificCases ? cases : null,
                    CaseCount = caseStatuses.Count,
                    FinishedCaseCount = finishedCount,
                    AnalysisTimeMs = stopwatch.ElapsedMilliseconds,
                    Units = unitSnapshot.Active
                }
            };
        }

        Console.Error.WriteLine(
            "ℹ Results written to sidecar files — skipping SaveFile() to preserve them");

        return Result.Ok(new RunAnalysisData
        {
            FilePath = filePath,
            CasesRequested = hasSpecificCases ? cases : null,
            CaseCount = caseStatuses.Count,
            FinishedCaseCount = finishedCount,
            AnalysisTimeMs = stopwatch.ElapsedMilliseconds,
            Units = unitSnapshot.Active
        });
    }

    internal static async Task<Dictionary<string, TableExtractionOutcome>> ExtractTablesOnOpenModelAsync(
        ETABSApplication app,
        TableSelections tables,
        string outputDir,
        bool isAnalyzed,
        bool isLocked,
        IEtabsTableServicesFactory tableFactory,
        TableExtractorRegistry registry,
        IParquetService parquet)
    {
        var planned = registry.Entries
            .Where(e => e.FilterSelector(tables) is not null)
            .ToList();

        var queryService = tableFactory.CreateQueryService(app);
        var outcomes = new Dictionary<string, TableExtractionOutcome>();

        try
        {
            foreach (var entry in registry.Entries)
            {
                var filter = entry.FilterSelector(tables);
                if (filter is null)
                {
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
                        filter, outputDir, queryService, parquet);
                }

                outcomes[entry.Extractor.Slug] = outcome;
                var status = outcome.Success
                    ? $"✓ {outcome.RowCount} rows → {Path.GetFileName(outcome.OutputFile ?? "(empty)")} ({outcome.ExtractionTimeMs} ms)"
                    : $"✗ FAILED: {outcome.Error}";
                Console.Error.WriteLine($"  {status}");
            }

            return outcomes;
        }
        finally
        {
            await queryService.ResetSelectionAsync();
        }
    }

    internal static async Task<ModelMetadata> CollectModelMetadataAsync(
        ETABSApplication app,
        string filePath,
        UnitSnapshot unitSnapshot)
    {
        await Task.CompletedTask;

        Console.Error.WriteLine("ℹ Collecting model metadata...");

        var metadata = new ModelMetadata
        {
            FilePath = filePath,
            EtabsVersion = app.FullVersion,
            IsAnalyzed = ReadOrDefault("analysis state", () => app.Model.Analyze.AreAllCasesFinished(), false),
            IsLocked = ReadOrDefault("lock state", () => app.Model.ModelInfo.IsLocked(), false),
            Units = ModelMetadataUnits.Format(unitSnapshot.Active),
            LoadCases = ReadLoadCases(app),
            LoadCombinations = ReadLoadCombinations(app),
            Stories = ReadStories(app),
            Groups = ReadOrDefault("groups", () => app.Model.Groups.GetNameList().ToList(), []),
            CollectedAt = DateTimeOffset.UtcNow
        };

        Console.Error.WriteLine(
            $"✓ Metadata collected ({metadata.LoadCases.Count} cases, {metadata.LoadCombinations.Count} combos, {metadata.Stories.Count} stories)");

        return metadata;
    }

    private static int RunSpecificCases(ETABSApplication app)
    {
        app.SapModel.Analyze.CreateAnalysisModel();
        return app.SapModel.Analyze.RunAnalysis();
    }

    private static List<LoadCaseInfo> ReadLoadCases(ETABSApplication app)
    {
        try
        {
            var result = new List<LoadCaseInfo>();
            foreach (var name in app.Model.LoadCases.GetNameList())
            {
                try
                {
                    var (caseType, _) = app.Model.LoadCases.GetTypeOAPI(name);
                    result.Add(new LoadCaseInfo(name, caseType.ToString()));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"⚠ Could not read load case '{name}' type: {ex.Message}");
                    result.Add(new LoadCaseInfo(name, "Unknown"));
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Could not read load cases: {ex.Message}");
            return [];
        }
    }

    private static List<LoadComboInfo> ReadLoadCombinations(ETABSApplication app)
    {
        try
        {
            var result = new List<LoadComboInfo>();
            foreach (var name in app.Model.LoadCombinations.GetNameList())
            {
                var comboType = "Unknown";
                var cases = new List<string>();

                try
                {
                    comboType = ComboTypeLabel(app.Model.LoadCombinations.GetComboType(name));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"⚠ Could not read combo '{name}' type: {ex.Message}");
                }

                try
                {
                    cases = app.Model.LoadCombinations
                        .GetCaseList(name)
                        .Select(c => c.CaseName)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"⚠ Could not read combo '{name}' cases: {ex.Message}");
                }

                result.Add(new LoadComboInfo(name, comboType, cases));
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Could not read load combinations: {ex.Message}");
            return [];
        }
    }

    private static List<StoryInfo> ReadStories(ETABSApplication app)
    {
        try
        {
            var result = new List<StoryInfo>();
            foreach (var name in app.Model.Story.GetNameList())
            {
                try
                {
                    result.Add(new StoryInfo(name, app.Model.Story.GetElevation(name)));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"⚠ Could not read story '{name}' elevation: {ex.Message}");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Could not read stories: {ex.Message}");
            return [];
        }
    }

    private static T ReadOrDefault<T>(string label, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Could not read {label}: {ex.Message}");
            return fallback;
        }
    }

    private static string ComboTypeLabel(int comboType) => comboType switch
    {
        0 => "Linear Add",
        1 => "Envelope",
        2 => "Absolute Add",
        3 => "SRSS",
        4 => "Range Add",
        _ => $"Unknown ({comboType})"
    };
}
