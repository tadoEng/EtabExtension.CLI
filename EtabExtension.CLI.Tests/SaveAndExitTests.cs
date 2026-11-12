using System.Text.Json;
using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>A model-file API whose save is scripted: it can block, fail, throw, or change the file.</summary>
internal sealed class SaveApi(List<string> events) : IEtabsModelFileApi
{
    public string? CurrentPath { get; set; }

    public Func<int> OnSave { get; set; } = () => 0;

    public int SaveCalls => Volatile.Read(ref _saveCalls);

    private int _saveCalls;

    public string? SavedTo { get; private set; }

    public string? GetModelFilename()
    {
        lock (events)
        {
            events.Add("GetModelFilename");
        }

        return CurrentPath;
    }

    public int SaveFile(string filePath)
    {
        _ = Interlocked.Increment(ref _saveCalls);
        SavedTo = filePath;
        lock (events)
        {
            events.Add("SaveFile");
        }

        return OnSave();
    }

    public int OpenFile(string filePath) => throw new InvalidOperationException("never opens");
}

/// <summary>Everything one <c>save-and-exit</c> test needs: a recorded document, a scripted save, probes for the exit.</summary>
internal sealed class SaveAndExitRig
{
    public const string Edb = @"D:\Project\.etabs-ext\main\working\model.edb";
    public const string Et = @"D:\Project\.etabs-ext\main\working\model.$et";
    public const string FileId = "0000abcd-0000000000001234";

    public static readonly DateTimeOffset Saved = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public SaveAndExitRig(TimeSpan? savingBudget = null, TimeSpan? exitBudget = null)
    {
        Session = new ProbeSession
        {
            Started = true,
            RecordedDocument = new EtabsDocumentRecord(Edb, FileId)
        };
        Api = new SaveApi(Events) { CurrentPath = Edb };
        Stamps = new FixedStamps(new SavedFileStamp(FileId, 100, Saved));

        // A save that really changes the file on disk, the way ETABS's does.
        Api.OnSave = () =>
        {
            Stamps.Current = new SavedFileStamp(FileId, 120, Saved.AddSeconds(5));
            return 0;
        };
        Service = new SaveAndExitService(
            Session,
            Watchdog,
            new FixedApiFactory(Api),
            Registry,
            Stamps,
            savingBudget ?? TimeSpan.FromSeconds(20),
            exitBudget ?? TimeSpan.FromSeconds(20));
    }

    public List<string> Events { get; } = [];

    public ProbeSession Session { get; }

    public ProbeWatchdog Watchdog { get; } = new();

    public SaveApi Api { get; }

    public FixedStamps Stamps { get; }

    public SaveAndExitRegistry Registry { get; } = new();

    public SaveAndExitService Service { get; }

    public static SaveAndExitRequest Request(string? filePath = null, string? fileId = FileId, string? path = Edb) => new()
    {
        FilePath = filePath,
        ExpectedDocument = fileId is null ? null : new ExpectedDocument(fileId) { Path = path }
    };

    public Task<object> Run(SaveAndExitRequest? request = null, long id = 1) =>
        Service.RunAsync(
            request ?? Request(),
            id,
            body => Kit.OnDedicatedThread(() => body().GetAwaiter().GetResult()));

    /// <summary>Nothing was saved and nothing was exited or killed.</summary>
    public void AssertUntouched()
    {
        Assert.Equal(0, Api.SaveCalls);
        Assert.Equal(0, Session.NormalShutdowns);
        Assert.Equal(0, Session.BlockedShutdowns);
    }

    /// <summary>Whatever else happened, ETABS was neither exited nor killed.</summary>
    public void AssertNeverExited()
    {
        Assert.Equal(0, Session.NormalShutdowns);
        Assert.Equal(0, Session.BlockedShutdowns);
    }
}

/// <summary>Card v2.5a A1: the document identity is verified, from the record, before ANY save.</summary>
public sealed class SaveAndExitDocumentTests
{
    private const string Edb = SaveAndExitRig.Edb;

    private static string Error(object result) =>
        Assert.IsType<Result>(result, exactMatch: false).Error ?? string.Empty;

    // The normal names proceed: the EDB itself, and the same-stem .$et ETABS reports after a save.
    [Theory]
    [InlineData(Edb)]
    [InlineData(SaveAndExitRig.Et)]
    public void TheNormalEdbOrEtNamesProceedToASaveAndOneExit(string observed)
    {
        var rig = new SaveAndExitRig();
        rig.Api.CurrentPath = observed;

        var result = rig.Run().Within("the command");

        var typed = Assert.IsType<Result<SaveAndExitData>>(result);
        Assert.True(typed.Success, typed.Error);
        Assert.True(typed.Data!.Saved);
        Assert.True(typed.Data.Exited);
        Assert.Equal(1, rig.Api.SaveCalls);
        Assert.Equal(1, rig.Session.NormalShutdowns);
        Assert.Equal(0, rig.Session.BlockedShutdowns);
        Assert.True(rig.Events.IndexOf("SaveFile") > rig.Events.IndexOf("GetModelFilename"));
    }

    // save_and_exit_uses_the_record_edb_path_not_get_model_filename.
    // Mutation: the destination is the observed filename (the .$et), not the recorded EDB.
    [Fact]
    public void SaveAndExitUsesTheRecordEdbPathNotGetModelFilename()
    {
        var rig = new SaveAndExitRig();
        rig.Api.CurrentPath = SaveAndExitRig.Et;

        var result = rig.Run().Within("the command");

        Assert.True(Assert.IsType<Result<SaveAndExitData>>(result).Success);
        Assert.Equal(Path.GetFullPath(Edb), rig.Api.SavedTo);
        Assert.NotEqual(SaveAndExitRig.Et, rig.Api.SavedTo);
    }

