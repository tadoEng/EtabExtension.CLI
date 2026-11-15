using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeVersion;
using EtabExtension.CLI.Features.AnalyzeVersion.Models;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.RunAnalysis;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The daemon's <c>analyze-version</c> body over a fake <see cref="IAnalyzeVersionApi"/> that
/// records every call in order (analyze-version card C-1..C-13, AV-R2, AV-R6). Every test
/// asserts how many items it examined and names the mutation it catches.
/// </summary>
public sealed class AnalyzeVersionTests : IDisposable
{
    private const string Scratch = @"C:\Scratch\av\model.edb";
    private const string FileId = "file-id-1";

    // eForce: lb 1, kip 2, N 3, kN 4, kgf 5, tonf 6. eLength: in 1, ft 2, mm 4, cm 5, m 6.
    // eTemperature: F 1, C 2.
    private static readonly Units KipFt = new(2, 2, 1);
    private static readonly Units KnM = new(4, 6, 2);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-analyze-version-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- C-1

    /// <summary>
    /// Catches: reading the database units after the analysis; setting units before
    /// RunAnalysis; skipping the read-back; restoring before extraction; a missing restore
    /// verification; calling OpenFile.
    /// </summary>
    [Fact]
    public async Task The_steps_run_in_the_contract_order()
    {
        var logs = new List<(string Name, List<string> Log)>();

        // 1. setAndRestore, present equal.
        var equalSet = await RunLogged(logs, "set+equal", Policy.SetAndRestore, present: KipFt);
        Assert.True(equalSet.Success, equalSet.Error);
        Assert.Equal(
            [
                "GetModelFilename", "GetDatabaseUnits", "RunAnalysis", "GetPresentUnits",
                "GetPresentUnits", "ReadModelState", "ExtractTables", "CollectMetadata", "GetPresentUnits"
            ],
            logs[0].Log);
        Assert.False(equalSet.Data!.Units.Changed);
        Assert.Null(equalSet.Data.Units.Restored);

        // 2. setAndRestore, present different.
        var differentSet = await RunLogged(logs, "set+different", Policy.SetAndRestore, present: KnM);
        Assert.True(differentSet.Success, differentSet.Error);
        Assert.Equal(
            [
                "GetModelFilename", "GetDatabaseUnits", "RunAnalysis", "GetPresentUnits",
                "SetPresentUnits(2,2,1)", "GetPresentUnits", "ReadModelState", "ExtractTables",
                "CollectMetadata", "GetPresentUnits", "SetPresentUnits(4,6,2)", "GetPresentUnits"
            ],
            logs[1].Log);
        Assert.True(differentSet.Data!.Units.Changed);
        Assert.True(differentSet.Data.Units.Restored);
        Assert.Equal(KnM.Raw, Raw(differentSet.Data.Units.PresentAfter));

        // 3. checkOnly, present equal.
        var equalCheck = await RunLogged(logs, "check+equal", Policy.CheckOnly, present: KipFt);
        Assert.True(equalCheck.Success, equalCheck.Error);
        Assert.Equal(
            [
                "GetModelFilename", "GetDatabaseUnits", "RunAnalysis", "GetPresentUnits",
                "GetPresentUnits", "ReadModelState", "ExtractTables", "CollectMetadata", "GetPresentUnits"
            ],
            logs[2].Log);

        // 4. checkOnly, present different: refused after the present read, nothing extracted.
        var differentCheck = await RunLogged(logs, "check+different", Policy.CheckOnly, present: KnM);
        Assert.False(differentCheck.Success);
        Assert.Equal(
            ["GetModelFilename", "GetDatabaseUnits", "RunAnalysis", "GetPresentUnits"],
            logs[3].Log);

        // 5. database disagrees with the text: stops before RunAnalysis.
        var disagree = await RunLogged(logs, "disagree", Policy.SetAndRestore, present: KipFt, database: KnM);
        Assert.False(disagree.Success);
        Assert.Equal(["GetModelFilename", "GetDatabaseUnits"], logs[4].Log);

        Assert.Equal(5, logs.Count);
        Assert.Equal(0, logs.Sum(item => item.Log.Count(call => call == "OpenFile")));
    }

    // ---------------------------------------------------------------- C-2

    /// <summary>Catches: calling SetPresentUnits_2 under checkOnly (any of the 60 runs).</summary>
    [Fact]
    public async Task Check_only_never_sets_units()
    {
        var runs = 0;
        var setterCalls = 0;
        foreach (var force in CheckedUnits.ForceTokens)
        {
            foreach (var length in CheckedUnits.LengthTokens)
            {
                CheckedUnits.TryForceToRaw(force, out var rawForce);
                CheckedUnits.TryLengthToRaw(length, out var rawLength);
                var database = new Units(rawForce, rawLength, 1);
                foreach (var presentIsEqual in new[] { true, false })
                {
                    // "Different" shifts the force to another valid value.
                    var present = presentIsEqual ? database : new Units(rawForce == 6 ? 1 : rawForce + 1, rawLength, 1);
                    var api = new FakeApi { Database = database, Present = present };
                    var request = Request(Policy.CheckOnly, expected: (force, length));
                    await Service().AnalyzeOnApiAsync(api, request);
                    setterCalls += api.Log.Count(call => call.StartsWith("SetPresentUnits", StringComparison.Ordinal));
                    runs++;
                }
            }
        }

        Assert.Equal(30 * 2, runs);
        Assert.Equal(0, setterCalls);
    }

    // ---------------------------------------------------------------- C-3

