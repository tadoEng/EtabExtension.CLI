using System.Text.Json;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

internal sealed class EventedFileApi(List<string> events) : IEtabsModelFileApi
{
    public string? CurrentPath { get; set; }

    public ChangeSignalReading Signal { get; set; } = ChangeSignalReading.Unsupported;

    public Exception? SignalFailure { get; set; }

    public int SaveCalls { get; private set; }

    public string? GetModelFilename()
    {
        events.Add("GetModelFilename");
        return CurrentPath;
    }

    public int SaveFile(string filePath)
    {
        SaveCalls++;
        events.Add("SaveFile");
        return 0;
    }

    public int OpenFile(string filePath) => throw new InvalidOperationException("never opens");

    public ChangeSignalReading ReadChangeSignal()
    {
        events.Add("ReadChangeSignal");
        return SignalFailure is null ? Signal : throw SignalFailure;
    }
}

/// <summary>An api that does not override the signal: it must inherit the "unsupported" default.</summary>
internal sealed class EventedFileApiWithoutSignal : IEtabsModelFileApi
{
    public string? GetModelFilename() => null;

    public int SaveFile(string filePath) => 0;

    public int OpenFile(string filePath) => 0;
}

internal sealed class FixedStamps(SavedFileStamp? stamp) : ISavedFileStampReader
{
    public SavedFileStamp? Current { get; set; } = stamp;

    public SavedFileStamp? Read(string path) => Current;
}

internal sealed class FixedApiFactory(IEtabsModelFileApi api) : IShutdownIfUnchangedApiFactory
{
    public IEtabsModelFileApi Create(IEtabsSession session) => api;
}

/// <summary>Cards v2.3 §2 / v2.4 §1: the saved stamp, and its change-signal baseline.</summary>
public sealed class SavedStampTests
{
    private const string Working = @"D:\Project\.etabs-ext\main\working\model.edb";

    private static (Result<SaveModelData> Result, List<string> Events, EventedFileApi Api) Save(
        ChangeSignalReading signal,
        ISavedFileStampReader? stamps = null,
        string? expectedFileId = null,
        Exception? signalFailure = null)
    {
        var events = new List<string>();
        var api = new EventedFileApi(events) { CurrentPath = Working, Signal = signal, SignalFailure = signalFailure };
        var result = SaveModelService.SaveOnApi(
            api,
            Working,
            (_, _) => new FileIdentityResult(FileIdentityMatch.Different, 0),
            stamps ?? new ChangingFileStamps(),
            expectedFileId);
        return (result, events, api);
    }

    [Fact]
    public void ACleanSignalRightAfterTheSaveGivesAVerifiedStampWithTheSavedFilesIdentity()
    {
        var (result, _, _) = Save(new ChangeSignalReading(ChangeSignalState.Clean, "dirty-flag"));

        Assert.True(result.Success, result.Error);
        var stamp = result.Data!.SavedStamp!;
        Assert.True(stamp.Verified);
        Assert.Equal("clean", stamp.ChangeSignal);
        Assert.Equal("0000abcd-0000000000001234", stamp.FileId);
        Assert.Equal(4096, stamp.Size);
    }

    // The flag is read IN THE SAME CALL, immediately after SaveFile returned 0 (card v2.4 §1):
    // before the model path is re-read.
    [Fact]
    public void TheSignalIsReadImmediatelyAfterSaveFileAndBeforeAnythingElse()
    {
        var (_, events, _) = Save(new ChangeSignalReading(ChangeSignalState.Clean));

        var save = events.IndexOf("SaveFile");
        Assert.Equal("ReadChangeSignal", events[save + 1]);
        Assert.True(events.LastIndexOf("GetModelFilename") > save + 1);
    }

