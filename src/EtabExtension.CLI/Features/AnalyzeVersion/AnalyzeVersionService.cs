using EtabExtension.CLI.Features.AnalyzeVersion.Models;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.AnalyzeVersion;

public interface IAnalyzeVersionService
{
    /// <summary>
    /// The <c>analyze-version</c> body against an already-open, caller-owned model. Never
    /// starts, opens, saves or closes anything.
    /// </summary>
    Task<Result<AnalyzeVersionData>> AnalyzeOnApiAsync(
        IAnalyzeVersionApi api,
        AnalyzeVersionRequest request,
        IEtabsOperationProgress? progress = null);
}

/// <summary>
/// The daemon's <c>analyze-version</c> (analyze-version card M3, ten ordered steps):
///
/// <list type="number">
/// <item>the open document equals <c>expectedDocument</c> (no <c>OpenFile</c>);</item>
/// <item>checked <c>GetDatabaseUnits_2</c> = <c>database</c>;</item>
/// <item><c>database</c> must equal the host's <c>expectedUnits</c>, else nothing is analysed;</item>
/// <item><c>AnalysisRunner.Run</c> (no save);</item>
/// <item>checked <c>GetPresentUnits_2</c> = <c>presentBefore</c>;</item>
/// <item>equal: no set. Different: <c>checkOnly</c> refuses; <c>setAndRestore</c> ARMS the
/// restore obligation, THEN sets <c>database</c> and reads it back (AV-R2);</item>
/// <item>extract every selected table, metadata and metrics;</item>
/// <item><c>finally</c>, outside every cancellation check: restore <c>presentBefore</c> and verify
/// force, length AND temperature by a fresh read;</item>
/// <item>terminal success only after the verified restore.</item>
/// </list>
///
/// <para>No unit conversion happens here (card I-8): the tables simply come out in the units
/// ETABS presents, which this operation has verified equal the model's own.</para>
/// </summary>
public sealed class AnalyzeVersionService : IAnalyzeVersionService
{
    private static readonly JsonSerializerOptions DetailJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly ISavedFileStampReader _stamps;
    private readonly Func<string, string, FileIdentityResult> _compareIdentity;

    public AnalyzeVersionService(ISavedFileStampReader stamps)
        : this(stamps, WindowsFileIdentity.Compare)
    {
    }

    internal AnalyzeVersionService(
        ISavedFileStampReader stamps,
        Func<string, string, FileIdentityResult> compareIdentity)
    {
        _stamps = stamps;
        _compareIdentity = compareIdentity;
    }

