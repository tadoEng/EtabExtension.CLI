// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;
using EtabSharp.System.Models;
using System.Diagnostics;

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

            // ── Unit normalisation ────────────────────────────────────────────
            var unitService = new EtabsUnitService(app);
            var unitSnapshot = await unitService.ReadAndNormaliseAsync(EtabSharp.System.Models.Units.US_Kip_Ft);
            Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));

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

            // ── DO NOT call SaveFile() ────────────────────────────────────────
            //
            // ETABS stores analysis results in sidecar files alongside the .EDB:
            //   .Y, .Y01, .Y03, .Y09, .Y0A   — displacement / force result sets
            //   .K_I, .K_J, .K_M             — stiffness matrices
            //   .msh                          — mesh data
            //
            // These files are written directly to disk by ETABS during the
            // analysis run.  The .EDB itself is updated to record "analyzed"
            // status when analysis completes.
            //
            // Calling SaveFile() on a hidden instance AFTER analysis causes ETABS
            // to overwrite the .EDB from its in-memory model state — which does
            // not include the sidecar files.  The save deletes all sidecar files,
            // leaving a model that says "analyzed" but has no results on disk.
            //
            // The correct behavior: let ETABS exit normally via ApplicationExit().
            // The sidecar files and the updated .EDB lock state persist naturally.
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
        catch (Exception ex)
        {
            return Result.Fail<RunAnalysisData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            // ApplicationExit(false) — ETABS handles its own cleanup.
            // The sidecar result files written during analysis are already on disk
            // and will NOT be touched by a normal exit.
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    /// <summary>
    /// When specific cases were selected via SetRunCaseFlag, skip CreateAnalysisModel
    /// inside RunCompleteAnalysis (which would reset flags) and call directly.
    /// </summary>
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