    // a_dirty_baseline_selects_fallback / an_incomplete_signal_selects_fallback
    [Theory]
    [InlineData(ChangeSignalState.Dirty, "dirty")]
    [InlineData(ChangeSignalState.Incomplete, "incomplete")]
    [InlineData(ChangeSignalState.Unsupported, "unsupported")]
    public void ADirtyIncompleteOrUnsupportedSignalLeavesTheStampUnverified(ChangeSignalState state, string name)
    {
        var (result, _, _) = Save(new ChangeSignalReading(state));

        Assert.True(result.Success, "an unverified stamp is still a successful save");
        Assert.False(result.Data!.SavedStamp!.Verified);
        Assert.Equal(name, result.Data.SavedStamp.ChangeSignal);
    }

    [Fact]
    public void ASignalThatThrowsIsAnUnverifiedStampNotAFailedSave()
    {
        var (result, _, _) = Save(
            new ChangeSignalReading(ChangeSignalState.Clean),
            signalFailure: new InvalidOperationException("COM"));

        Assert.True(result.Success, result.Error);
        Assert.False(result.Data!.SavedStamp!.Verified);
        Assert.Equal("incomplete", result.Data.SavedStamp.ChangeSignal);
    }

    [Fact]
    public void TheDefaultProductionSourceReportsUnsupportedSoEveryStampIsUnverified()
    {
        Assert.Equal(
            ChangeSignalState.Unsupported,
            new UnsupportedChangeSignalSource().Read(null!).State);
        Assert.Equal(ChangeSignalState.Unsupported, ((IEtabsModelFileApi)new EventedFileApiWithoutSignal()).ReadChangeSignal().State);
    }

    [Fact]
    public void ASaveThatLeftTheFileOnDiskUnchangedIsNotReportedAsASave()
    {
        var same = new SavedFileStamp("0000abcd-0000000000001234", 4096, new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));

        var (result, _, _) = Save(new ChangeSignalReading(ChangeSignalState.Clean), new FixedStamps(same));

        Assert.False(result.Success);
        Assert.Contains(SaveModelService.SaveNotObservedCode, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExpectedDocumentThatIsNotTheRequestedFileRefusesBeforeAnySave()
    {
        var present = new FixedStamps(new SavedFileStamp("0000abcd-0000000000001234", 1, DateTimeOffset.UtcNow));

        var (refused, _, api) = Save(new ChangeSignalReading(ChangeSignalState.Clean), present, expectedFileId: "0000ffff-0000000000000001");
        var (accepted, _, _) = Save(new ChangeSignalReading(ChangeSignalState.Clean), new ChangingFileStamps(), expectedFileId: null);

        Assert.False(refused.Success);
        Assert.Contains(SaveModelService.DocumentMismatchCode, refused.Error, StringComparison.Ordinal);
        Assert.Equal(0, api.SaveCalls);
        Assert.True(accepted.Success);
    }
}

/// <summary>Cards v2.3 §2 and v2.4 §2, §3: shutdown-if-unchanged (C10) and its abandonment fence.</summary>
public sealed class ShutdownIfUnchangedTests
{
    private const string Working = @"D:\Project\.etabs-ext\main\working\model.edb";

    private static readonly DateTimeOffset Saved = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly SavedStamp Verified = new("0000abcd-0000000000001234", 4096, Saved, "clean", true);

    private static readonly SavedStamp Unverified = Verified with { ChangeSignal = "unsupported", Verified = false };

    private sealed class Rig
    {
        public Rig(TimeSpan? budget = null)
        {
            Api = new EventedFileApi([]) { CurrentPath = Working, Signal = new ChangeSignalReading(ChangeSignalState.Clean) };
            Stamps = new FixedStamps(new SavedFileStamp(Verified.FileId, Verified.Size, Verified.Mtime));
            Service = new ShutdownIfUnchangedService(
                Session,
                Watchdog,
                new FixedApiFactory(Api),
                Registry,
                Stamps,
                budget ?? TimeSpan.FromSeconds(10),
                (_, _) => new FileIdentityResult(FileIdentityMatch.Same, 0));
        }

        public ProbeSession Session { get; } = new();

        public ProbeWatchdog Watchdog { get; } = new();

        public EventedFileApi Api { get; }

        public FixedStamps Stamps { get; }

        public ShutdownIfUnchangedRegistry Registry { get; } = new();

