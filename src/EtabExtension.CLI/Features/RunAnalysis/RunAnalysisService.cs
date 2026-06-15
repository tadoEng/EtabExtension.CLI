// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;
using System.Diagnostics;

namespace EtabExtension.CLI.Features.RunAnalysis;

public class RunAnalysisService : IRunAnalysisService
{
    private readonly IEtabsBootstrapService _bootstrap;

    public RunAnalysisService(IEtabsBootstrapService bootstrap)
    {
        _bootstrap = bootstrap;
    }

    public async Task<Result<RunAnalysisData>> RunAnalysisAsync(
        string filePath,
        List<string>? cases,
        string? units = null)
    {
        var hasSpecificCases = cases is { Count: > 0 };

        var bootstrapResult = await _bootstrap.BootstrapAsync(filePath, units);
        if (!bootstrapResult.Success || bootstrapResult.Data is null)
            return Result.Fail<RunAnalysisData>(bootstrapResult.Error ?? "Bootstrap failed");

        using var context = bootstrapResult.Data;
        var app = context.App;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (app.Model.ModelInfo.IsLocked())
            {
                Console.Error.WriteLine("ℹ Clearing analysis lock...");
                app.Model.ModelInfo.SetLocked(false);
            }

            // ── Case selection ────────────────────────────────────────────────
            if (hasSpecificCases)
            {
                app.Model.Analyze.SetRunCaseFlag(caseName: "", run: false, all: true);
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
                    return Result.Fail<RunAnalysisData>(
                        $"None of the requested cases were found: {string.Join(", ", notFound)}");
            }
            else
            {
                Console.Error.WriteLine("ℹ Running all cases (default)");
            }

            // ── Analysis ──────────────────────────────────────────────────────
            Console.Error.WriteLine("ℹ Running analysis (this may take several minutes)...");

            int analysisRet = hasSpecificCases
                ? RunSpecificCases(app)
                : app.Model.Analyze.RunCompleteAnalysis();

            stopwatch.Stop();

            if (analysisRet != 0)
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

            Console.Error.WriteLine($"✓ Analysis complete ({FormatDuration(stopwatch.Elapsed)})");

            var caseStatuses = app.Model.Analyze.GetCaseStatus();
            var finishedCount = caseStatuses.Count(cs => cs.IsFinished);
            if (finishedCount == 0)
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
                        Units = context.Units?.Active
                    }
                };

            // ── DO NOT call SaveFile() ────────────────────────────────────────
            // ETABS writes analysis results to sidecar files (.Y*, .K_*, .msh)
            // during the run. Calling SaveFile() overwrites the .EDB from
            // in-memory state and deletes those sidecar files.
            // Let ApplicationExit(false) handle clean shutdown instead.
            Console.Error.WriteLine(
                "ℹ Results written to sidecar files — skipping SaveFile() to preserve them");

            return Result.Ok(new RunAnalysisData
            {
                FilePath = filePath,
                CasesRequested = hasSpecificCases ? cases : null,
                CaseCount = caseStatuses.Count,
                FinishedCaseCount = finishedCount,
                AnalysisTimeMs = stopwatch.ElapsedMilliseconds,
                Units = context.Units?.Active
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<RunAnalysisData>($"ETABS COM error: {ex.Message}");
        }
    }

    private static int RunSpecificCases(ETABSApplication app)
    {
        app.SapModel.Analyze.CreateAnalysisModel();
        return app.SapModel.Analyze.RunAnalysis();
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s"
            : $"{ts.TotalSeconds:F1}s";
}