    public async Task<Result<AnalyzeVersionData>> AnalyzeOnApiAsync(
        IAnalyzeVersionApi api,
        AnalyzeVersionRequest request,
        IEtabsOperationProgress? progress = null)
    {
        var valid = AnalyzeVersionRequestValidator.Validate(request, out var invalid);
        if (valid is null)
        {
            return Result.Fail<AnalyzeVersionData>(invalid!);
        }

        var tables = ExtractionProfiles.Resolve(
            request.Tables ?? new TableSelections(),
            request.ExtractionProfile,
            ExtractionProfiles.Full);
        if (api.CountPlannedTables(tables) == 0)
        {
            return Result.Fail<AnalyzeVersionData>(
                "No tables selected - all TableSelections properties are null. " +
                "Set at least one table filter in the request.");
        }

        Directory.CreateDirectory(valid.OutputDir);
        Console.Error.WriteLine($"ℹ analyze-version: {valid.FilePath}");
        var run = new RunState(valid, tables, new RunMetricsBuilder("analyze-version", valid.FilePath, valid.OutputDir), progress);

        Result<AnalyzeVersionData>? body = null;
        Exception? thrown = null;
        try
        {
            // No ConfigureAwait(false) here or in the body: the restore below is COM work and
            // must resume on the daemon's owning STA worker (AV-L4 item 1).
            body = await BodyAsync(api, run);
        }
#pragma warning disable CA1031 // every exit, cancellation included, must reach the restore below
        catch (Exception exception)
#pragma warning restore CA1031
        {
            thrown = exception;
        }
        finally
        {
            // Outside every cancellation check: no token is consulted, and this never goes
            // through the progress lane (whose steps honour a cancel request).
            run.Restore = run.Armed && run.PresentBefore is not null
                ? RestorePresentUnits(api, run.PresentBefore)
                : null;
        }

        if (run.Restore is { Verified: false } unverified)
        {
            // ONE detail object: the restore result plus the earlier failure (token and fields),
            // so the first-delimiter parser reads a single JSON value (AV-L4 item 2).
            object? prior = thrown is not null
                ? new { message = $"The operation had stopped on: {thrown.Message}" }
                : body is { Success: false } ? SplitPrior(body.Error) : null;
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.UnitsRestoreUnverified,
                "The present units could not be verified as restored; the ETABS instance must not be reused.",
                new { restored = unverified.Unknown ? "unknown" : (object)false, prior }));
        }

        if (thrown is not null)
        {
            if (thrown is OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(thrown).Throw();
            }

            return Result.Fail<AnalyzeVersionData>($"ETABS COM error: {thrown.Message}");
        }

        if (body is { Success: true, Data: not null } && run.Restore is { Verified: true } restored)
        {
            // Terminal success is built only now, after the verified restore (M3 step 10).
            return Result.Ok(body.Data with
            {
                Units = body.Data.Units with { Restored = true, PresentAfter = restored.ReadBack! }
            });
        }

        return body!;
    }

    private async Task<Result<AnalyzeVersionData>> BodyAsync(IAnalyzeVersionApi api, RunState run)
    {
        var valid = run.Valid;

        // 1. The open document is the expected one. No OpenFile, ever (card I-2).
        var documentRefusal = await Step(run, 1, "cSapModel.GetModelFilename",
            () => Task.FromResult(CheckDocument(api, valid)));
        if (documentRefusal is not null)
        {
            return Result.Fail<AnalyzeVersionData>(documentRefusal);
        }

        // 2. The model's own units.
        var databaseRead = await Step(run, 2, "cSapModel.GetDatabaseUnits_2",
            () => Task.FromResult(CheckedUnits.Read(api.GetDatabaseUnits, "cSapModel.GetDatabaseUnits_2")));
        if (!databaseRead.Success || databaseRead.Data is null)
        {
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.UnitsReadFailed, databaseRead.Error ?? "The database units could not be read."));
        }

        var database = databaseRead.Data;

        // 3. database vs the text's units, BEFORE any analysis: a disagreement costs
        // seconds, not an hour, and set/restore can never repair it.
        if (!database.SameForceAndLength(valid.ExpectedUnits))
        {
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.ModelUnitsDisagree,
                "The model's ETABS database units differ from the version text's units; nothing was analysed.",
                new { database, text = valid.ExpectedUnits }));
        }

        // 4. The analysis (unchanged runner; it never saves).
        Result<RunAnalysisData> analysis;
        try
        {
            analysis = await Step(run, 3, "cSapModel.Analyze.RunAnalysis", () => api.RunAnalysisAsync(
                valid.FilePath, valid.Request.Cases, ToUnitInfo(database)));
        }
#pragma warning disable CA1031 // a COM fault is an analysis failure; cancellation is rethrown
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.Failed,
                $"The analysis threw: {exception.Message}",
                new { caseCount = 0L, finishedCaseCount = 0L }));
        }

        if (!analysis.Success || analysis.Data is null)
        {
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.Failed,
                analysis.Error ?? "Analysis failed",
                new
                {
                    caseCount = analysis.Data?.CaseCount ?? 0L,
                    finishedCaseCount = analysis.Data?.FinishedCaseCount ?? 0L
                }));
        }

        // 5-6. Present units: read, compare, (arm, set, read back), and the tables read.
        var gate = await Step(run, 4, "cSapModel.GetPresentUnits_2", () => Task.FromResult(UnitsGate(api, run, database)));
        if (gate.Failure is not null)
        {
            return Result.Fail<AnalyzeVersionData>(gate.Failure);
        }

        var tablesUnits = gate.Tables!;

        // 7. Extraction, metadata, metrics.
        var extractionSw = Stopwatch.StartNew();
        Dictionary<string, TableExtractionOutcome> outcomes;
        try
        {
            outcomes = await Step(run, 5, "cDatabaseTables.GetTableForDisplayArray", async () =>
            {
                var state = api.ReadModelState();
                return await run.Metrics.MeasureAsync(
                    "extractTables",
                    () => api.ExtractTablesAsync(run.Tables, valid.OutputDir, state.IsAnalyzed, state.IsLocked));
            });
        }