        public ShutdownIfUnchangedService Service { get; }

        public Task<object> Run(ShutdownIfUnchangedRequest request, long id = 1) =>
            Service.RunAsync(request, id, check => Task.Run(check));
    }

    private static ShutdownIfUnchangedRequest Request(SavedStamp? stamp = null, bool discard = false) => new()
    {
        FilePath = Working,
        SavedStamp = stamp ?? Verified,
        DiscardConfirmed = discard
    };

    private static string Error(object result) => Assert.IsType<Result>(result, exactMatch: false).Error ?? string.Empty;

    // An unchanged model exits once, through the session's ordinary exit.
    [Fact]
    public void AnUnchangedModelExitsOnceAndAnswersTheShutdownData()
    {
        var rig = new Rig();

        var result = rig.Run(Request()).Within("the command");

        var typed = Assert.IsType<Result<ManagedEtabsShutdownData>>(result);
        Assert.True(typed.Success, typed.Error);
        Assert.Equal(1, rig.Session.NormalShutdowns);
        Assert.Equal(0, rig.Session.BlockedShutdowns);
    }

    // Test 60': every edit category flips the dirty flag, and every other kind of movement
    // since the save blocks the exit too. Mutation: exiting without the re-read.
    [Theory]
    [InlineData("geometry")]
    [InlineData("assignment")]
    [InlineData("load assignment")]
    [InlineData("definition")]
    [InlineData("analysis setting")]
    public void AnEditAfterTheSaveInAnyCategoryIsDetectedAndBlocksTheExit(string category)
    {
        var rig = new Rig();
        rig.Api.Signal = new ChangeSignalReading(ChangeSignalState.Dirty, category);

        var result = rig.Run(Request()).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.ChangedSinceSave, Error(result), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
        Assert.Equal(0, rig.Session.BlockedShutdowns);
    }

    [Fact]
    public void AMovedFileOrADifferentDocumentBlocksTheExit()
    {
        foreach (var mutate in new Action<Rig>[]
        {
            rig => rig.Stamps.Current = new SavedFileStamp(Verified.FileId, Verified.Size + 1, Verified.Mtime),
            rig => rig.Stamps.Current = new SavedFileStamp(Verified.FileId, Verified.Size, Verified.Mtime.AddSeconds(1)),
            rig => rig.Stamps.Current = new SavedFileStamp("0000abcd-00000000000099ff", Verified.Size, Verified.Mtime),
            rig => rig.Stamps.Current = null,
            rig => rig.Api.CurrentPath = @"D:\Project\.etabs-ext\main\working\other.edb"
        })
        {
            var rig = new Rig();
            mutate(rig);

            var result = rig.Run(Request()).Within("the command");

            Assert.Contains(ShutdownIfUnchangedCodes.ChangedSinceSave, Error(result), StringComparison.Ordinal);
            Assert.Equal(0, rig.Session.NormalShutdowns);
        }
    }

    [Fact]
    public void AnExpectedDocumentThatIsNotTheSavedFileBlocksTheExit()
    {
        var rig = new Rig();
        var request = new ShutdownIfUnchangedRequest
        {
            FilePath = Working,
            SavedStamp = Verified,
            ExpectedDocument = new ExpectedDocument("0000ffff-0000000000000001")
        };

        var result = rig.Run(request).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.ChangedSinceSave, Error(result), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
    }

    // Inconclusive evidence is never "unchanged".
    [Theory]
    [InlineData(ChangeSignalState.Unsupported)]
    [InlineData(ChangeSignalState.Incomplete)]
    public void AnUnsupportedOrIncompleteSignalAtTheRecheckIsInconclusiveAndNeverExits(ChangeSignalState state)
    {
        var rig = new Rig();
        rig.Api.Signal = new ChangeSignalReading(state);

        var result = rig.Run(Request()).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.CheckInconclusive, Error(result), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
    }

