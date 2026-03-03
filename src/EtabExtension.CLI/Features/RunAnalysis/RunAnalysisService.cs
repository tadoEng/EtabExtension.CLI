// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.RunAnalysis;

public class RunAnalysisService : IRunAnalysisService
{
    public async Task<Result<RunAnalysisData>> RunAnalysisAsync(
        string filePath, List<string>? cases)
    {
        await Task.CompletedTask;

        if (!File.Exists(filePath))
            return Result.Fail<RunAnalysisData>($"File not found: {filePath}");

        var hasSpecificCases = cases is { Count: > 0 };

        ETABSApplication? app = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
                return Result.Fail<RunAnalysisData>("Failed to start ETABS hidden instance.");

            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
                return Result.Fail<RunAnalysisData>($"OpenFile failed (ret={openRet})");

            if (app.Model.ModelInfo.IsLocked())
            {
                Console.Error.WriteLine("ℹ Clearing analysis lock...");
                app.Model.ModelInfo.SetLocked(false);
            }

            // ── Case selection ────────────────────────────────────────────────
            if (hasSpecificCases)
            {
                // Step 1: skip all cases first
                app.Model.Analyze.SetRunCaseFlag(caseName: "", run: false, all: true);
                Console.Error.WriteLine("ℹ Set all cases to skip");

                // Step 2: enable only the requested cases
                var notFound = new List<string>();
                foreach (var caseName in cases!)
                {
                    try
                    {
                        app.Model.Analyze.SetRunCaseFlag(caseName: caseName, run: true, all: false);
                        Console.Error.WriteLine($"  ✓ Set case '{caseName}' to run");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  ⚠ Case '{caseName}' not found or failed: {ex.Message}");
                        notFound.Add(caseName);
                    }
                }

                if (notFound.Count == cases!.Count)
                    return Result.Fail<RunAnalysisData>(
                        $"None of the requested cases were found: {string.Join(", ", notFound)}");

                if (notFound.Count > 0)
                    Console.Error.WriteLine(
                        $"⚠ {notFound.Count} case(s) not found: {string.Join(", ", notFound)}");
            }
            else
            {
                // Default: run all cases (what RunCompleteAnalysis does internally)
                Console.Error.WriteLine("ℹ Running all cases (default)");
            }

            // ── Analysis ──────────────────────────────────────────────────────
            Console.Error.WriteLine("ℹ Running analysis (this may take several minutes)...");

            // RunCompleteAnalysis = SetAllCasesToRun + CreateAnalysisModel + RunAnalysis
            // When we set specific cases above, we call CreateAnalysisModel + RunAnalysis directly
            // to avoid RunCompleteAnalysis resetting our case flags.
            int analysisRet;
            if (hasSpecificCases)
            {
                app.SapModel.Analyze.CreateAnalysisModel();
                analysisRet = app.SapModel.Analyze.RunAnalysis();
            }
            else
            {
                analysisRet = app.Model.Analyze.RunCompleteAnalysis();
            }

            stopwatch.Stop();

            if (analysisRet != 0)
                return Result.Fail<RunAnalysisData>(
                    $"Analysis failed (ret={analysisRet})")
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

            // Save results back into .edb — critical for extract-results to work
            Console.Error.WriteLine("ℹ Saving results into .edb...");
            int saveRet = app.Model.Files.SaveFile(filePath);
            if (saveRet != 0)
                Console.Error.WriteLine($"⚠ SaveFile returned {saveRet} — results may not persist");
            else
                Console.Error.WriteLine("✓ Results saved");

            return Result.Ok(new RunAnalysisData
            {
                FilePath = filePath,
                CasesRequested = hasSpecificCases ? cases : null,
                CaseCount = caseStatuses.Count,
                FinishedCaseCount = finishedCount,
                AnalysisTimeMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<RunAnalysisData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s"
            : $"{ts.TotalSeconds:F1}s";
}
