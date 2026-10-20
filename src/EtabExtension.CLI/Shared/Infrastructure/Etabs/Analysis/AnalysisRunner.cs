// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Diagnostics;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Analysis;

/// <summary>
/// One analysis run on an already-open model: establish the selection, run, then judge
/// the result against that selection.
///
/// <para>Both entry points that analyse a model — the standalone <c>run-analysis</c>
/// command and the combined <c>analyze-and-extract</c> session — go through here. They
/// used to carry two copies of this sequence, which is why CLI #31's two defects existed
/// twice over.</para>
/// </summary>
internal static class AnalysisRunner
{
    internal static Result<RunAnalysisData> Run(
        IEtabsAnalysisApi api,
        string filePath,
        List<string>? cases,
        UnitInfo? units)
    {
        var requested = cases is { Count: > 0 } ? cases : null;

        // CLI #34: clear an earlier analysis BEFORE the census, so the census describes
        // the model this run will actually analyse.
        if (ClearEarlierResults(api) is { } clearError)
        {
            return Result.Fail<RunAnalysisData>(clearError);
        }

        var selection = AnalysisRunSelection.Establish(api, requested);
        if (!selection.Success || selection.Data is null)
        {
            return Result.Fail<RunAnalysisData>(
                selection.Error ?? "The analysis run selection could not be established.");
        }

        var runSet = selection.Data;
        ReportSelection(runSet);

        Console.Error.WriteLine("ℹ Running analysis (this may take several minutes)...");
        var stopwatch = Stopwatch.StartNew();

        var createRet = api.CreateAnalysisModel();
        if (createRet != 0)
        {
            stopwatch.Stop();
            return Result.Fail<RunAnalysisData>(
                $"cAnalyze.CreateAnalysisModel failed (ret={createRet})")
                with
            { Data = Partial(filePath, requested, runSet, stopwatch, units) };
        }

        var analysisRet = api.RunAnalysis();
        stopwatch.Stop();

        if (analysisRet != 0)
        {
            return Result.Fail<RunAnalysisData>($"Analysis failed (ret={analysisRet})")
                with
            { Data = Partial(filePath, requested, runSet, stopwatch, units) };
        }

        var evaluation = AnalysisRunSelection.Evaluate(api, runSet);
        if (evaluation.Data is null)
        {
            return Result.Fail<RunAnalysisData>(
                evaluation.Error ?? "The analysis outcome could not be read.")
                with
            { Data = Partial(filePath, requested, runSet, stopwatch, units) };
        }

        var outcome = evaluation.Data;
        var data = new RunAnalysisData
        {
            FilePath = filePath,
            CasesRequested = requested,
            CaseCount = outcome.ModelCaseCount,
            FinishedCaseCount = outcome.ModelFinishedCaseCount,
            CasesRun = runSet.Cases,
            CasesRunFinishedCount = outcome.RunSetFinishedCount,
            CasesNotFinished = outcome.NotFinished,
            AnalysisTimeMs = stopwatch.ElapsedMilliseconds,
            Units = units
        };

        if (!evaluation.Success)
        {
            Console.Error.WriteLine($"✗ {evaluation.Error}");
            return Result.Fail<RunAnalysisData>(evaluation.Error!) with { Data = data };
        }

        Console.Error.WriteLine(
            $"✓ Analysis complete — {outcome.RunSetFinishedCount} of {runSet.Cases.Count} " +
            $"requested case(s) finished ({FormatDuration(stopwatch.Elapsed)})");

        // ── DO NOT call SaveFile() ────────────────────────────────────────────
        // ETABS writes analysis results to sidecar files (.Y*, .K_*, .msh) during
        // the run. Calling SaveFile() overwrites the .EDB from in-memory state and
        // deletes those sidecar files.
        Console.Error.WriteLine(
            "ℹ Results written to sidecar files — skipping SaveFile() to preserve them");

        return Result.Ok(data);
    }

    /// <summary>
    /// Makes a model that already holds results ready for a genuine re-run, or explains
    /// why it cannot be.
    ///
    /// <para><b>CLI #34.</b> ETABS locks a model when it analyses it, and an engineer's
    /// saved EDB is normally in that state. This used to be handled by UNLOCKING it
    /// (<c>SetModelIsLocked(false)</c>). Measured live on ETABS 23.3, driving the CSI API
    /// directly with no sidecar involved, on an analysed copy of the sanctioned model:
    /// the unlock shrinks the run-case census from 31 cases to 23, resets the status
    /// report to 23 cases at "not run", and the following <c>CreateAnalysisModel</c> +
    /// <c>RunAnalysis</c> return 0 after ~6 s having analysed nothing. Every case stayed
    /// at status 1. The same model with its results DELETED instead
    /// (<c>DeleteResults(All)</c>) stays locked, keeps all 31 cases, and the identical
    /// <c>CreateAnalysisModel</c> + <c>RunAnalysis</c> perform a full fresh analysis
    /// (160 s, 31 of 31 finished).</para>
    ///
    /// <para>Deleting is also what makes the verdict honest. Leaving the results in place
    /// and simply re-running would let the previous analysis's status-4 cases answer for
    /// a run that may have done nothing. After a delete, every case starts at status 1,
    /// so only THIS run can mark it finished.</para>
    ///
    /// <para>A failed delete fails the request: the earlier results would still be there,
    /// and nothing could tell them apart from this run's.</para>
    /// </summary>
    private static string? ClearEarlierResults(IEtabsAnalysisApi api)
    {
        if (!api.GetModelIsLocked())
        {
            return null;
        }

        Console.Error.WriteLine(
            "ℹ The model holds results from an earlier analysis — deleting them so this run starts fresh");
        var ret = api.DeleteResults(string.Empty, all: true);
        return ret == 0
            ? null
            : $"cAnalyze.DeleteResults(All=True) failed (ret={ret}). The model's earlier analysis " +
              "results could not be cleared, so no analysis was run: its outcome could not be told " +
              "apart from the earlier one.";
    }

    private static void ReportSelection(AnalysisRunSet runSet)
    {
        Console.Error.WriteLine(runSet.IsAllCases
            ? $"ℹ Restored the all-cases run selection — running all {runSet.Cases.Count} case(s)"
            : $"ℹ Run selection established — running {runSet.Cases.Count} of " +
              $"{runSet.ModelCaseCount} case(s): {string.Join(", ", runSet.Cases)}");
    }

    /// <summary>
    /// The payload for a run that failed before its outcome could be read. It reports the
    /// selection that was established and nothing about results, because nothing about
    /// results is known.
    /// </summary>
    private static RunAnalysisData Partial(
        string filePath,
        List<string>? requested,
        AnalysisRunSet runSet,
        Stopwatch stopwatch,
        UnitInfo? units) => new()
        {
            FilePath = filePath,
            CasesRequested = requested,
            CaseCount = runSet.ModelCaseCount,
            CasesRun = runSet.Cases,
            AnalysisTimeMs = stopwatch.ElapsedMilliseconds,
            Units = units
        };

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s"
            : $"{ts.TotalSeconds:F1}s";
}