#pragma warning disable CA1031 // a COM fault is an extraction failure; cancellation is rethrown
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.ExtractFailed, $"Table extraction failed: {exception.Message}"));
        }

        extractionSw.Stop();

        var snapshot = new UnitSnapshot
        {
            Original = ToUnitInfo(run.PresentBefore!),
            Active = ToUnitInfo(tablesUnits),
            WasChanged = run.Armed
        };

        ModelMetadata? metadata = null;
        string? metadataPath = null;
        try
        {
            metadata = await Step(run, 6, "cDatabaseTables.GetAllTables", () => run.Metrics.MeasureAsync(
                "collectMetadata", () => api.CollectMetadataAsync(valid.FilePath, snapshot)));
            metadataPath = string.IsNullOrWhiteSpace(valid.Request.MetadataOutputPath)
                ? Path.Combine(valid.OutputDir, "model-metadata.json")
                : valid.Request.MetadataOutputPath;
            var metadataJson = JsonSerializer.Serialize(metadata, AnalyzeAndExtract.AnalyzeAndExtractService.MetadataJsonOptions);
            await Step(run, 7, "write-operation-artifacts", async () =>
            {
                EnsureDirectoryOf(metadataPath);
                await run.Metrics.MeasureAsync("writeMetadata", () => File.WriteAllTextAsync(metadataPath, metadataJson));
                return true;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // legacy parity: a metadata failure never fails the run
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"⚠ Metadata collection failed: {exception.Message}");
            metadata = null;
            metadataPath = null;
        }

        // 7b. The units the tables were produced in are checked once more, AFTER the reads: a
        // change during extraction would otherwise leave tables in the wrong units unnoticed.
        var afterRead = CheckedUnits.Read(api.GetPresentUnits, "cSapModel.GetPresentUnits_2 (after extraction)");
        if (!afterRead.Success || afterRead.Data is null)
        {
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.UnitsReadFailed,
                afterRead.Error ?? "The present units could not be read after extraction."));
        }

        var presentAfterExtraction = afterRead.Data;
        if (!presentAfterExtraction.SameForceAndLength(tablesUnits))
        {
            return Result.Fail<AnalyzeVersionData>(Fail(
                AnalyzeVersionCodes.UnitsMismatch,
                "ETABS's present units changed while the tables were being extracted; the tables cannot be trusted.",
                new { tables = tablesUnits, present = presentAfterExtraction }));
        }

        run.TotalStopwatch.Stop();
        var metrics = run.Metrics.Build(run.TotalStopwatch.ElapsedMilliseconds);
        var metricsPath = string.IsNullOrWhiteSpace(valid.Request.MetricsOutputPath)
            ? Path.Combine(valid.OutputDir, "run-metrics.json")
            : valid.Request.MetricsOutputPath;
        EnsureDirectoryOf(metricsPath);
        await File.WriteAllTextAsync(
            metricsPath,
            JsonSerializer.Serialize(metrics, AnalyzeAndExtract.AnalyzeAndExtractService.MetadataJsonOptions));

        var succeeded = outcomes.Values.Count(o => o.Success);
        var failed = outcomes.Values.Count(o => !o.Success);
        return Result.Ok(new AnalyzeVersionData
        {
            FilePath = valid.FilePath,
            OutputDir = valid.OutputDir,
            CasesRequested = analysis.Data.CasesRequested,
            CaseCount = analysis.Data.CaseCount,
            FinishedCaseCount = analysis.Data.FinishedCaseCount,
            AnalysisTimeMs = analysis.Data.AnalysisTimeMs,
            Tables = outcomes,
            TotalRowCount = outcomes.Values.Sum(o => o.RowCount),
            SucceededCount = succeeded,
            FailedCount = failed,
            ExtractionTimeMs = extractionSw.ElapsedMilliseconds,
            Metadata = metadata,
            MetadataPath = metadataPath,
            Metrics = metrics,
            MetricsPath = metricsPath,
            Units = new AnalyzeVersionUnits
            {
                Database = database,
                Expected = valid.ExpectedUnits,
                PresentBefore = run.PresentBefore!,
                Tables = tablesUnits,
                PresentAfter = presentAfterExtraction,
                Changed = run.Armed,
                Restored = null,
                Policy = valid.SetAndRestore
                    ? AnalyzeVersionRequest.PolicySetAndRestore
                    : AnalyzeVersionRequest.PolicyCheckOnly
            },
            TotalElapsedMs = run.TotalStopwatch.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// Steps 5-6. Reads <c>presentBefore</c>, compares it with the database units, and either
    /// leaves the units alone, refuses (<c>checkOnly</c>), or arms the restore obligation and
    /// only THEN calls the setter (AV-R2). Returns the <c>tables</c> read taken immediately
    /// before extraction.
    /// </summary>
    private static UnitsGateResult UnitsGate(IAnalyzeVersionApi api, RunState run, UnitBlock database)
    {
        var beforeRead = CheckedUnits.Read(api.GetPresentUnits, "cSapModel.GetPresentUnits_2");
        if (!beforeRead.Success || beforeRead.Data is null)
        {
            return UnitsGateResult.Fail(Fail(
                AnalyzeVersionCodes.UnitsReadFailed, beforeRead.Error ?? "The present units could not be read."));
        }

        var before = beforeRead.Data;
        run.PresentBefore = before;

        if (before.SameForceAndLength(database))
        {
            return Fresh(api, database);
        }

        if (!run.Valid.SetAndRestore)
        {
            return UnitsGateResult.Fail(Fail(
                AnalyzeVersionCodes.UnitsMismatch,
                "ETABS presents different units from the model's own and the policy is checkOnly; nothing was extracted.",
                new { database, present = before }));
        }

        // The obligation exists from HERE: a setter that changes the units and then throws or
        // fails to confirm still leaves ETABS in a state only the restore can repair.
        run.Armed = true;
        try
        {
            var code = api.SetPresentUnits(database.RawForce, database.RawLength, database.RawTemperature!.Value);
            if (code != 0)
            {
                return UnitsGateResult.Fail(Fail(
                    AnalyzeVersionCodes.UnitsSetFailed, $"cSapModel.SetPresentUnits_2 returned {code}."));
            }
        }
#pragma warning disable CA1031 // a COM fault in the setter is a failed set; the armed restore follows
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return UnitsGateResult.Fail(Fail(
                AnalyzeVersionCodes.UnitsSetFailed, $"cSapModel.SetPresentUnits_2 threw: {exception.Message}"));
        }

        var readBack = CheckedUnits.Read(api.GetPresentUnits, "cSapModel.GetPresentUnits_2 (read-back)");
        if (!readBack.Success || readBack.Data is null)
        {
            return UnitsGateResult.Fail(Fail(
                AnalyzeVersionCodes.UnitsSetFailed,
                $"The set could not be read back: {readBack.Error}"));
        }

        if (!readBack.Data.SameAll(database))
        {
            return UnitsGateResult.Fail(Fail(
                AnalyzeVersionCodes.UnitsSetFailed,
                "The read-back after the set differs from the database units.",
                new { database, present = readBack.Data }));
        }

        return UnitsGateResult.Ok(readBack.Data);
    }

    // No set was needed: the tables read is a FRESH read, never a cached one.
    private static UnitsGateResult Fresh(IAnalyzeVersionApi api, UnitBlock database)
    {
        var read = CheckedUnits.Read(api.GetPresentUnits, "cSapModel.GetPresentUnits_2");
        if (!read.Success || read.Data is null)
        {
            return UnitsGateResult.Fail(Fail(
                AnalyzeVersionCodes.UnitsReadFailed, read.Error ?? "The present units could not be read."));
        }

        return read.Data.SameForceAndLength(database)
            ? UnitsGateResult.Ok(read.Data)
            : UnitsGateResult.Fail(Fail(
                AnalyzeVersionCodes.UnitsMismatch,
                "ETABS presents different units from the model's own immediately before extraction.",
                new { database, present = read.Data }));
    }

    /// <summary>
    /// Sets <paramref name="target"/> (the present units found before the analysis) and
    /// verifies it by a fresh read of force, length AND temperature. Verified only when the
    /// setter returned 0 AND the read-back equals the target. A throw, or a read-back that
    /// cannot be read, is "unknown"; a difference (temperature alone counts) is "false".
    /// </summary>
    private static RestoreOutcome RestorePresentUnits(IAnalyzeVersionApi api, UnitBlock target)
    {
        var setterThrew = false;
        var setterCode = 0;
        try
        {
            setterCode = api.SetPresentUnits(target.RawForce, target.RawLength, target.RawTemperature!.Value);
        }
#pragma warning disable CA1031 // the restore must always get to its verification
        catch (Exception)
#pragma warning restore CA1031
        {
            setterThrew = true;
        }

        var read = CheckedUnits.Read(api.GetPresentUnits, "cSapModel.GetPresentUnits_2 (restore read-back)");
        if (!read.Success || read.Data is null)
        {
            return RestoreOutcome.NotVerified(unknown: true, null);
        }

        if (!read.Data.SameAll(target))
        {
            return RestoreOutcome.NotVerified(unknown: false, read.Data);
        }

        if (setterThrew)
        {
            return RestoreOutcome.NotVerified(unknown: true, read.Data);
        }

        return setterCode == 0
            ? RestoreOutcome.Ok(read.Data)
            : RestoreOutcome.NotVerified(unknown: false, read.Data);
    }

    private string? CheckDocument(IAnalyzeVersionApi api, ValidAnalyzeVersionRequest valid)
    {
        string expectedFull;
        try
        {
            expectedFull = Path.GetFullPath(valid.ExpectedPath);
        }
#pragma warning disable CA1031 // an unusable path is a document mismatch
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Fail(AnalyzeVersionCodes.DocumentMismatch, $"The expected document path is unusable: {exception.Message}");
        }

        string? reported;
        try
        {
            reported = api.GetModelFilename();
        }
#pragma warning disable CA1031 // an unreadable document is a document mismatch
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Fail(AnalyzeVersionCodes.DocumentMismatch, $"The open document could not be read: {exception.Message}");
        }

        if (SaveModelService.ValidateOpenModelIs(reported, expectedFull, _compareIdentity) is not null)
        {
            return Fail(
                AnalyzeVersionCodes.DocumentMismatch,
                $"ETABS has '{EtabsCurrentModelPath.Describe(reported)}' open, not '{expectedFull}'; nothing was analysed.");
        }

        var found = _stamps.Read(expectedFull)?.FileId;
        if (!string.Equals(found, valid.ExpectedFileId, StringComparison.Ordinal))
        {
            return Fail(
                AnalyzeVersionCodes.DocumentMismatch,
                $"The expected document's file id differs (expected '{valid.ExpectedFileId}', found '{found ?? "none"}'); nothing was analysed.");
        }

        return null;
    }

    private static UnitInfo ToUnitInfo(UnitBlock block)
    {
        var us = block.Force is "kip" or "lb";
        return new UnitInfo
        {
            Force = block.Force,
            Length = block.Length,
            Temperature = block.Temperature ?? string.Empty,
            IsUs = us,
            IsMetric = !us,
            RawForce = block.RawForce,
            RawLength = block.RawLength,
            RawTemperature = block.RawTemperature ?? 0
        };
    }

    private static void EnsureDirectoryOf(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static Task<T> Step<T>(RunState run, int index, string csiOperation, Func<Task<T>> action) =>
        run.Progress is null ? action() : run.Progress.RunStepAsync(index, TotalSteps, csiOperation, action);

    private static string Fail(string code, string prose, object? detail = null) =>
        detail is null
            ? $"[{code}] {prose}"
            : $"[{code}] {prose} detail={JsonSerializer.Serialize(detail, DetailJson)}";

    // "[CODE] prose detail={json}" -> { code, message, detail }, detail kept as parsed JSON.
    private static object SplitPrior(string? error)
    {
        var text = error ?? string.Empty;
        string? code = null;
        if (text.StartsWith('[') && text.IndexOf(']') is var close and > 0)
        {
            code = text[1..close];
            text = text[(close + 1)..].TrimStart();
        }

        JsonElement? detail = null;
        var at = text.IndexOf(" detail=", StringComparison.Ordinal);
        if (at >= 0)
        {
            try
            {
                detail = JsonDocument.Parse(text[(at + " detail=".Length)..]).RootElement.Clone();
                text = text[..at];
            }
            catch (JsonException)
            {
                // not a detail object after all: keep the whole text as the message
            }
        }

        return new { code, message = text, detail };
    }

    private const int TotalSteps = 7;

    private sealed class RunState(
        ValidAnalyzeVersionRequest valid,
        TableSelections tables,
        RunMetricsBuilder metrics,
        IEtabsOperationProgress? progress)
    {
        public ValidAnalyzeVersionRequest Valid { get; } = valid;
        public TableSelections Tables { get; } = tables;
        public RunMetricsBuilder Metrics { get; } = metrics;
        public IEtabsOperationProgress? Progress { get; } = progress;
        public Stopwatch TotalStopwatch { get; } = Stopwatch.StartNew();
        public UnitBlock? PresentBefore { get; set; }

        /// <summary>True from before the setter is called until the operation ends.</summary>
        public bool Armed { get; set; }

        public RestoreOutcome? Restore { get; set; }
    }

    private readonly record struct UnitsGateResult(string? Failure, UnitBlock? Tables)
    {
        public static UnitsGateResult Fail(string failure) => new(failure, null);
        public static UnitsGateResult Ok(UnitBlock tables) => new(null, tables);
    }

    private sealed record RestoreOutcome(bool Verified, bool Unknown, UnitBlock? ReadBack)
    {
        public static RestoreOutcome Ok(UnitBlock readBack) => new(true, false, readBack);
        public static RestoreOutcome NotVerified(bool unknown, UnitBlock? readBack) => new(false, unknown, readBack);
    }
}