    /// <summary>Catches: extracting anyway after a present/database difference under checkOnly.</summary>
    [Fact]
    public async Task A_present_difference_under_check_only_fails_and_extracts_nothing()
    {
        var api = new FakeApi { Database = KipFt, Present = KnM };
        var result = await Service().AnalyzeOnApiAsync(api, Request(Policy.CheckOnly));

        Assert.False(result.Success);
        Assert.StartsWith("[ANALYSIS_UNITS_MISMATCH]", result.Error, StringComparison.Ordinal);
        var detail = Detail(result.Error!);
        Assert.Equal(2, detail.GetProperty("database").GetProperty("rawForce").GetInt32());
        Assert.Equal(4, detail.GetProperty("present").GetProperty("rawForce").GetInt32());
        Assert.Equal(0, api.Log.Count(call => call == "ExtractTables"));
    }

    // ---------------------------------------------------------------- C-4

    /// <summary>
    /// Catches: trusting the setter's return code; skipping the read-back; not arming the
    /// restore before the setter (a setter that mutates and then fails).
    /// </summary>
    [Fact]
    public async Task Set_and_restore_reads_back_after_each_set()
    {
        var cases = new List<string>();

        // a. the setter returns nonzero WITHOUT changing anything.
        var a = new FakeApi { Present = KnM, Setter = (n, _, _, _) => n == 1 ? 5 : 0 };
        var ra = await Service().AnalyzeOnApiAsync(a, Request(Policy.SetAndRestore));
        AssertFailure(ra, "ANALYSIS_UNITS_SET_FAILED");
        Assert.Equal(0, a.Log.Count(call => call == "ExtractTables"));
        cases.Add("nonzero");

        // b. the setter MUTATES, then returns nonzero: the armed restore still runs.
        var b = new FakeApi { Present = KnM };
        b.Setter = (n, f, l, t) =>
        {
            b.Present = new Units(f, l, t);
            return n == 1 ? 5 : 0;
        };
        var rb = await Service().AnalyzeOnApiAsync(b, Request(Policy.SetAndRestore));
        AssertFailure(rb, "ANALYSIS_UNITS_SET_FAILED");
        Assert.Equal(KnM, b.Present);
        Assert.Equal(1, b.Log.Count(call => call == "SetPresentUnits(4,6,2)"));
        cases.Add("mutates-then-nonzero");

        // c. the setter returns 0 but the read-back still differs.
        var c = new FakeApi { Present = KnM, Setter = (_, _, _, _) => 0 };
        var rc = await Service().AnalyzeOnApiAsync(c, Request(Policy.SetAndRestore));
        AssertFailure(rc, "ANALYSIS_UNITS_SET_FAILED");
        Assert.Equal(0, c.Log.Count(call => call == "ExtractTables"));
        cases.Add("readback-differs");

        // d. the setter mutates and the read-back cannot be read (call 2 of the present reads).
        var d = new FakeApi { Present = KnM };
        d.PresentReader = n => n == 2 ? (1, 0, 0, 0) : (0, d.Present.Force, d.Present.Length, d.Present.Temperature);
        var rd = await Service().AnalyzeOnApiAsync(d, Request(Policy.SetAndRestore));
        AssertFailure(rd, "ANALYSIS_UNITS_SET_FAILED");
        Assert.Equal(KnM, d.Present);
        cases.Add("readback-unreadable");

        // e. the setter works and verifies.
        var e = new FakeApi { Present = KnM };
        var re = await Service().AnalyzeOnApiAsync(e, Request(Policy.SetAndRestore));
        Assert.True(re.Success, re.Error);
        Assert.Equal(1, e.Log.Count(call => call == "ExtractTables"));
        cases.Add("ok");

        Assert.Equal(5, cases.Count);
    }

    // ---------------------------------------------------------------- C-5

    /// <summary>
    /// Catches: running the restore outside a finally; checking cancellation before the
    /// restore; not arming the restore before the setter.
    /// </summary>
    [Fact]
    public async Task The_restore_runs_when_extraction_throws_or_is_cancelled()
    {
        var cases = new List<string>();

        // throw
        var thrown = new FakeApi { Present = KnM, Extract = () => throw new InvalidOperationException("boom") };
        var rThrown = await Service().AnalyzeOnApiAsync(thrown, Request(Policy.SetAndRestore));
        AssertFailure(rThrown, "ANALYSIS_EXTRACT_FAILED");
        Assert.Equal(1, thrown.Log.Count(call => call == "SetPresentUnits(4,6,2)"));
        Assert.Equal(KnM, thrown.Present);
        cases.Add("throw");

        // cancel
        var cancelled = new FakeApi { Present = KnM, Extract = () => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Service().AnalyzeOnApiAsync(cancelled, Request(Policy.SetAndRestore)));
        Assert.Equal(1, cancelled.Log.Count(call => call == "SetPresentUnits(4,6,2)"));
        Assert.Equal(KnM, cancelled.Present);
        cases.Add("cancel");

        // success
        var ok = new FakeApi { Present = KnM };
        var rOk = await Service().AnalyzeOnApiAsync(ok, Request(Policy.SetAndRestore));
        Assert.True(rOk.Success, rOk.Error);
        Assert.Equal(1, ok.Log.Count(call => call == "SetPresentUnits(4,6,2)"));
        cases.Add("success");

        // the setter mutates and then throws (AV-R2: the obligation was armed first)
        var setterThrows = new FakeApi { Present = KnM };
        setterThrows.Setter = (n, f, l, t) =>
        {
            if (n == 1)
            {
                setterThrows.Present = new Units(f, l, t);
                throw new InvalidOperationException("com fault");
            }

            setterThrows.Present = new Units(f, l, t);
            return 0;
        };
        var rSetter = await Service().AnalyzeOnApiAsync(setterThrows, Request(Policy.SetAndRestore));
        AssertFailure(rSetter, "ANALYSIS_UNITS_SET_FAILED");
        Assert.Equal(KnM, setterThrows.Present);
        Assert.Equal(1, setterThrows.Log.Count(call => call == "SetPresentUnits(4,6,2)"));
        cases.Add("setter-mutates-then-throws");

        Assert.Equal(4, cases.Count);
    }