    [Fact]
    public void AComErrorDuringTheRecheckIsNonDestructive()
    {
        var rig = new Rig();
        rig.Api.SignalFailure = new InvalidOperationException("RPC_E_DISCONNECTED");

        var result = rig.Run(Request()).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.CheckInconclusive, Error(result), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
        Assert.Equal(0, rig.Session.BlockedShutdowns);
    }

    [Fact]
    public void AnOpenModalRefusesWithoutExitAndAnInconclusiveScanNeverAnswersSuccess()
    {
        var modal = new Rig();
        modal.Watchdog.Check = new EtabsDialogCheck(
            EtabsDialogCheckKind.Modal,
            new EtabsModalDialogObservation("Define Load Patterns", "Names...", false),
            null);
        var scan = new Rig();
        scan.Watchdog.Check = new EtabsDialogCheck(EtabsDialogCheckKind.Inconclusive, null, "scan timed out");

        var modalResult = modal.Run(Request()).Within("the command");
        var scanResult = scan.Run(Request()).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.ModalOpen, Error(modalResult), StringComparison.Ordinal);
        Assert.Contains("Define Load Patterns", Error(modalResult), StringComparison.Ordinal);
        Assert.Contains(EtabsTypedFailureCodes.DialogScanInconclusive, Error(scanResult), StringComparison.Ordinal);
        Assert.Equal(0, modal.Session.NormalShutdowns + scan.Session.NormalShutdowns);
    }

    // Card v2.4 §3 (daemon part of 62'): an unverified stamp gives NO unchanged assurance, so the
    // exit needs the caller's explicit discard confirmation - and without it nothing is exited.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnverifiedStampExitsOnlyAfterAnExplicitDiscardConfirmation(bool discardConfirmed)
    {
        var rig = new Rig();
        rig.Api.Signal = new ChangeSignalReading(ChangeSignalState.Dirty);

        var result = rig.Run(Request(Unverified, discardConfirmed)).Within("the command");

        if (discardConfirmed)
        {
            Assert.IsType<Result<ManagedEtabsShutdownData>>(result);
            Assert.Equal(1, rig.Session.NormalShutdowns);
        }
        else
        {
            Assert.Contains(ShutdownIfUnchangedCodes.DiscardNotConfirmed, Error(result), StringComparison.Ordinal);
            Assert.Equal(0, rig.Session.NormalShutdowns);
        }
    }

    [Fact]
    public void AMissingStampIsTheDiscardFallbackToo()
    {
        var rig = new Rig();
        var request = new ShutdownIfUnchangedRequest { FilePath = Working };

        var result = rig.Run(request).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.DiscardNotConfirmed, Error(result), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
    }

