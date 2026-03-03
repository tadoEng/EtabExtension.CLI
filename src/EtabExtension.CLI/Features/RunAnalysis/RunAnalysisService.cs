// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.RunAnalysis;

public class RunAnalysisService : IRunAnalysisService
{
    public async Task<Result<RunAnalysisData>> RunAnalysisAsync(string filePath)
    {
        await Task.CompletedTask;

        if (!File.Exists(filePath))
            return Result.Fail<RunAnalysisData>($"File not found: {filePath}");

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

            // Unlock if locked from previous analysis
            if (app.Model.ModelInfo.IsLocked())
            {
                Console.Error.WriteLine("ℹ Clearing analysis lock...");
                app.Model.ModelInfo.SetLocked(false);
            }

            Console.Error.WriteLine("ℹ Running analysis (this may take several minutes)...");
            // RunCompleteAnalysis = SetAllCasesToRun + CreateAnalysisModel + RunAnalysis
            int loadcase = app.Model.Analyze.SetAllCasesToRun();

            int analysisRet = app.Model.Analyze.RunCompleteAnalysis();
            stopwatch.Stop();

            if (analysisRet != 0)
                return Result.Fail<RunAnalysisData>($"RunCompleteAnalysis failed (ret={analysisRet})")
                    with
                { Data = new RunAnalysisData { FilePath = filePath, AnalysisTimeMs = stopwatch.ElapsedMilliseconds } };

            Console.Error.WriteLine($"✓ Analysis complete ({FormatDuration(stopwatch.Elapsed)})");

            var caseStatuses = app.Model.Analyze.GetCaseStatus();
            var finishedCount = caseStatuses.Count(cs => cs.IsFinished);

            // Critical: save so results persist in .edb when hidden instance exits
            Console.Error.WriteLine("ℹ Saving results into .edb...");
            int saveRet = app.Model.Files.SaveFile(filePath);
            if (saveRet != 0)
                Console.Error.WriteLine($"⚠ SaveFile returned {saveRet} — results may not persist");
            else
                Console.Error.WriteLine("✓ Results saved");

            return Result.Ok(new RunAnalysisData
            {
                FilePath = filePath,
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