    // ---------------------------------------------------------------- C-6

    /// <summary>
    /// Catches: reporting success after an unverified restore; verifying force and length
    /// only (the temperature-only case); collapsing "unknown" into false.
    /// </summary>
    [Fact]
    public async Task An_unverified_restore_is_a_failure_with_detail()
    {
        var cases = new List<string>();

        // restored:false - the restore read-back still shows the database units.
        var differs = new FakeApi { Present = KnM };
        differs.Setter = (n, f, l, t) =>
        {
            if (n == 1)
            {
                differs.Present = new Units(f, l, t);
            }

            return 0; // the restore set is accepted but changes nothing
        };
        var rDiffers = await Service().AnalyzeOnApiAsync(differs, Request(Policy.SetAndRestore));
        AssertFailure(rDiffers, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
        Assert.False(Detail(rDiffers.Error!).GetProperty("restored").GetBoolean());
        cases.Add("differs");

        // restored:"unknown" - the restore read-back cannot be read (the fourth present read).
        var unknown = new FakeApi { Present = KnM };
        unknown.PresentReader = n => n == 4 ? (1, 0, 0, 0) : (0, unknown.Present.Force, unknown.Present.Length, unknown.Present.Temperature);
        var rUnknown = await Service().AnalyzeOnApiAsync(unknown, Request(Policy.SetAndRestore));
        AssertFailure(rUnknown, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
        Assert.Equal("unknown", Detail(rUnknown.Error!).GetProperty("restored").GetString());
        cases.Add("unknown");

        // temperature alone differs after the restore: still restored:false (AV-R2).
        var temperature = new FakeApi { Present = KnM };
        temperature.Setter = (n, f, l, t) =>
        {
            // The restore (call 2) lands on the right force and length but the wrong temperature.
            temperature.Present = n == 1 ? new Units(f, l, t) : new Units(f, l, t == 2 ? 1 : 2);
            return 0;
        };
        var rTemperature = await Service().AnalyzeOnApiAsync(temperature, Request(Policy.SetAndRestore));
        AssertFailure(rTemperature, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
        Assert.False(Detail(rTemperature.Error!).GetProperty("restored").GetBoolean());
        cases.Add("temperature-only");

        Assert.Equal(3, cases.Count);
    }

    // ---------------------------------------------------------------- C-7

    /// <summary>Catches: calling OpenFile; accepting a different document or file id.</summary>
    [Fact]
    public async Task The_document_must_be_the_expected_one_and_no_file_is_opened()
    {
        var logs = new List<List<string>>();

        var otherPath = new FakeApi { ModelFile = @"C:\Other\elsewhere.edb" };
        var rPath = await Service().AnalyzeOnApiAsync(otherPath, Request(Policy.SetAndRestore));
        AssertFailure(rPath, "ANALYSIS_DOCUMENT_MISMATCH");
        logs.Add(otherPath.Log);

        var otherId = new FakeApi();
        var rId = await Service(fileId: "someone-else").AnalyzeOnApiAsync(otherId, Request(Policy.SetAndRestore));
        AssertFailure(rId, "ANALYSIS_DOCUMENT_MISMATCH");
        logs.Add(otherId.Log);

        Assert.Equal(2, logs.Count);
        Assert.All(logs, log => Assert.DoesNotContain("OpenFile", log));
        Assert.All(logs, log => Assert.DoesNotContain("GetDatabaseUnits", log));
        Assert.All(logs, log => Assert.DoesNotContain("RunAnalysis", log));
    }

    // ---------------------------------------------------------------- C-11

    /// <summary>Catches: comparing after the analysis; comparing force only; comparing length only.</summary>
    [Fact]
    public async Task A_database_text_disagreement_fails_before_any_analysis()
    {
        var cases = new (string Name, Units Database)[]
        {
            ("force", new Units(4, 2, 1)),
            ("length", new Units(2, 6, 1)),
            ("both", new Units(4, 6, 2))
        };

        foreach (var (name, database) in cases)
        {
            var api = new FakeApi { Database = database, Present = database };
            var result = await Service().AnalyzeOnApiAsync(api, Request(Policy.SetAndRestore));
            AssertFailure(result, "ANALYSIS_MODEL_UNITS_DISAGREE");
            var detail = Detail(result.Error!);
            Assert.Equal(database.Force, detail.GetProperty("database").GetProperty("rawForce").GetInt32());
            Assert.Equal(2, detail.GetProperty("text").GetProperty("rawForce").GetInt32());
            Assert.Equal(0, api.Log.Count(call => call == "RunAnalysis"));
            Assert.True(name.Length > 0);
        }

        Assert.Equal(3, cases.Length);
    }

    // ---------------------------------------------------------------- C-13

    /// <summary>Catches: accepting NotApplicable, micron or an out-of-range enum value at either read.</summary>
    [Fact]
    public async Task A_zero_or_unknown_enum_value_is_an_unread_answer()
    {
        // force {0, 99}, length {0, 99, micron=3}, temperature {0, 99} = 7 bad answers per read.
        var bad = new (string Name, int Force, int Length, int Temperature)[]
        {
            ("force0", 0, 2, 1), ("force99", 99, 2, 1),
            ("length0", 2, 0, 1), ("length99", 2, 99, 1), ("micron", 2, 3, 1),
            ("temperature0", 2, 2, 0), ("temperature99", 2, 2, 99)
        };
        var examined = 0;

        foreach (var (_, force, length, temperature) in bad)
        {
            // The database read.
            var first = new FakeApi { DatabaseReader = _ => (0, force, length, temperature) };
            var rFirst = await Service().AnalyzeOnApiAsync(first, Request(Policy.SetAndRestore));
            AssertFailure(rFirst, "ANALYSIS_UNITS_READ_FAILED");
            Assert.DoesNotContain("RunAnalysis", first.Log);
            examined++;

            // The present read before extraction.
            var second = new FakeApi { PresentReader = _ => (0, force, length, temperature) };
            var rSecond = await Service().AnalyzeOnApiAsync(second, Request(Policy.SetAndRestore));
            AssertFailure(rSecond, "ANALYSIS_UNITS_READ_FAILED");
            Assert.DoesNotContain("ExtractTables", second.Log);
            Assert.DoesNotContain(second.Log, call => call.StartsWith("SetPresentUnits", StringComparison.Ordinal));
            examined++;
        }

        Assert.Equal(14, examined);
    }

    // ------------------------------------------------- supporting behaviour

    /// <summary>Catches: setting units before the analysis returned (I-1); a missing token for a failed analysis.</summary>
    [Fact]
    public async Task A_failed_analysis_carries_case_counts_and_never_touches_units()
    {
        var api = new FakeApi
        {
            Present = KnM,
            Analysis = () => Result.Fail<RunAnalysisData>("Analysis failed (ret=1)") with
            {
                Data = new RunAnalysisData { CaseCount = 7, FinishedCaseCount = 2 }
            }
        };
        var result = await Service().AnalyzeOnApiAsync(api, Request(Policy.SetAndRestore));

        AssertFailure(result, "ANALYSIS_FAILED");
        var detail = Detail(result.Error!);
        Assert.Equal(7, detail.GetProperty("caseCount").GetInt64());
        Assert.Equal(2, detail.GetProperty("finishedCaseCount").GetInt64());
        Assert.Equal(0, api.Log.Count(call => call.StartsWith("SetPresentUnits", StringComparison.Ordinal)));
        Assert.Equal(0, api.Log.Count(call => call == "GetPresentUnits"));
    }

    /// <summary>Catches: a success block that omits restored (null) or any of its eight members.</summary>
    [Fact]
    public async Task The_success_units_block_has_the_agreed_shape()
    {
        var api = new FakeApi { Present = KipFt };
        var result = await Service().AnalyzeOnApiAsync(api, Request(Policy.CheckOnly));
        Assert.True(result.Success, result.Error);

        var json = JsonSerializer.SerializeToElement(result.Data!.Units, JsonOptions);
        var members = json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["changed", "database", "expected", "policy", "presentAfter", "presentBefore", "restored", "tables"],
            members);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("restored").ValueKind);
        Assert.Equal("checkOnly", json.GetProperty("policy").GetString());
        Assert.Equal(6, json.GetProperty("database").EnumerateObject().Count());
    }

    /// <summary>Catches: a request with ten members drifting (S-1 in the host mirrors these names).</summary>
    [Fact]
    public void The_request_has_the_ten_agreed_top_level_names()
    {
        var names = typeof(AnalyzeVersionRequest).GetProperties()
            .Select(property => ((System.Text.Json.Serialization.JsonPropertyNameAttribute)Attribute.GetCustomAttribute(
                property, typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute))!).Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "cases", "expectedDocument", "expectedUnits", "extractionProfile", "filePath",
                "metadataOutputPath", "metricsOutputPath", "outputDir", "tables", "unitPolicy"
            ],
            names);
        Assert.Equal(10, names.Length);
        Assert.Equal(9, AnalyzeVersionCodes.All.Distinct(StringComparer.Ordinal).Count());
    }

    // ------------------------------------------------------------ helpers

    // ---------------------------------------------------------------- AV-L4

    /// <summary>
    /// Catches: ConfigureAwait(false) on an await that precedes COM work or the restore, which
    /// lets the unit set, the restore and the read-back run off the daemon's owning thread.
    /// Every call below is examined (12 for set-and-restore with a different present).
    /// </summary>
    [Fact]
    public async Task Every_com_call_including_the_restore_runs_on_the_owning_thread()
    {
        using var worker = new SingleThreadWorker();
        var api = new FakeApi { Present = KnM, Yield = true };

        var result = await worker.RunAsync(() => Service().AnalyzeOnApiAsync(api, Request(Policy.SetAndRestore)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(12, api.Threads.Count);
        Assert.Equal("SetPresentUnits(4,6,2)", api.Log[^2]);
        Assert.Equal("GetPresentUnits", api.Log[^1]);
        Assert.All(api.Threads, id => Assert.Equal(worker.ThreadId, id));
    }

    /// <summary>
    /// Catches: two detail objects (the earlier error text embedded before the restore detail),
    /// which the first-delimiter parser cannot read; losing the prior failure's token or fields.
    /// </summary>
    [Fact]
    public async Task A_restore_failure_after_an_earlier_failure_has_one_detail_object()
    {
        var cases = 0;

        // Set read-back mismatch, then the restore fails: the prior token and fields survive.
        var api = new FakeApi { Present = KnM };
        api.Setter = (n, f, l, t) =>
        {
            // call 1 lands on the wrong temperature; the restore (call 2) changes nothing
            api.Present = n == 1 ? new Units(f, l, t == 1 ? 2 : 1) : api.Present;
            return 0;
        };
        var result = await Service().AnalyzeOnApiAsync(api, Request(Policy.SetAndRestore));
        AssertFailure(result, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
        Assert.Equal(1, Occurrences(result.Error!, " detail="));
        var detail = Detail(result.Error!);
        Assert.False(detail.GetProperty("restored").GetBoolean());
        var prior = detail.GetProperty("prior");
        Assert.Equal("ANALYSIS_UNITS_SET_FAILED", prior.GetProperty("code").GetString());
        Assert.True(prior.GetProperty("detail").TryGetProperty("database", out _));
        Assert.True(prior.GetProperty("detail").TryGetProperty("present", out _));
        cases++;

        // Cancellation, then the restore fails: the prior message survives, still one detail.
        var cancelled = new FakeApi { Present = KnM, Extract = () => throw new OperationCanceledException() };
        cancelled.Setter = (n, f, l, t) =>
        {
            cancelled.Present = n == 1 ? new Units(f, l, t) : cancelled.Present;
            return 0;
        };
        var rCancelled = await Service().AnalyzeOnApiAsync(cancelled, Request(Policy.SetAndRestore));
        AssertFailure(rCancelled, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
        Assert.Equal(1, Occurrences(rCancelled.Error!, " detail="));
        Assert.Contains("stopped on", Detail(rCancelled.Error!).GetProperty("prior").GetProperty("message").GetString());
        cases++;

        Assert.Equal(2, cases);
    }

    // ---------------------------------------------------------------- AV-L7

    /// <summary>
    /// Catches (SF2): trusting the cached pre-extraction read; reporting presentAfter from the
    /// cached tables read; accepting a unit change (force or length) during extraction; not
    /// restoring after that failure. Four cases examined.
    /// </summary>
    [Fact]
    public async Task Units_that_change_during_extraction_fail_the_run_and_presentAfter_is_a_fresh_read()
    {
        var cases = 0;

        // 1. checkOnly: the present units change inside ExtractTables -> mismatch, nothing set.
        var check = new FakeApi { Present = KipFt };
        check.Extract = () =>
        {
            check.Present = KnM;
            return OneTable();
        };
        var rCheck = await Service().AnalyzeOnApiAsync(check, Request(Policy.CheckOnly));
        AssertFailure(rCheck, "ANALYSIS_UNITS_MISMATCH");
        var detail = Detail(rCheck.Error!);
        Assert.Equal(2, detail.GetProperty("tables").GetProperty("rawForce").GetInt32());
        Assert.Equal(4, detail.GetProperty("present").GetProperty("rawForce").GetInt32());
        Assert.Equal(0, check.Log.Count(call => call.StartsWith("SetPresentUnits", StringComparison.Ordinal)));
        cases++;

        // 2. setAndRestore: the units change again during extraction -> mismatch AND the
        // restore still puts the original units back.
        var set = new FakeApi { Present = KnM };
        set.Extract = () =>
        {
            set.Present = new Units(1, 1, 1);
            return OneTable();
        };
        var rSet = await Service().AnalyzeOnApiAsync(set, Request(Policy.SetAndRestore));
        AssertFailure(rSet, "ANALYSIS_UNITS_MISMATCH");
        Assert.Equal(KnM, set.Present);
        Assert.Equal(1, set.Log.Count(call => call == "SetPresentUnits(4,6,2)"));
        cases++;

        // 3. only the length changes during extraction (force alone must not be the check).
        var length = new FakeApi { Present = KipFt };
        length.Extract = () =>
        {
            length.Present = new Units(2, 6, 1);
            return OneTable();
        };
        AssertFailure(await Service().AnalyzeOnApiAsync(length, Request(Policy.CheckOnly)), "ANALYSIS_UNITS_MISMATCH");
        cases++;

        // 4. nothing set, a harmless temperature-only change during extraction: success, and
        // presentAfter is that FRESH read (temperature C), not the cached tables read (F).
        var fresh = new FakeApi { Present = KipFt };
        fresh.Extract = () =>
        {
            fresh.Present = new Units(2, 2, 2);
            return OneTable();
        };
        var rFresh = await Service().AnalyzeOnApiAsync(fresh, Request(Policy.CheckOnly));
        Assert.True(rFresh.Success, rFresh.Error);
        Assert.Equal((2, 2, 1), Raw(rFresh.Data!.Units.Tables));
        Assert.Equal((2, 2, 2), Raw(rFresh.Data.Units.PresentAfter));
        Assert.Null(rFresh.Data.Units.Restored);
        cases++;

        Assert.Equal(4, cases);
    }

    /// <summary>Catches (SF2): an unreadable or out-of-range post-extraction read being accepted. Eight bad answers.</summary>
    [Fact]
    public async Task A_bad_read_after_extraction_is_an_unread_answer()
    {
        var bad = new (int Force, int Length, int Temperature)[]
        {
            (0, 2, 1), (99, 2, 1), (2, 0, 1), (2, 99, 1), (2, 3, 1), (2, 2, 0), (2, 2, 99)
        };
        var examined = 0;
        foreach (var (force, length, temperature) in bad)
        {
            var api = new FakeApi { Present = KipFt };
            api.PresentReader = n => n == 3
                ? (0, force, length, temperature)
                : (0, api.Present.Force, api.Present.Length, api.Present.Temperature);
            AssertFailure(await Service().AnalyzeOnApiAsync(api, Request(Policy.CheckOnly)), "ANALYSIS_UNITS_READ_FAILED");
            examined++;
        }

        var unreadable = new FakeApi { Present = KipFt };
        unreadable.PresentReader = n => n == 3
            ? (1, 0, 0, 0)
            : (0, unreadable.Present.Force, unreadable.Present.Length, unreadable.Present.Temperature);
        AssertFailure(await Service().AnalyzeOnApiAsync(unreadable, Request(Policy.CheckOnly)), "ANALYSIS_UNITS_READ_FAILED");
        examined++;

        Assert.Equal(8, examined);
    }

    /// <summary>
    /// Catches (SF4): the two restore branches nothing covered - a setter that throws after
    /// landing the right units must be "unknown"; a nonzero code after landing them is false.
    /// </summary>
    [Fact]
    public async Task A_restore_setter_that_lands_the_units_but_complains_is_not_a_verified_restore()
    {
        var cases = 0;

        // The restore setter (call 2) applies the units and THROWS: read-back equal -> "unknown".
        var throws = new FakeApi { Present = KnM };
        throws.Setter = (n, f, l, t) =>
        {
            throws.Present = new Units(f, l, t);
            return n == 2 ? throw new InvalidOperationException("com fault") : 0;
        };
        var rThrows = await Service().AnalyzeOnApiAsync(throws, Request(Policy.SetAndRestore));
        AssertFailure(rThrows, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
        Assert.Equal("unknown", Detail(rThrows.Error!).GetProperty("restored").GetString());
        Assert.Equal(KnM, throws.Present);
        cases++;

        // The restore setter applies the units and returns nonzero: read-back equal -> false.
        var nonzero = new FakeApi { Present = KnM };
        nonzero.Setter = (n, f, l, t) =>
        {
            nonzero.Present = new Units(f, l, t);
            return n == 2 ? 5 : 0;
        };
        var rNonzero = await Service().AnalyzeOnApiAsync(nonzero, Request(Policy.SetAndRestore));
        AssertFailure(rNonzero, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
        Assert.False(Detail(rNonzero.Error!).GetProperty("restored").GetBoolean());
        Assert.Equal(KnM, nonzero.Present);
        cases++;

        Assert.Equal(2, cases);
    }

    /// <summary>
    /// Catches (SF4, C-13): a zero, unknown or micron value accepted on the SET read-back or
    /// on the RESTORE read-back. Seven bad answers at each of the two reads.
    /// </summary>
    [Fact]
    public async Task A_bad_enum_value_on_the_set_or_restore_read_back_is_an_unread_answer()
    {
        var bad = new (int Force, int Length, int Temperature)[]
        {
            (0, 4, 2), (99, 4, 2), (4, 0, 2), (4, 99, 2), (4, 3, 2), (4, 6, 0), (4, 6, 99)
        };
        var examined = 0;

        foreach (var (force, length, temperature) in bad)
        {
            // The set read-back is the second present read.
            var set = new FakeApi { Present = KnM };
            set.PresentReader = n => n == 2
                ? (0, force, length, temperature)
                : (0, set.Present.Force, set.Present.Length, set.Present.Temperature);
            AssertFailure(await Service().AnalyzeOnApiAsync(set, Request(Policy.SetAndRestore)), "ANALYSIS_UNITS_SET_FAILED");
            Assert.DoesNotContain("ExtractTables", set.Log);
            Assert.Equal(KnM, set.Present);
            examined++;

            // The restore read-back is the fourth present read (before, set read-back, after
            // extraction, restore read-back): an unread answer is "unknown".
            var restore = new FakeApi { Present = KnM };
            restore.PresentReader = n => n == 4
                ? (0, force, length, temperature)
                : (0, restore.Present.Force, restore.Present.Length, restore.Present.Temperature);
            var rRestore = await Service().AnalyzeOnApiAsync(restore, Request(Policy.SetAndRestore));
            AssertFailure(rRestore, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
            Assert.Equal("unknown", Detail(rRestore.Error!).GetProperty("restored").GetString());
            examined++;
        }

        Assert.Equal(14, examined);
    }

    /// <summary>
    /// Catches (SF4): a restore that runs off the daemon's owning thread when extraction
    /// THROWS or the operation is CANCELLED (the success path is covered above). Both paths
    /// make ten recorded calls, restore included.
    /// </summary>
    [Fact]
    public async Task The_restore_stays_on_the_owning_thread_when_extraction_throws_or_is_cancelled()
    {
        var paths = 0;
        using var worker = new SingleThreadWorker();

        var thrown = new FakeApi { Present = KnM, Yield = true, Extract = () => throw new InvalidOperationException("boom") };
        var rThrown = await worker.RunAsync(() => Service().AnalyzeOnApiAsync(thrown, Request(Policy.SetAndRestore)));
        AssertFailure(rThrown, "ANALYSIS_EXTRACT_FAILED");
        Assert.Equal(10, thrown.Threads.Count);
        Assert.Equal("SetPresentUnits(4,6,2)", thrown.Log[^2]);
        Assert.All(thrown.Threads, id => Assert.Equal(worker.ThreadId, id));
        Assert.Equal(KnM, thrown.Present);
        paths++;

        var cancelled = new FakeApi { Present = KnM, Yield = true, Extract = () => throw new OperationCanceledException() };
        var rCancelled = await worker.RunAsync(async () =>
        {
            try
            {
                return await Service().AnalyzeOnApiAsync(cancelled, Request(Policy.SetAndRestore));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        });
        Assert.Null(rCancelled);
        Assert.Equal(10, cancelled.Threads.Count);
        Assert.Equal("SetPresentUnits(4,6,2)", cancelled.Log[^2]);
        Assert.All(cancelled.Threads, id => Assert.Equal(worker.ThreadId, id));
        Assert.Equal(KnM, cancelled.Present);
        paths++;

        Assert.Equal(2, paths);
    }

    /// <summary>
    /// Catches (SF4): a cancel through the real OperationManager and the real service that
    /// skips the restore, or that reports Cancelled although the restore failed (that must be
    /// Failed with the unverified token). Two cases, each through the manager.
    /// </summary>
    [Fact]
    public async Task A_real_cancel_restores_then_is_cancelled_and_a_failed_restore_is_failed()
    {
        var cases = 0;

        // 1. cancel requested during extraction: restore runs and is verified; phase Cancelled.
        var ok = new FakeApi { Present = KnM };
        var (manager1, id1) = StartOverRealService(ok);
        using (manager1)
        {
            var result = await manager1.WaitAsync(id1, TestContext.Current.CancellationToken);
            Assert.False(((Result)result).Success);
            Assert.Equal(OperationPhase.Cancelled, manager1.GetStatus(id1).Data!.Phase);
            Assert.Equal(KnM, ok.Present);
            Assert.Equal(1, ok.Log.Count(call => call == "SetPresentUnits(4,6,2)"));
            cases++;
        }

        // 2. cancel requested, then the restore cannot be verified: Failed, token and detail.
        var broken = new FakeApi { Present = KnM };
        broken.Setter = (n, f, l, t) =>
        {
            broken.Present = n == 1 ? new Units(f, l, t) : broken.Present; // the restore changes nothing
            return 0;
        };
        var (manager2, id2) = StartOverRealService(broken);
        using (manager2)
        {
            var result = Assert.IsType<Result<AnalyzeVersionData>>(
                await manager2.WaitAsync(id2, TestContext.Current.CancellationToken));
            AssertFailure(result, "ANALYSIS_UNITS_RESTORE_UNVERIFIED");
            Assert.False(Detail(result.Error!).GetProperty("restored").GetBoolean());
            Assert.Equal(OperationPhase.Failed, manager2.GetStatus(id2).Data!.Phase);
            cases++;
        }

        Assert.Equal(2, cases);
    }

    /// <summary>
    /// Catches (SF4): an operation that cannot be built from the real container (a scoped
    /// dependency captured by a singleton, a missing registration). One resolution under
    /// scope validation, two operations of the right kinds.
    /// </summary>
    [Fact]
    public void The_real_container_resolves_the_operation_definitions_with_scope_validation()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([]);
        builder.Services
            .AddEtabsInfrastructure()
            .AddGetStatusFeature()
            .AddRunAnalysisFeature()
            .AddExtractResultsFeature()
            .AddAnalyzeAndExtractFeature()
            .AddServeFeature();
        using var provider = builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var definitions = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IOperationDefinition>>().ToList();

        Assert.Equal(1, definitions.Count(item => item.Kind == "analyze-version"));
        Assert.Equal(1, definitions.Count(item => item.Kind == "analyze-and-extract"));
        Assert.Equal(2, definitions.Count);
    }

    private static Dictionary<string, TableExtractionOutcome> OneTable() => new()
    {
        ["baseReactions"] = new TableExtractionOutcome { Success = true, RowCount = 3 }
    };

    // The real service over the fake seam, driven through a real OperationManager. The seam's
    // extraction asks the manager to cancel the very operation that is running it, which the
    // manager honours at the end of that step, exactly as a host's cancel-operation would.
    private (OperationManager Manager, string Id) StartOverRealService(FakeApi api)
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "model.edb");
        File.WriteAllText(file, "x");
        api.ModelFile = file;

        string? operationId = null;
        var known = new ManualResetEventSlim();
        OperationManager? manager = null;
        api.Extract = () =>
        {
            Assert.True(known.Wait(TimeSpan.FromSeconds(10)));
            manager!.Cancel(operationId!);
            return OneTable();
        };

        var session = new OperationSession();
        var definition = new AnalyzeVersionOperation(
            session, Service(), new FixedFactory(api), new OperationStatus(), new CachedSessionStatus());
        manager = new OperationManager(
            new StaExecutionWorker(),
            new OperationEventJournalFactory(_directory),
            new SystemOperationClock(),
            WorkEnvelopeFixtures.Consented(session),
            [definition]);

        var payload = JsonSerializer.SerializeToElement(new
        {
            filePath = file,
            outputDir = _directory,
            expectedDocument = new { path = file, fileId = FileId },
            expectedUnits = new { force = "kip", length = "ft", temperature = "F" },
            unitPolicy = "setAndRestore",
            extractionProfile = "full"
        });
        var started = manager.Start("analyze-version", payload);
        operationId = started.Data!.OperationId;
        known.Set();
        return (manager, operationId);
    }

    private sealed class FixedFactory(IAnalyzeVersionApi api) : IAnalyzeVersionApiFactory
    {
        public IAnalyzeVersionApi Create(EtabSharp.Core.ETABSApplication application) => api;
    }

    private sealed class OperationStatus : IGetStatusService
    {
        public Task<Result<EtabExtension.CLI.Features.GetStatus.Models.GetStatusData>> GetStatusAsync() =>
            throw new NotSupportedException();

        public Result<EtabExtension.CLI.Features.GetStatus.Models.GetStatusData> GetStatusOnApp(
            EtabSharp.Core.ETABSApplication app, int? pid = null) =>
            Result.Ok(new EtabExtension.CLI.Features.GetStatus.Models.GetStatusData { IsRunning = true, Pid = pid });
    }

    private sealed class OperationSession : EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.IEtabsSession
    {
        public Result CertifyNoUnconsentedExposure() => Result.Ok();
        public void MarkVisibilityStage(string stage) { }
        public bool IsStarted => true;
        public int? ProcessId => 42;
        public EtabSharp.Core.ETABSApplication GetOrStart() => null!;
        public EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.IManagedEtabsApplication GetOrStartOwned() =>
            throw new NotSupportedException();
        public Result RevealForExplicitUserRequest() => Result.Ok();
        public EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsShutdownResult Shutdown() =>
            throw new NotSupportedException();
        public EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked() =>
            throw new NotSupportedException();
        public void Dispose() { }
    }

    private static int Occurrences(string text, string needle) =>
        text.Split(needle, StringSplitOptions.None).Length - 1;

    private sealed class SingleThreadWorker : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = [];
        private readonly Thread _thread;

        public SingleThreadWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
            ThreadId = _thread.ManagedThreadId;
        }

        public int ThreadId { get; }

        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            var completion = new TaskCompletionSource<T>();
            _queue.Add((_ =>
            {
                try
                {
                    work().ContinueWith(
                        t =>
                        {
                            if (t.IsFaulted)
                            {
                                completion.SetException(t.Exception!.InnerExceptions);
                            }
                            else
                            {
                                completion.SetResult(t.Result);
                            }
                        },
                        TaskScheduler.Default);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }, null));
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
        }

        private void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(new Context(_queue));
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        private sealed class Context(System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> queue) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));
        }
    }

    private enum Policy { CheckOnly, SetAndRestore }

    private readonly record struct Units(int Force, int Length, int Temperature)
    {
        public (int, int, int) Raw => (Force, Length, Temperature);
    }

    private static (int, int, int) Raw(UnitBlock block) => (block.RawForce, block.RawLength, block.RawTemperature!.Value);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void AssertFailure(Result<AnalyzeVersionData> result, string token)
    {
        Assert.False(result.Success, "expected a failure");
        Assert.StartsWith($"[{token}]", result.Error, StringComparison.Ordinal);
    }

    private static JsonElement Detail(string error)
    {
        var at = error.IndexOf(" detail=", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no detail in: {error}");
        return JsonDocument.Parse(error[(at + " detail=".Length)..]).RootElement.Clone();
    }

    private AnalyzeVersionService Service(string fileId = FileId) => new(
        new FakeStamps(fileId),
        (_, _) => FileIdentityResult.Different);

    private AnalyzeVersionRequest Request(Policy policy, (string Force, string Length)? expected = null) => new()
    {
        FilePath = Scratch,
        OutputDir = _directory,
        ExpectedDocument = new AnalyzeVersionExpectedDocument { Path = Scratch, FileId = FileId },
        ExpectedUnits = new AnalyzeVersionExpectedUnits
        {
            Force = expected?.Force ?? "kip",
            Length = expected?.Length ?? "ft",
            Temperature = "F"
        },
        UnitPolicy = policy == Policy.CheckOnly ? "checkOnly" : "setAndRestore",
        Tables = new TableSelections(),
        ExtractionProfile = "full"
    };

    private async Task<Result<AnalyzeVersionData>> RunLogged(
        List<(string Name, List<string> Log)> logs,
        string name,
        Policy policy,
        Units present,
        Units? database = null)
    {
        var api = new FakeApi { Database = database ?? KipFt, Present = present };
        var result = await Service().AnalyzeOnApiAsync(api, Request(policy));
        logs.Add((name, api.Log));
        return result;
    }

    private sealed class FakeStamps(string fileId) : ISavedFileStampReader
    {
        public SavedFileStamp? Read(string path) => new(fileId, 1, DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeApi : IAnalyzeVersionApi
    {
        private int _databaseReads;
        private int _presentReads;
        private int _sets;

        public List<string> Log { get; } = [];

        /// <summary>The managed thread id of every recorded call, in call order.</summary>
        public List<int> Threads { get; } = [];

        /// <summary>When set, the async members really leave the calling thread (as real I/O does).</summary>
        public bool Yield { get; set; }

        private void Record(string name)
        {
            Log.Add(name);
            Threads.Add(Environment.CurrentManagedThreadId);
        }

        private async Task<T> Hop<T>(Func<T> work)
        {
            if (Yield)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }

            return work();
        }

        public string? ModelFile { get; set; } = Scratch;
        public Units Database { get; set; } = KipFt;
        public Units Present { get; set; } = KipFt;

        /// <summary>Scripted answer for the nth database read (1-based); null = the real state.</summary>
        public Func<int, (int Code, int Force, int Length, int Temperature)>? DatabaseReader { get; set; }

        public Func<int, (int Code, int Force, int Length, int Temperature)>? PresentReader { get; set; }

        /// <summary>Scripted setter: nth call (1-based) and the units; null = apply and return 0.</summary>
        public Func<int, int, int, int, int>? Setter { get; set; }

        public Func<Dictionary<string, TableExtractionOutcome>>? Extract { get; set; }
        public Func<Result<RunAnalysisData>>? Analysis { get; set; }

        public string? GetModelFilename()
        {
            Record("GetModelFilename");
            return ModelFile;
        }

        public int GetDatabaseUnits(out int force, out int length, out int temperature)
        {
            Record("GetDatabaseUnits");
            var (code, f, l, t) = DatabaseReader?.Invoke(++_databaseReads)
                ?? (0, Database.Force, Database.Length, Database.Temperature);
            (force, length, temperature) = (f, l, t);
            return code;
        }

        public int GetPresentUnits(out int force, out int length, out int temperature)
        {
            Record("GetPresentUnits");
            var (code, f, l, t) = PresentReader?.Invoke(++_presentReads)
                ?? (0, Present.Force, Present.Length, Present.Temperature);
            (force, length, temperature) = (f, l, t);
            return code;
        }

        public int SetPresentUnits(int force, int length, int temperature)
        {
            Record($"SetPresentUnits({force},{length},{temperature})");
            if (Setter is not null)
            {
                return Setter(++_sets, force, length, temperature);
            }

            Present = new Units(force, length, temperature);
            return 0;
        }

        public int CountPlannedTables(TableSelections tables) => 1;

        public Task<Result<RunAnalysisData>> RunAnalysisAsync(string filePath, List<string>? cases, UnitInfo units)
        {
            Record("RunAnalysis");
            return Hop(() => Analysis?.Invoke() ?? Result.Ok(new RunAnalysisData
            {
                FilePath = filePath,
                CaseCount = 3,
                FinishedCaseCount = 3
            }));
        }

        public (bool IsAnalyzed, bool IsLocked) ReadModelState()
        {
            Record("ReadModelState");
            return (true, true);
        }

        public Task<Dictionary<string, TableExtractionOutcome>> ExtractTablesAsync(
            TableSelections tables, string outputDir, bool isAnalyzed, bool isLocked)
        {
            Record("ExtractTables");
            return Hop(() => Extract?.Invoke() ?? new Dictionary<string, TableExtractionOutcome>
            {
                ["baseReactions"] = new TableExtractionOutcome { Success = true, RowCount = 3 }
            });
        }

        public Task<ModelMetadata> CollectMetadataAsync(string filePath, UnitSnapshot units)
        {
            Record("CollectMetadata");
            return Hop(() => new ModelMetadata());
        }

        public int OpenFile(string filePath)
        {
            Log.Add("OpenFile");
            return 0;
        }
    }
}