    // c10_pause_before_check_then_timeout_cancel_or_detach_never_exits, and
    // c7a_kill_fallback_never_runs_before_c10_exiting: before Exiting nothing is exited AND
    // nothing is killed, whichever rival takes the arbiter.
    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("detach")]
    public void APauseBeforeTheCheckThenATimeoutCancelOrDetachNeverExitsOrKills(string rival)
    {
        var rig = new Rig(budget: rival == "timeout" ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(10));
        using var hold = new ManualResetEventSlim(false);
        using var paused = new ManualResetEventSlim(false);
        rig.Service.BeforeCheck = () =>
        {
            paused.Set();
            hold.Signalled("the test to release the paused check");
        };
        var running = rig.Run(Request(), id: 9);
        paused.Signalled("the check to pause");

        switch (rival)
        {
            case "timeout":
                Assert.Contains(ShutdownIfUnchangedCodes.CheckTimeout, Error(running.Within("the timeout answer")), StringComparison.Ordinal);
                break;
            case "cancel":
                Assert.Equal(ShutdownIfUnchangedState.Abandoned, rig.Registry.Cancel(9));
                break;
            default:
                Assert.True(rig.Registry.AbandonCurrent());
                break;
        }

        hold.Set();
        var late = running.Within("the late continuation");

        if (rival != "timeout")
        {
            Assert.Contains(ShutdownIfUnchangedCodes.Abandoned, Error(late), StringComparison.Ordinal);
        }

        Assert.Equal(0, rig.Session.NormalShutdowns);
        Assert.Equal(0, rig.Session.BlockedShutdowns);
        Assert.False(rig.Registry.Begin(10) is null, "the abandoned operation no longer blocks the next one");
    }

    // c10_pause_after_check_before_exit_cas_then_cancel_never_exits
    [Fact]
    public void APauseAfterTheCheckBeforeTheExitCasThenACancelNeverExits()
    {
        var rig = new Rig();
        rig.Service.AfterCheckBeforeExitCas = () => Assert.Equal(ShutdownIfUnchangedState.Abandoned, rig.Registry.Cancel(1));

        var result = rig.Run(Request()).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.Abandoned, Error(result), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
        Assert.Equal(0, rig.Session.BlockedShutdowns);
    }

    // c10_exit_cas_won_then_timeout_follows_c7a_exit_rules: once Exiting is won, the Checking
    // budget cannot abandon it - the exit runs to its own answer.
    [Fact]
    public void OnceTheExitCasIsWonATimeoutNeverAbandonsItAndTheExitRunsToItsAnswer()
    {
        var rig = new Rig(budget: TimeSpan.FromMilliseconds(1500));
        using var exitStarted = new ManualResetEventSlim(false);
        using var finishExit = new ManualResetEventSlim(false);
        rig.Session.NormalShutdown = () =>
        {
            exitStarted.Set();
            finishExit.Signalled("the test to finish the slow exit");
            return Shutdowns.Ok();
        };

        var running = rig.Run(Request());
        exitStarted.Signalled("the exit to start");
        Thread.Sleep(2000); // well past the 1.5 s Checking budget

        Assert.False(running.IsCompleted, "a won exit is not answered as a Checking timeout");
        Assert.False(rig.Registry.AbandonCurrent(), "an exiting operation cannot be abandoned");
        finishExit.Set();
        var result = running.Within("the exit's own answer");

        Assert.IsType<Result<ManagedEtabsShutdownData>>(result);
        Assert.Equal(1, rig.Session.NormalShutdowns);
    }

    [Fact]
    public void ASecondCommandWhileOneIsInFlightIsBusyAndExitsNothing()
    {
        var rig = new Rig();
        _ = rig.Registry.Begin(1);

        var result = rig.Run(Request(), id: 2).Within("the command");

        Assert.Contains(ShutdownIfUnchangedCodes.Busy, Error(result), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
    }

    // The dispatcher wires it: save-model returns the stamp, and cancel-operation names the request.
    [Fact]
    public void TheDispatcherRoutesTheCommandAndCancelOperationNamesTheRequestId()
    {
        var rig = new Rig();
        using var hold = new ManualResetEventSlim(false);
        using var paused = new ManualResetEventSlim(false);
        rig.Service.BeforeCheck = () =>
        {
            paused.Set();
            hold.Signalled("the test to release the check");
        };
        var dispatcher = DispatcherRig.Create(rig.Session, rig.Watchdog, rig.Registry, rig.Service);
        var payload = JsonSerializer.SerializeToElement(new { filePath = Working, savedStamp = Verified });

        Task<object> command;
        using (ServeRequestContext.Enter(new ServeRequestInfo(31, "shutdown-if-unchanged")))
        {
            command = dispatcher.DispatchAsync("shutdown-if-unchanged", payload, CancellationToken.None);
        }

        paused.Signalled("the check to pause");
        var cancelled = dispatcher.DispatchAsync(
            "cancel-operation",
            JsonSerializer.SerializeToElement(new { requestId = 31 }),
            CancellationToken.None).Within("cancel-operation");
        hold.Set();
        var answer = command.Within("the abandoned command");

        Assert.True(Assert.IsType<Result<CancelOperationData>>(cancelled).Success);
        Assert.Contains(ShutdownIfUnchangedCodes.Abandoned, Error(answer), StringComparison.Ordinal);
        Assert.Equal(0, rig.Session.NormalShutdowns);
        Assert.Contains("shutdown-if-unchanged", dispatcher.Capabilities);
    }
}