    // The user opened another model: refuse, with nothing saved. Mutation: skip the observed-name check.
    [Theory]
    [InlineData(@"D:\Project\.etabs-ext\other\working\model.edb")]
    [InlineData(@"D:\Project\.etabs-ext\main\working\other.edb")]
    [InlineData(@"D:\Project\.etabs-ext\main\working\other.$et")]
    public void AnotherModelTheUserOpenedRefusesWithNothingSaved(string observed)
    {
        var rig = new SaveAndExitRig();
        rig.Api.CurrentPath = observed;

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    // The post-export name: ExportFile changes ETABS's current file name to the .e2k.
    // Mutation: accept a same-stem sibling of ANY extension.
    [Theory]
    [InlineData(@"D:\Project\.etabs-ext\main\working\model.e2k")]
    [InlineData(@"D:\Project\.etabs-ext\main\working\model.edb.bak")]
    public void ThePostExportE2kNameRefusesNothingSavedNothingExited(string observed)
    {
        var rig = new SaveAndExitRig();
        rig.Api.CurrentPath = observed;

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"D:\Project\.etabs-ext\main\working\")]
    public void ABlankOrFolderOnlyObservedNameRefuses(string? observed)
    {
        var rig = new SaveAndExitRig();
        rig.Api.CurrentPath = observed;

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    // The EDB was replaced on disk (a new file id). Mutation: skip the file-id comparison.
    [Fact]
    public void AnEdbReplacedOnDiskWithANewFileIdRefuses()
    {
        var rig = new SaveAndExitRig();
        rig.Stamps.Current = new SavedFileStamp("0000abcd-0000000000009999", 100, SaveAndExitRig.Saved);

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        Assert.Contains("replaced", Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    [Fact]
    public void AnEdbThatIsGoneRefuses()
    {
        var rig = new SaveAndExitRig();
        rig.Stamps.Current = null;

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    // A destination mismatch: the save destination is the recorded path ONLY. Mutation: honour a
    // requested destination.
    [Theory]
    [InlineData(@"D:\Project\.etabs-ext\elsewhere\model.edb")]
    [InlineData(@"D:\Project\.etabs-ext\main\working\model.e2k")]
    public void ADestinationThatIsNotTheRecordedPathRefuses(string destination)
    {
        var rig = new SaveAndExitRig();

        var result = rig.Run(SaveAndExitRig.Request(filePath: destination)).Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        Assert.Contains("destination", Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    [Fact]
    public void ARequestedDestinationThatEqualsTheRecordedPathIsAccepted()
    {
        var rig = new SaveAndExitRig();

        var result = rig.Run(SaveAndExitRig.Request(filePath: Edb.ToUpperInvariant())).Within("the command");

        Assert.True(Assert.IsType<Result<SaveAndExitData>>(result).Success);
    }

    // The host's identity must be the one recorded at open. Mutation: skip the expected-document comparison.
    [Theory]
    [InlineData("0000abcd-0000000000009999", Edb)] // another file id
    [InlineData(null, Edb)] // no expected document at all
    [InlineData(SaveAndExitRig.FileId, @"D:\Project\.etabs-ext\other\working\model.edb")] // another path
    public void ARequestThatDoesNotPresentTheRecordedIdentityRefuses(string? fileId, string path)
    {
        var rig = new SaveAndExitRig();

        var result = rig.Run(SaveAndExitRig.Request(fileId: fileId, path: path)).Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    // No record, or a record without a file identity, means there is nothing to verify against.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInstanceRecordWithoutAUsableDocumentRefuses(bool recordWithoutFileId)
    {
        var rig = new SaveAndExitRig();
        rig.Session.RecordedDocument = recordWithoutFileId ? new EtabsDocumentRecord(Edb, null) : null;

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.DocumentUnverified, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    // The modal rule: nothing has changed at that point. Mutation: no dialog check.
    [Fact]
    public void AnOpenModalDialogRefusesBeforeAnythingIsSavedOrExited()
    {
        var rig = new SaveAndExitRig();
        rig.Watchdog.Check = new EtabsDialogCheck(
            EtabsDialogCheckKind.Modal,
            new EtabsModalDialogObservation("Define Load Patterns", "x", false),
            null);

        var result = rig.Run().Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.ModalOpen, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    [Fact]
    public void AnInconclusiveDialogScanRefusesBeforeAnythingIsSavedOrExited()
    {
        var rig = new SaveAndExitRig();
        rig.Watchdog.Check = new EtabsDialogCheck(EtabsDialogCheckKind.Inconclusive, null, "scan failed");

        var result = rig.Run().Within("the command");

        Assert.Contains(EtabsTypedFailureCodes.DialogScanInconclusive, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    // A failed or unknown save NEVER exits (card v2.5 §2). Mutation: exit regardless of the save.
    [Fact]
    public void ANonZeroSaveReturnCodeIsAFailedSaveAndNeverExits()
    {
        var rig = new SaveAndExitRig();
        rig.Api.OnSave = () => 7;

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.SaveFailed, Error(result), StringComparison.Ordinal);
        Assert.Contains("\"saved\":false", Error(result), StringComparison.Ordinal);
        Assert.Equal(1, rig.Api.SaveCalls);
        rig.AssertNeverExited();
    }

    [Fact]
    public void ASaveThatFaultsIsUnknownAndNeverExits()
    {
        var rig = new SaveAndExitRig();
        rig.Api.OnSave = () => throw new InvalidOperationException("COM");

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.SaveUnknown, Error(result), StringComparison.Ordinal);
        Assert.Contains("\"saved\":\"unknown\"", Error(result), StringComparison.Ordinal);
        Assert.Contains("\"exited\":false", Error(result), StringComparison.Ordinal);
        rig.AssertNeverExited();
    }

    // The EDB did not change on disk: ETABS_SAVE_NOT_OBSERVED. Mutation: ignore the unchanged file.
    [Fact]
    public void AFileThatDidNotChangeOnDiskIsSaveNotObservedAndNeverExits()
    {
        var rig = new SaveAndExitRig();
        rig.Api.OnSave = () => 0; // returns 0, writes nothing

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveModelService.SaveNotObservedCode, Error(result), StringComparison.Ordinal);
        Assert.Equal("ETABS_SAVE_NOT_OBSERVED", SaveAndExitCodes.SaveNotObserved);
        rig.AssertNeverExited();
    }

    // A2: the residual is disclosed, not prevented. An edit accepted after the save, or during
    // a delayed exit, does not block the exit and is not re-checked. Mutation: a re-check or a
    // quiescence step between the save and the exit.
    [Fact]
    public void AnEditAfterTheSaveOrDuringDelayedExitProcessingIsTheDisclosedResidualNotARefusal()
    {
        var rig = new SaveAndExitRig();
        var edits = 0;
        rig.Service.AfterSaveBeforeExitCas = () =>
        {
            // The user's edit lands between the save call and the exit call.
            edits++;
            rig.Stamps.Current = new SavedFileStamp(SaveAndExitRig.FileId, 999, SaveAndExitRig.Saved.AddMinutes(1));
            rig.Api.CurrentPath = @"D:\Project\.etabs-ext\main\working\model.e2k";
        };
        rig.Session.NormalShutdown = () =>
        {
            // ...and another during the delayed exit's own processing.
            edits++;
            Thread.Sleep(150);
            return Shutdowns.Ok();
        };

        var result = rig.Run().Within("the command");

        Assert.True(Assert.IsType<Result<SaveAndExitData>>(result).Success);
        Assert.Equal(2, edits);
        Assert.Equal(1, rig.Session.NormalShutdowns);
        Assert.Equal(
            "Edits not included in the saved state, accepted before closing takes effect, may be lost.",
            SaveAndExitService.ResidualDisclosure);
    }

    // An exit that is not confirmed is UNKNOWN, never exited:false (card v2.5a A4).
    // Mutation: report exited:false.
    [Fact]
    public void AnUnconfirmedExitIsAnUnknownPayloadNeverExitedFalse()
    {
        var rig = new SaveAndExitRig();
        rig.Session.NormalShutdown = () => Shutdowns.Unconfirmed();

        var result = rig.Run().Within("the command");

        var error = Error(result);
        Assert.Contains(SaveAndExitCodes.ExitUnconfirmed, error, StringComparison.Ordinal);
        var detail = JsonDocument.Parse(error[(error.IndexOf(" detail=", StringComparison.Ordinal) + " detail=".Length)..]).RootElement;
        Assert.True(detail.GetProperty("saved").GetBoolean());
        Assert.Equal("unknown", detail.GetProperty("exited").GetString());
        Assert.DoesNotContain("\"exited\":false", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExitThatNeverReturnsIsAnsweredUnconfirmedWithinItsBudgetAsSavedTrueExitedUnknown()
    {
        var rig = new SaveAndExitRig(exitBudget: TimeSpan.FromMilliseconds(400));
        using var never = new ManualResetEventSlim(false);
        rig.Session.NormalShutdown = () =>
        {
            never.Wait(TimeSpan.FromSeconds(30));
            return Shutdowns.Ok();
        };

        var result = rig.Run().Within("the exit's own bound");

        var error = Error(result);
        Assert.Contains(SaveAndExitCodes.ExitUnconfirmed, error, StringComparison.Ordinal);
        Assert.Contains("\"saved\":true", error, StringComparison.Ordinal);
        Assert.Contains("\"exited\":\"unknown\"", error, StringComparison.Ordinal);
        never.Set();
    }
}

/// <summary>Card v2.5a A4: the Saving → Exiting lifecycle and its CAS with every abandoner.</summary>
public sealed class SaveAndExitLifecycleTests
{
    private static string Error(object result) =>
        Assert.IsType<Result>(result, exactMatch: false).Error ?? string.Empty;

    // abandoned_save_and_exit_before_exiting_never_exits. Mutation: no Saving -> Exiting CAS.
    [Theory]
    [InlineData("cancel")]
    [InlineData("detach")]
    [InlineData("eof-or-shutdown")]
    public void AbandonedSaveAndExitBeforeExitingNeverExits(string rival)
    {
        var rig = new SaveAndExitRig();
        using var saving = new ManualResetEventSlim(false);
        using var finish = new ManualResetEventSlim(false);
        rig.Api.OnSave = () =>
        {
            saving.Set();
            finish.Signalled("the test to finish the save");
            rig.Stamps.Current = new SavedFileStamp(SaveAndExitRig.FileId, 120, SaveAndExitRig.Saved.AddSeconds(5));
            return 0;
        };

        var running = rig.Run(id: 9);
        saving.Signalled("the save to start");
        switch (rival)
        {
            case "cancel":
                Assert.Equal(SaveAndExitState.Abandoned, rig.Registry.Cancel(9));
                break;
            default:
                Assert.True(rig.Registry.AbandonCurrent());
                break;
        }

        // The abandonment is answered AT ONCE, while the save is still running.
        var answer = running.Within("the abandonment answer");
        Assert.Contains(SaveAndExitCodes.Abandoned, Error(answer), StringComparison.Ordinal);
        Assert.Contains("\"saved\":\"unknown\"", Error(answer), StringComparison.Ordinal);

        // The late Save success finds Abandoned and NEVER exits.
        finish.Set();
        Thread.Sleep(250);
        Assert.Equal(1, rig.Api.SaveCalls);
        rig.AssertNeverExited();
        Assert.False(rig.Registry.Begin(10) is null, "the abandoned operation no longer blocks the next one");
    }

    // A pause after the save and before the exit CAS, then a cancel: never exits.
    [Fact]
    public void APauseAfterTheSaveBeforeTheExitCasThenACancelNeverExits()
    {
        var rig = new SaveAndExitRig();
        rig.Service.AfterSaveBeforeExitCas = () => Assert.Equal(SaveAndExitState.Abandoned, rig.Registry.Cancel(1));

        var result = rig.Run().Within("the command");

        Assert.Contains(SaveAndExitCodes.Abandoned, Error(result), StringComparison.Ordinal);
        Assert.Equal(1, rig.Api.SaveCalls);
        rig.AssertNeverExited();
    }

    // The C11 equivalent of the retired 61': a save that returns after the Saving phase expired is
    // dropped and can never exit. Mutation: chain the exit to the save completion.
    [Fact]
    public void ASaveSuccessArrivingAfterTheSavingPhaseExpiredIsUnknownAndNeverExits()
    {
        var rig = new SaveAndExitRig(savingBudget: TimeSpan.FromMilliseconds(1500));
        using var finish = new ManualResetEventSlim(false);
        using var saving = new ManualResetEventSlim(false);
        rig.Api.OnSave = () =>
        {
            saving.Set();
            finish.Signalled("the test to finish the late save");
            rig.Stamps.Current = new SavedFileStamp(SaveAndExitRig.FileId, 120, SaveAndExitRig.Saved.AddSeconds(5));
            return 0;
        };

        var running = rig.Run();
        saving.Signalled("the save to start");
        var answer = running.Within("the expiry answer");

        var error = Error(answer);
        Assert.Contains(SaveAndExitCodes.SaveUnknown, error, StringComparison.Ordinal);
        Assert.Contains("\"saved\":\"unknown\"", error, StringComparison.Ordinal);
        Assert.Contains("\"exited\":false", error, StringComparison.Ordinal);

        finish.Set();
        Thread.Sleep(250);
        rig.AssertNeverExited();
    }

    // The Saving clock is counted from INTAKE: a body still queued behind a blocked STA when the
    // phase expires never starts a save afterwards. Mutation: no state check at the start of the body.
    [Fact]
    public void ABodyThatStartsAfterTheSavingPhaseExpiredNeverSavesAndNeverExits()
    {
        var rig = new SaveAndExitRig(savingBudget: TimeSpan.FromMilliseconds(300));
        var queued = new TaskCompletionSource<Func<Task<object>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = rig.Service.RunAsync(
            SaveAndExitRig.Request(),
            3,
            body =>
            {
                queued.SetResult(body);
                return started.Task;
            });
        var body = queued.Task.Within("the body to be queued");
        var answer = running.Within("the expiry answer");
        Assert.Contains(SaveAndExitCodes.SaveUnknown, Error(answer), StringComparison.Ordinal);

        var late = body().Within("the late body");
        started.SetResult(late);

        Assert.Contains(SaveAndExitCodes.Abandoned, Error(late), StringComparison.Ordinal);
        Assert.Equal(0, rig.Api.SaveCalls);
        rig.AssertNeverExited();
    }

    // Once Exiting is won, the Saving budget cannot take it: the exit runs to its own answer.
    [Fact]
    public void OnceTheExitCasIsWonATimeoutNeverAbandonsItAndACancelIsRefused()
    {
        var rig = new SaveAndExitRig(savingBudget: TimeSpan.FromMilliseconds(1500));
        using var exitStarted = new ManualResetEventSlim(false);
        using var finishExit = new ManualResetEventSlim(false);
        rig.Session.NormalShutdown = () =>
        {
            exitStarted.Set();
            finishExit.Signalled("the test to finish the slow exit");
            return Shutdowns.Ok();
        };

        var running = rig.Run(id: 5);
        exitStarted.Signalled("the exit to start");
        Thread.Sleep(1900); // well past the 1.5 s Saving budget

        Assert.False(running.IsCompleted, "a won exit is not answered as a Saving timeout");
        Assert.Equal(SaveAndExitState.Exiting, rig.Registry.Cancel(5));
        Assert.False(rig.Registry.AbandonCurrent(), "an exiting operation cannot be abandoned");
        finishExit.Set();
        var result = running.Within("the exit's own answer");

        Assert.True(Assert.IsType<Result<SaveAndExitData>>(result).Success);
        Assert.Equal(1, rig.Session.NormalShutdowns);
    }

    [Fact]
    public void ASecondCommandWhileOneIsInFlightIsBusyAndNothingIsSavedOrExited()
    {
        var rig = new SaveAndExitRig();
        _ = rig.Registry.Begin(1);

        var result = rig.Run(id: 2).Within("the command");

        Assert.Contains(SaveAndExitCodes.Busy, Error(result), StringComparison.Ordinal);
        rig.AssertUntouched();
    }

    // The 331 s policy: 2 s lock + 300 s save + 21 s exit + 8 s margin (card v2.5a A4).
    [Fact]
    public void ThePhaseDeadlinesAreTwoPlusThreeHundredPlusTwentyOnePlusEightIsThreeHundredThirtyOne()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), SaveAndExitService.LockWait);
        Assert.Equal(TimeSpan.FromSeconds(300), SaveAndExitService.SaveWait);
        Assert.Equal(TimeSpan.FromSeconds(302), SaveAndExitService.DefaultSavingBudget);
        Assert.Equal(TimeSpan.FromSeconds(21), SaveAndExitService.DefaultExitBudget);
        Assert.Equal(TimeSpan.FromSeconds(8), SaveAndExitService.HostMargin);
        Assert.Equal(TimeSpan.FromSeconds(331), SaveAndExitService.HostPolicy);
    }
}

/// <summary>
/// Synchronous work runs one item at a time on a worker thread, like the single STA: a save that
/// blocks its thread holds the "STA", and the reader thread stays free to read the next frame.
/// </summary>
internal sealed class ThreadedSerialOperations : IOperationManager
{
    private readonly SemaphoreSlim _sta = new(1, 1);

    public bool HasActiveOperation => false;

    public Result<StartOperationData> Start(string kind, JsonElement payload) => throw new NotSupportedException();

    public Result<OperationStatusData> GetStatus(string operationId) => throw new NotSupportedException();

    public Result<GetOperationEventsData> GetEvents(string operationId, long sinceSequence) =>
        throw new NotSupportedException();

    public Result<CancelOperationData> Cancel(string operationId) => throw new NotSupportedException();

    public Task<object> WaitAsync(string operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async Task<T> ExecuteSynchronousAsync<T>(Func<Task<T>> action)
    {
        await _sta.WaitAsync();
        try
        {
            return await Task.Factory.StartNew(
                () => action().GetAwaiter().GetResult(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        finally
        {
            _ = _sta.Release();
        }
    }

    public void Dispose() => _sta.Dispose();
}

/// <summary>Card v2.5a A4: through the REAL intake (<see cref="ServeLoop"/> + dispatcher + coordinator).</summary>
public sealed class SaveAndExitIntakeTests
{
    private sealed class Intake : IDisposable
    {
        public Intake(
            Func<ProbeSession, ServeShutdownCoordinator>? coordinator = null,
            ServeEofPolicy eof = ServeEofPolicy.Exit,
            TimeSpan? savingBudget = null,
            ISaveModelService? save = null)
        {
            Rig = new SaveAndExitRig(savingBudget);
            var dispatcher = DispatcherRig.Create(
                Rig.Session,
                Rig.Watchdog,
                save: save,
                operations: new ThreadedSerialOperations(),
                saveAndExit: Rig.Service,
                c11: Rig.Registry);
            Dispatcher = dispatcher;
            Coordinator = coordinator?.Invoke(Rig.Session) ?? new ServeShutdownCoordinator(
                new ProbeWorker(busy: false),
                Rig.Session,
                Rig.Watchdog,
                c10: null,
                c11: Rig.Registry);
            Loop = new ServeLoop(
                dispatcher,
                Coordinator,
                new ManagedEtabsStartIntentScope(),
                capabilities => new ServeHandshake(
                    "etab-cli-serve", 1, "0.1.0", "0.1.0+gtest", Environment.ProcessId,
                    Path.GetFullPath(Environment.ProcessPath!), capabilities),
                TextWriter.Null,
                eof);
            Run = Loop.RunAsync(Reader, Writer, TestContext.Current.CancellationToken);
        }

        public SaveAndExitRig Rig { get; }

        public ServeDispatcher Dispatcher { get; }

        public ServeShutdownCoordinator Coordinator { get; }

        public ServeLoop Loop { get; }

        public ScriptedLines Reader { get; } = new();

        public FrameCollector Writer { get; } = new();

        public Task Run { get; }

        public void Send(long id, string command, object? request = null) => Reader.Send(
            JsonSerializer.Serialize(new { id, command, request }));

        public void SendSaveAndExit(long id) => Send(id, "save-and-exit", new
        {
            expectedDocument = new { path = SaveAndExitRig.Edb, fileId = SaveAndExitRig.FileId }
        });

        public string ErrorOf(long id) => Writer.Frame(id)!.Value.GetProperty("error").GetString()!;

        public void Dispose() => Reader.Dispose();
    }

    // Capability: the handshake and the dispatcher both advertise save-and-exit, and the loop
    // treats it as a control command (registered at intake).
    [Fact]
    public void SaveAndExitIsAdvertisedAndIsAControlCommand()
    {
        using var intake = new Intake();

        Assert.Contains("save-and-exit", intake.Dispatcher.Capabilities);
        Assert.Contains("save-and-exit", ServeLoop.ControlCommands);
        intake.Reader.Close();
        intake.Run.Within("the loop");
    }

    [Fact]
    public void ASuccessfulSaveAndExitIsAnsweredOnItsOwnIdWithSavedAndExitedTrue()
    {
        using var intake = new Intake();

        intake.SendSaveAndExit(4);
        DetachTests.WaitFor(() => intake.Writer.Frame(4) is not null, "the answer");

        var data = intake.Writer.Frame(4)!.Value.GetProperty("data");
        Assert.True(intake.Writer.Frame(4)!.Value.GetProperty("success").GetBoolean());
        Assert.Equal("ended", data.GetProperty("stage").GetString());
        Assert.True(data.GetProperty("saved").GetBoolean());
        Assert.True(data.GetProperty("exited").GetBoolean());
        Assert.Equal(1, intake.Rig.Session.NormalShutdowns);
        intake.Reader.Close();
        intake.Run.Within("the loop");
    }

    // Detach during Save: the detach wins the CAS; the late save success never exits.
    [Fact]
    public void ADetachDuringSaveAbandonsItAndTheLateSaveNeverExits()
    {
        using var intake = new Intake();
        using var saving = new ManualResetEventSlim(false);
        using var finish = new ManualResetEventSlim(false);
        intake.Rig.Api.OnSave = () =>
        {
            saving.Set();
            finish.Signalled("the test to finish the save");
            intake.Rig.Stamps.Current = new SavedFileStamp(SaveAndExitRig.FileId, 120, SaveAndExitRig.Saved.AddSeconds(5));
            return 0;
        };

        intake.SendSaveAndExit(1);
        saving.Signalled("the save to start");
        intake.Send(2, "detach");
        DetachTests.WaitFor(() => intake.Writer.Frame(2) is not null, "the detach answer");
        DetachTests.WaitFor(() => intake.Writer.Frame(1) is not null, "the abandonment answer");
        Assert.Contains(SaveAndExitCodes.Abandoned, intake.ErrorOf(1), StringComparison.Ordinal);

        finish.Set();
        Thread.Sleep(250);
        intake.Reader.Close();
        intake.Run.Within("the loop");

        Assert.Equal(1, intake.Rig.Session.Detaches);
        intake.Rig.AssertNeverExited();
    }

    // EOF suppression during Save: a user-visible instance whose EOF detach FAILS is left running
    // on purpose; the save that completes late never exits it.
    [Fact]
    public void EofSuppressionDuringSaveNeverExitsOnTheLateSaveSuccess()
    {
        using var intake = new Intake(eof: ServeEofPolicy.DetachVisible);
        intake.Rig.Session.UserVisible = true;
        intake.Rig.Session.DetachBehavior = () => DetachResult.Failed(
            ManagedEtabsShutdownErrorCodes.DetachFailed,
            "the record write failed before it started");
        using var saving = new ManualResetEventSlim(false);
        using var finish = new ManualResetEventSlim(false);
        intake.Rig.Api.OnSave = () =>
        {
            saving.Set();
            finish.Signalled("the test to finish the save");
            intake.Rig.Stamps.Current = new SavedFileStamp(SaveAndExitRig.FileId, 120, SaveAndExitRig.Saved.AddSeconds(5));
            return 0;
        };

        intake.SendSaveAndExit(1);
        saving.Signalled("the save to start");
        intake.Reader.Close();
        intake.Run.Within("the loop to end on EOF");

        Assert.Contains(SaveAndExitCodes.Abandoned, intake.ErrorOf(1), StringComparison.Ordinal);
        finish.Set();
        Thread.Sleep(250);
        Assert.Equal(1, intake.Rig.Session.Detaches);
        intake.Rig.AssertNeverExited();
    }

    // A late Save success after a cancel by request id: no exit.
    [Fact]
    public void ALateSaveSuccessAfterACancelOperationByRequestIdNeverExits()
    {
        using var intake = new Intake();
        using var saving = new ManualResetEventSlim(false);
        using var finish = new ManualResetEventSlim(false);
        intake.Rig.Api.OnSave = () =>
        {
            saving.Set();
            finish.Signalled("the test to finish the save");
            intake.Rig.Stamps.Current = new SavedFileStamp(SaveAndExitRig.FileId, 120, SaveAndExitRig.Saved.AddSeconds(5));
            return 0;
        };

        intake.SendSaveAndExit(1);
        saving.Signalled("the save to start");
        intake.Send(2, "cancel-operation", new { requestId = 1 });
        DetachTests.WaitFor(() => intake.Writer.Frame(2) is not null, "the cancel answer");

        Assert.True(intake.Writer.Frame(2)!.Value.GetProperty("success").GetBoolean());
        DetachTests.WaitFor(() => intake.Writer.Frame(1) is not null, "the abandonment answer");
        finish.Set();
        Thread.Sleep(250);

        // Before EOF: the loop's own cleanup exit on EOF is not this command's.
        intake.Rig.AssertNeverExited();
        intake.Reader.Close();
        intake.Run.Within("the loop");
    }

    [Fact]
    public void AFailedSaveThroughTheIntakeAnswersTypedAndNeverExits()
    {
        using var intake = new Intake();
        intake.Rig.Api.OnSave = () => 3;

        intake.SendSaveAndExit(1);
        DetachTests.WaitFor(() => intake.Writer.Frame(1) is not null, "the answer");

        Assert.False(intake.Writer.Frame(1)!.Value.GetProperty("success").GetBoolean());
        Assert.Contains(SaveAndExitCodes.SaveFailed, intake.ErrorOf(1), StringComparison.Ordinal);
        intake.Reader.Close();
        intake.Run.Within("the loop");
        // EOF's own exit is the loop's cleanup, not this command's: before it, nothing had exited.
        Assert.Equal(1, intake.Rig.Api.SaveCalls);
    }

    [Fact]
    public void AnUnconfirmedExitThroughTheIntakeAnswersAnUnknownPayload()
    {
        using var intake = new Intake();
        intake.Rig.Session.NormalShutdown = () => Shutdowns.Unconfirmed();

        intake.SendSaveAndExit(1);
        DetachTests.WaitFor(() => intake.Writer.Frame(1) is not null, "the answer");

        var error = intake.ErrorOf(1);
        Assert.Contains(SaveAndExitCodes.ExitUnconfirmed, error, StringComparison.Ordinal);
        Assert.Contains("\"saved\":true", error, StringComparison.Ordinal);
        Assert.Contains("\"exited\":\"unknown\"", error, StringComparison.Ordinal);
        intake.Reader.Close();
        intake.Run.Within("the loop");
    }

    // The C11 daemon part of 62': after an unknown save, the daemon never exits on its own; only
    // an explicit shutdown ends it (the host's "Close anyway (discard)" confirmation is host-side).
    [Fact]
    public void AfterAnUnknownSaveNothingExitsUntilAnExplicitShutdownIsSent()
    {
        using var intake = new Intake(savingBudget: TimeSpan.FromMilliseconds(400));
        using var finish = new ManualResetEventSlim(false);
        intake.Rig.Api.OnSave = () =>
        {
            finish.Signalled("the test to finish the late save");
            return 0;
        };

        intake.SendSaveAndExit(1);
        DetachTests.WaitFor(() => intake.Writer.Frame(1) is not null, "the unknown answer");
        Assert.Contains(SaveAndExitCodes.SaveUnknown, intake.ErrorOf(1), StringComparison.Ordinal);
        finish.Set();
        Thread.Sleep(250);
        intake.Rig.AssertNeverExited();

        intake.Send(2, "shutdown");
        intake.Run.Within("the loop to end on the explicit shutdown");
        Assert.True(intake.Rig.Session.NormalShutdowns >= 1);
    }

    // Registered at INTAKE (like C10): behind an ordinary request that blocks the STA, a
    // save-and-exit is still read, can be cancelled, can expire, and never saves or exits later.
    // Mutation: drop it from the control commands (it is then chained behind the blocked request).
    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    public void AQueuedSaveAndExitCanBeCancelledOrExpireAndNeverSavesOrExits(string how)
    {
        var save = new BlockingSaveModel();
        using var intake = new Intake(
            savingBudget: how == "expire" ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(20),
            save: save);

        intake.Send(1, "save-model", new { filePath = SaveAndExitRig.Edb });
        DetachTests.WaitFor(() => save.Started, "the ordinary request to block the STA");
        intake.SendSaveAndExit(2);
        DetachTests.WaitFor(() => intake.Reader.LinesRead == 2, "the save-and-exit frame to be read");
        if (how == "cancel")
        {
            DetachTests.WaitFor(
                () => intake.Rig.Registry.Cancel(2) == SaveAndExitState.Abandoned,
                "the queued command to be cancellable by request id");
        }

        DetachTests.WaitFor(() => intake.Writer.Frame(2) is not null, "the answer while the ordinary request is still blocked");
        Assert.Null(intake.Writer.Frame(1));
        Assert.Contains(
            how == "cancel" ? SaveAndExitCodes.Abandoned : SaveAndExitCodes.SaveUnknown,
            intake.ErrorOf(2),
            StringComparison.Ordinal);

        save.Release.SetResult(Result.Ok(new SaveModelData()));
        DetachTests.WaitFor(() => intake.Writer.Frame(1) is not null, "the ordinary request's own answer");
        Thread.Sleep(200); // the stale body now runs and must find the arbiter taken
        Assert.Equal(0, intake.Rig.Api.SaveCalls);
        intake.Reader.Close();
        intake.Run.Within("the loop");
        Assert.Equal(0, intake.Rig.Session.BlockedShutdowns);
    }

    private sealed class BlockingSaveModel : ISaveModelService
    {
        private int _started;

        public bool Started => Volatile.Read(ref _started) == 1;

        public TaskCompletionSource<Result<SaveModelData>> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<SaveModelData>> SaveModelAsync(string filePath) => Release.Task;

        public Task<Result<SaveModelData>> SaveModelOnAppAsync(ETABSApplication app, string filePath)
        {
            Volatile.Write(ref _started, 1);
            return Release.Task;
        }
    }
}

/// <summary>Card v2.5a A1: the instance record gains <c>document: {path, fileId}</c>, written durably at open.</summary>
public sealed class DocumentRecordTests
{
    private sealed class FakeOpen(Result<OpenModelData> result) : IOpenModelService
    {
        public Task<Result<OpenModelData>> OpenModelOnAppAsync(ETABSApplication app, string filePath, bool save) =>
            Task.FromResult(result);

        public Task<Result<OpenModelData>> OpenModelAsync(string filePath, bool save, bool newInstance) =>
            throw new NotSupportedException();
    }

    private static object Open(ProbeSession session, Result<OpenModelData> opened, FixedStamps stamps)
    {
        var dispatcher = DispatcherRig.Create(
            session,
            new ProbeWatchdog(),
            open: new FakeOpen(opened),
            stamps: stamps);
        return dispatcher.DispatchAsync(
            "open-model",
            JsonSerializer.SerializeToElement(new { filePath = SaveAndExitRig.Edb, background = true }),
            CancellationToken.None).Within("open-model");
    }

    // The document is written when the daemon opens a model. Mutation: do not record on open.
    [Fact]
    public void OpeningAModelRecordsItsPathAndFileIdDurablyAndAnswersThem()
    {
        var session = new ProbeSession { Started = true };
        var stamps = new FixedStamps(new SavedFileStamp(SaveAndExitRig.FileId, 100, SaveAndExitRig.Saved));

        var answer = Open(session, Result.Ok(new OpenModelData { FilePath = SaveAndExitRig.Edb }), stamps);

        var typed = Assert.IsType<Result<OpenModelData>>(answer);
        Assert.True(typed.Success, typed.Error);
        var recorded = Assert.Single(session.RecordedWrites);
        Assert.Equal(Path.GetFullPath(SaveAndExitRig.Edb), recorded.Path);
        Assert.Equal(SaveAndExitRig.FileId, recorded.FileId);
        Assert.Equal(recorded, typed.Data!.Document);
        var json = JsonSerializer.Serialize(typed.Data, ServeJson.Options);
        Assert.Contains("\"document\":{\"path\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedOpenRecordsNothing()
    {
        var session = new ProbeSession { Started = true };

        var answer = Open(
            session,
            Result.Fail<OpenModelData>("OpenFile failed"),
            new FixedStamps(new SavedFileStamp(SaveAndExitRig.FileId, 100, SaveAndExitRig.Saved)));

        Assert.False(Assert.IsType<Result<OpenModelData>>(answer).Success);
        Assert.Empty(session.RecordedWrites);
    }

    [Fact]
    public void AnOpenWhoseIdentityCannotBeRecordedFailsTypedBecauseSaveAndExitWouldRefuse()
    {
        var session = new ProbeSession
        {
            Started = true,
            RecordFailure = $"[{EtabsLaunchErrorCodes.RecoveryRecordWriteFailed}] the record write threw"
        };

        var answer = Open(
            session,
            Result.Ok(new OpenModelData { FilePath = SaveAndExitRig.Edb }),
            new FixedStamps(new SavedFileStamp(SaveAndExitRig.FileId, 100, SaveAndExitRig.Saved)));

        var failed = Assert.IsType<Result<OpenModelData>>(answer);
        Assert.False(failed.Success);
        Assert.Contains(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, failed.Error, StringComparison.Ordinal);
    }

    // The real session persists it in the record, and every LATER record write carries it forward.
    // Mutation: the record builder drops the document.
    [Fact]
    public void TheRecordCarriesTheDocumentAcrossTheUserVisibleAndTheDetachedWrites()
    {
        var rig = new SessionRig();
        _ = rig.Session.GetOrStartOwned();
        var document = new EtabsDocumentRecord(Path.GetFullPath(SaveAndExitRig.Edb), SaveAndExitRig.FileId);

        Assert.True(rig.Session.RecordOpenedDocument(document).Success);

        Assert.Equal(document, rig.L.Records.Record!.Document);
        Assert.Equal(document, rig.Session.ReadRecordedDocument());
        var launch = (EtabsLaunchState)rig.Field("_launch")!;
        Assert.True(launch.WriteUserVisibleRecord(Kit.Identity));
        Assert.Equal(document, rig.L.Records.Record!.Document);
        Assert.True(rig.L.Records.Record.UserVisible);

        Assert.Equal(DetachOutcome.Detached, rig.Session.Detach().Outcome);
        var detached = rig.L.Records.Writes.Last(write => write.Phase == ManagedEtabsRecordPhase.Detached);
        Assert.Equal(document, detached.Document);
    }

    [Fact]
    public void ARecordWriteThatThrowsLeavesNoDocumentAndTheNextWriteDoesNotInventOne()
    {
        var rig = new SessionRig();
        _ = rig.Session.GetOrStartOwned();
        rig.L.Records.WriteException = new IOException("disk full");

        var failed = rig.Session.RecordOpenedDocument(
            new EtabsDocumentRecord(Path.GetFullPath(SaveAndExitRig.Edb), SaveAndExitRig.FileId));
        rig.L.Records.WriteException = null;

        Assert.False(failed.Success);
        Assert.Contains(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, failed.Error, StringComparison.Ordinal);
        Assert.Null(rig.Session.ReadRecordedDocument());
        var launch = (EtabsLaunchState)rig.Field("_launch")!;
        Assert.True(launch.WriteUserVisibleRecord(Kit.Identity));
        Assert.Null(rig.L.Records.Record!.Document);
    }

    [Fact]
    public void TheDocumentRoundTripsThroughTheRealJsonRecordStoreAsPathAndFileId()
    {
        var root = Path.Combine(Path.GetTempPath(), "c11-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            using var store = new JsonSessionRecordStore(Path.Combine(root, "record.json"), trustedAnchor: root);
            store.AcquireExclusive();
            var record = new ManagedEtabsSessionRecord(
                1,
                Kit.Identity.Pid,
                Kit.Identity.ProcessStartTimeUtc,
                Kit.Identity.ExecutablePath,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow)
            {
                Phase = ManagedEtabsRecordPhase.Ready,
                Document = new EtabsDocumentRecord(@"D:\m\model.edb", SaveAndExitRig.FileId)
            };

            store.Write(record);
            var text = File.ReadAllText(Path.Combine(root, "record.json"));
            var read = store.Read();

            Assert.Contains("\"document\"", text, StringComparison.Ordinal);
            Assert.Contains("\"path\"", text, StringComparison.Ordinal);
            Assert.Contains("\"fileId\"", text, StringComparison.Ordinal);
            Assert.Equal(record.Document, read!.Document);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // An older record (no document) still reads.
    [Fact]
    public void ARecordWithoutADocumentOmitsTheFieldAndStillReads()
    {
        var root = Path.Combine(Path.GetTempPath(), "c11-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            using var store = new JsonSessionRecordStore(Path.Combine(root, "record.json"), trustedAnchor: root);
            store.AcquireExclusive();
            store.Write(new ManagedEtabsSessionRecord(
                1, Kit.Identity.Pid, Kit.Identity.ProcessStartTimeUtc, Kit.Identity.ExecutablePath, Guid.NewGuid(), DateTimeOffset.UtcNow));

            Assert.DoesNotContain("document", File.ReadAllText(Path.Combine(root, "record.json")), StringComparison.Ordinal);
            Assert.Null(store.Read()!.Document);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Card v2.5a A3: the narrowed startup-blocker rule, and the main window captured by it.</summary>
public sealed class StartupBlockerRuleTests
{
    private static readonly ManagedProcessIdentity Identity = Kit.Identity;
    private const string WinForms = "WindowsForms10.Window.8.app.0.1f550a4_r3_ad1";

    private static StartupWindow MainWindow(nint handle = 0x200) => new(handle, "ETABS  ", WinForms);

    private static StartupWindow License(nint handle = 0x300, nint owner = 0) =>
        new(handle, " License Message", WinForms, owner);

    // a_license_message_window_is_a_start_blocker. Mutation: licence title not recognised.
    [Theory]
    [InlineData(" License Message", WinForms)] // the live title keeps a leading space
    [InlineData("License Message", WinForms)]
    [InlineData("AuthenticationView", WinForms)]
    [InlineData("Sign in", "AuthenticationView")]
    public void ALicenseMessageOrAuthenticationWindowIsAStartBlockerWhateverTheMainWindowIs(string title, string windowClass)
    {
        var window = new StartupWindow(0x300, title, windowClass);

        foreach (var main in new[] { nint.Zero, (nint)0x200 })
        {
            var assessment = EtabsStartupBlockerRule.Assess(
                [window, MainWindow()],
                main,
                _ => true,
                _ => true);

            Assert.Same(window, assessment.Blocker);
        }
    }

    // A licence window present at DISCOVERY (no main window yet, the splash up): START_BLOCKED
    // at once, before any attach. Mutation: trust Process.MainWindowHandle / wait for main.
    [Fact]
    public void ALicenseWindowPresentAtDiscoveryIsStartBlockedAtOnceBeforeAnyAttach()
    {
        var rig = new LauncherRig();
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x500, string.Empty, WinForms, Owner: 0x999)));
        rig.Windows.Windows.Add((Identity.Pid, License()));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.Contains("License Message", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.Attach.AttachCalls);
    }

    // "At once": before ANY main window exists the launcher does not wait out the deadline; it
    // blocks on the first look. Mutation: only look for a blocker once a main window exists.
    [Fact]
    public void ALicenseWindowSeenBeforeAnyMainWindowExistsBlocksOnTheFirstLookWithoutWaiting()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { NoWindowPolls = int.MaxValue });
        rig.Windows.Windows.Add((Identity.Pid, License()));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.Empty(rig.Clock.Waits);
        Assert.Equal(0, rig.Attach.AttachCalls);
    }

    // main_is_identified_by_the_attach_hwnd: the main window is captured by the rule from the
    // visible windows - NOT Process.MainWindowHandle, which the licence dialog became on a live
    // run. Mutation: trust Process.MainWindowHandle.
    [Fact]
    public void MainIsIdentifiedByTheCapturedWindowNotByProcessMainWindowHandle()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { MainWindow = 0x100 });
        rig.Windows.Windows.Add((Identity.Pid, MainWindow(0x200)));
        var state = rig.NewState();

        _ = rig.Launcher.Launch(state);

        Assert.Equal(0x200, (int)state.MainWindow);
    }

    [Fact]
    public void WithNoCapturableMainWindowMainStaysUnknownNotTheProcessMainWindowHandle()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { MainWindow = 0x100 });
        var state = rig.NewState();

        _ = rig.Launcher.Launch(state);

        Assert.Equal(nint.Zero, state.MainWindow);
    }

    [Fact]
    public void TheFirstVisibleOwnerlessEtabsTitledNonLicenseWindowIsCapturedAsMain()
    {
        var splash = new StartupWindow(0x100, string.Empty, WinForms, Owner: 0x999);
        var owned = new StartupWindow(0x110, "ETABS progress", WinForms, Owner: 0x200);
        var other = new StartupWindow(0x120, "Something else", WinForms);
        var first = MainWindow(0x200);
        var second = MainWindow(0x210);

        var assessment = EtabsStartupBlockerRule.Assess(
            [splash, owned, other, first, second],
            nint.Zero,
            _ => true,
            _ => true);

        Assert.Equal(0x200, (int)assessment.Main);
        Assert.Null(assessment.Blocker);
    }

    // The splash (visible, untitled, owned by someone other than main) is NOT a blocker while
    // main is unknown. Mutation: the old "any visible non-main window blocks" rule.
    [Fact]
    public void TheUntitledSplashBeforeMainExistsIsNotABlocker()
    {
        var rig = new LauncherRig();
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x500, string.Empty, WinForms, Owner: 0x999)));

        var launched = rig.Launcher.Launch(rig.NewState());

        Assert.NotNull(launched);
    }

    // Until main is captured it is UNKNOWN, not "disabled": no disabled-main blocker can arise.
    [Fact]
    public void AnUnknownMainWindowIsNeverTreatedAsDisabled()
    {
        var splash = new StartupWindow(0x500, string.Empty, WinForms, Owner: 0x999);

        var assessment = EtabsStartupBlockerRule.Assess([splash], nint.Zero, _ => true, _ => false);

        Assert.Equal(nint.Zero, assessment.Main);
        Assert.Null(assessment.Blocker);
    }

    // (ii) main KNOWN and disabled AND a visible non-main window.
    [Fact]
    public void AKnownDisabledMainWithAnotherVisibleWindowIsABlockerAtLaunch()
    {
        var rig = new LauncherRig();
        rig.Windows.MainEnabled = false;
        rig.Windows.Windows.Add((Identity.Pid, MainWindow()));
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x400, "Opening", WinForms, Owner: 0x200)));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.Contains("Opening", error.Message, StringComparison.Ordinal);
    }

    // A visible OWNED window while main is ENABLED is not a blocker (modeless or progress).
    // Mutation: the owned-window clause of the old rule.
    [Fact]
    public void AVisibleOwnedWindowWhileMainIsEnabledIsNotABlockerAtLaunchEither()
    {
        var rig = new LauncherRig();
        rig.Windows.MainEnabled = true;
        rig.Windows.Windows.Add((Identity.Pid, MainWindow()));
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x400, "Opening", WinForms, Owner: 0x200)));

        var launched = rig.Launcher.Launch(rig.NewState());

        Assert.NotNull(launched);
    }

    [Fact]
    public void ADisabledMainWithNoOtherVisibleWindowIsNotABlocker()
    {
        var assessment = EtabsStartupBlockerRule.Assess([MainWindow()], 0x200, _ => true, _ => false);

        Assert.Null(assessment.Blocker);
        Assert.Equal(0x200, (int)assessment.Main);
    }

    // The captured main is validated on every use: a stale handle is forgotten (never trusted as
    // "disabled"), and a fresh one is captured. Mutation: skip the validation.
    [Fact]
    public void AStaleMainWindowIsForgottenAndNeverTreatedAsADisabledMain()
    {
        var popup = new StartupWindow(0x400, "Popup", WinForms);

        var forgotten = EtabsStartupBlockerRule.Assess([popup], 0x100, _ => false, _ => false);
        var recaptured = EtabsStartupBlockerRule.Assess([popup, MainWindow(0x210)], 0x100, handle => handle != 0x100, _ => true);

        Assert.Equal(nint.Zero, forgotten.Main);
        Assert.Null(forgotten.Blocker);
        Assert.Equal(0x210, (int)recaptured.Main);
    }

    [Fact]
    public void TheWin32SourceTreatsAZeroOrDeadHandleAsNotLive()
    {
        var source = new Win32EtabsDialogWindowSource();

        Assert.False(source.IsLiveWindowOf(nint.Zero, Environment.ProcessId));
        Assert.False(source.IsLiveWindowOf(0x7FFF_FFF0, Environment.ProcessId));
    }

    // Post-Ready: the same rule runs under the watchdog, separate from the modal rule, and the
    // reveal still disarms it (the existing reveal tests keep that).
    [Fact]
    public void ALicenseWindowAfterReadyBlocksEvenWhileMainIsNotYetCaptured()
    {
        var cell = new LaunchPublicationCell();
        var state = new EtabsLaunchState(new MemoryRecordStore(), cell);
        state.Publish(Identity, lease: null);
        state.ArmStartupBlocker();
        var windows = new FakeStartupWindows { MainEnabled = true };
        windows.Windows.Add((Identity.Pid, License()));
        var watchdog = new EtabsModalDialogWatchdog(
            new NoDialogsSource(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(20),
            cell,
            windows);

        var result = Assert.IsType<Result>(
            watchdog.GuardAsync(
                () => Identity.Pid,
                () => Task.FromResult<object>(Result.Ok()),
                CancellationToken.None).Within("the guarded command"),
            exactMatch: false);

        Assert.False(result.Success);
        Assert.StartsWith($"[{EtabsLaunchErrorCodes.StartBlocked}]", result.Error, StringComparison.Ordinal);
        Assert.Contains("License Message", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePostReadyWatchCapturesMainFromTheVisibleWindowsAndStoresItInTheCell()
    {
        var cell = new LaunchPublicationCell();
        var state = new EtabsLaunchState(new MemoryRecordStore(), cell);
        state.Publish(Identity, lease: null);
        state.ArmStartupBlocker();
        var windows = new FakeStartupWindows { MainEnabled = true };
        windows.Windows.Add((Identity.Pid, MainWindow(0x210)));
        var watchdog = new EtabsModalDialogWatchdog(
            new NoDialogsSource(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(20),
            cell,
            windows);

        var result = watchdog.GuardAsync(
            () => Identity.Pid,
            () => Task.FromResult<object>(Result.Ok()),
            CancellationToken.None).Within("the guarded command");

        Assert.True(Assert.IsType<Result>(result, exactMatch: false).Success);
        Assert.Equal(0x210, (int)cell.MainWindow);
    }

    private sealed class NoDialogsSource : IEtabsDialogWindowSource
    {
        public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid) => [];

        public bool TryDismiss(EtabsDialogWindow dialog) => false;
    }
}
