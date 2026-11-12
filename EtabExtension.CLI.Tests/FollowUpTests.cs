using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>#747 review items P2-A, P3-F, P3-G (and the card's C4 detail fields).</summary>
public sealed class StartBlockerFollowUpTests
{
    private static readonly ManagedProcessIdentity Identity = Kit.Identity;
    private const nint Main = 0x100;

    private sealed class NoDialogs : IEtabsDialogWindowSource
    {
        public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid) => [];

        public bool TryDismiss(EtabsDialogWindow dialog) => false;
    }

    private static (EtabsModalDialogWatchdog Watchdog, LaunchPublicationCell Cell, FakeStartupWindows Windows) Build(
        bool mainEnabled)
    {
        var cell = new LaunchPublicationCell();
        var state = new EtabsLaunchState(new MemoryRecordStore(), cell);
        state.Publish(Identity, lease: null);
        state.SetMainWindow(Main);
        state.ArmStartupBlocker();
        var windows = new FakeStartupWindows { MainEnabled = mainEnabled };
        windows.Windows.Add((Identity.Pid, new StartupWindow(Main, "ETABS", "HwndWrapper")));
        var watchdog = new EtabsModalDialogWatchdog(
            new NoDialogs(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(2),
            cell,
            windows);
        return (watchdog, cell, windows);
    }

    private static Result Guarded(EtabsModalDialogWatchdog watchdog) => Assert.IsType<Result>(
        watchdog.GuardAsync(
            () => Identity.Pid,
            () => Task.FromResult<object>(Result.Ok()),
            CancellationToken.None).Within("the guarded command"),
        exactMatch: false);

    // P2-A: an owned popup of an ENABLED main window (a progress form, a floating tool window)
    // is not a blocker. Mutation: the old rule (any visible non-main window).
    [Fact]
    public void AnOwnedPopupOfAnEnabledMainWindowIsNotABlocker()
    {
        var (watchdog, cell, windows) = Build(mainEnabled: true);
        windows.Windows.Add((Identity.Pid, new StartupWindow(0x300, "Opening model", "ProgressForm", Owner: Main)));
        windows.Windows.Add((Identity.Pid, new StartupWindow(0x301, "Tool window", "ToolWindow")));

        var result = Guarded(watchdog);

        Assert.True(result.Success, result.Error);
        Assert.False(cell.StartupBlockerArmed, "a clean first command disarms the watch");
    }

    [Theory]
    [InlineData(true, 0, false)] // ownerless, main enabled: nothing is held up
    [InlineData(false, 0, true)] // ownerless, main disabled: a blocker
    [InlineData(false, 0x100, false)] // owned BY main (its own child dialog): not a blocker
    [InlineData(false, 0x777, true)] // owned by some other window, main disabled: a blocker
    public void TheBlockerRuleIsOwnerlessOrNotOwnedByMainAndOnlyWhileMainIsDisabled(
        bool mainEnabled,
        int owner,
        bool expectBlocker)
    {
        var window = new StartupWindow(0x200, "Sign in", "AuthenticationView", (nint)owner);

        Assert.Equal(expectBlocker, EtabsPostReadyBlockerRule.IsBlocker(window, Main, mainEnabled));

        var (watchdog, _, windows) = Build(mainEnabled);
        windows.Windows.Add((Identity.Pid, window));
        var result = Guarded(watchdog);
        Assert.Equal(!expectBlocker, result.Success);
        if (expectBlocker)
        {
            Assert.StartsWith($"[{EtabsLaunchErrorCodes.StartBlocked}]", result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheMainWindowItselfIsNeverABlocker()
    {
        Assert.False(EtabsPostReadyBlockerRule.IsBlocker(
            new StartupWindow(Main, "ETABS", "HwndWrapper"),
            Main,
            mainWindowEnabled: false));
    }

    // P3-G: the post-Ready failure says so; the launch-phase one says "launch" and reports its cleanup.
    [Fact]
    public void ThePostReadyBlockerSaysPostReadyAndTheLaunchPhaseOneSaysLaunch()
    {
        var (watchdog, _, windows) = Build(mainEnabled: false);
        windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));
        var postReady = Detail(Guarded(watchdog).Error!);

        var rig = new LauncherRig();
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x100, "ETABS", "Main")));
        var launch = Detail(Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState())).Message);

        Assert.Equal("postReady", postReady["phase"]!.GetValue<string>());
        Assert.Null(postReady["ownedExitConfirmed"]);
        Assert.Equal("launch", launch["phase"]!.GetValue<string>());
        Assert.True(launch["ownedExitConfirmed"]!.GetValue<bool>());
    }

    private static JsonObject Detail(string message)
    {
        var at = message.IndexOf(" detail=", StringComparison.Ordinal);
        Assert.True(at >= 0, message);
        return JsonNode.Parse(message[(at + " detail=".Length)..])!.AsObject();
    }

    // C4: the launch-phase details carry ownedExitConfirmed and recordRetained.
    [Fact]
    public void AnUnconfirmedCleanupOfABlockedStartSaysSoInTheDetail()
    {
        var rig = new LauncherRig(
            process: new FakeSpawnedProcess(Identity) { RefusesToExit = true });
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x100, "ETABS", "Main")));

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));
        var detail = Detail(error.Message);

        Assert.Equal(EtabsLaunchErrorCodes.StartBlocked, error.Code);
        Assert.False(detail["ownedExitConfirmed"]!.GetValue<bool>());
        Assert.True(detail["recordRetained"]!.GetValue<bool>());
        Assert.Equal("Sign in", detail["title"]!.GetValue<string>());
        Assert.True(error.Message.TrimEnd().EndsWith('}'), "the detail runs to the end of the message");
    }

    [Fact]
    public void AStartTimeoutCarriesTheSameTwoDetailFields()
    {
        var rig = new LauncherRig(process: new FakeSpawnedProcess(Identity) { NoWindowPolls = int.MaxValue });

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));
        var detail = Detail(error.Message);

        Assert.Equal(EtabsLaunchErrorCodes.StartTimeout, error.Code);
        Assert.True(detail["ownedExitConfirmed"]!.GetValue<bool>());
        Assert.False(detail["recordRetained"]!.GetValue<bool>());
    }

    // P3-F: the terminator waits one second longer than the worker.
    [Fact]
    public void TheTerminatorWaitsOneSecondLongerThanTheWorkersKillWait()
    {
        Assert.Equal(TimeSpan.FromSeconds(11), ManagedEtabsLauncher.TerminatorExitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(11), new ManagedEtabsLauncherOptions().TerminatorExitTimeout);
        Assert.Equal(
            TimeSpan.FromMilliseconds(1300),
            new ManagedEtabsLauncherOptions { ForcedExitTimeout = TimeSpan.FromMilliseconds(300) }.TerminatorExitTimeout);

        // And the failed start really waits that long for an exit that never confirms.
        var rig = new LauncherRig(
            new ManagedEtabsLauncherOptions
            {
                ForcedExitTimeout = TimeSpan.FromMilliseconds(300),
                GracefulCleanupWait = TimeSpan.Zero
            },
            process: new FakeSpawnedProcess(Identity) { RefusesToExit = true, WaitSleeps = true });
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x200, "Sign in", "AuthenticationView")));
        rig.Windows.Windows.Add((Identity.Pid, new StartupWindow(0x100, "ETABS", "Main")));
        var clock = Stopwatch.StartNew();

        var error = Assert.Throws<EtabsLaunchException>(() => rig.Launcher.Launch(rig.NewState()));

        Assert.False(error.Cleanup!.Success);
        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(1200),
            $"the terminator gave up after {clock.Elapsed}, before ForcedExitTimeout + 1 s");
    }
}

/// <summary>#747 review P3-H: the trusted anchor is passed through to the record store.</summary>
public sealed class TrustedAnchorPassThroughTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "c747d-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var junction in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                    .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                    .ToList())
                {
                    Directory.Delete(junction, recursive: false);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public void ARecordRootOutsideTheDefaultAnchorIsAcceptedOnlyWhenItsAnchorIsPassedThrough()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(Path.Combine(real, "HostData"));
        var profile = Path.Combine(_root, "profile");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{profile}\" \"{real}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        var anchor = Path.Combine(profile, "HostData");
        var record = Path.Combine(anchor, "etabs-session", "editing", "record.json");

        using var anchored = ServeExtensions.CreateRecordStore(
            new ServeSessionPaths { SessionRecordPath = record, TrustedAnchor = anchor });
        anchored.AcquireExclusive();
        Assert.True(anchored.HoldsExclusiveLock);

        // The mutation: dropping the anchor makes the same layout unsafe.
        using var unanchored = ServeExtensions.CreateRecordStore(
            new ServeSessionPaths { SessionRecordPath = Path.Combine(anchor, "etabs-session", "analysis", "record.json") });
        Assert.Throws<RecordPathUnsafeException>(unanchored.AcquireExclusive);
    }
}

/// <summary>#747-D C5: the daemon's own facts in get-status.</summary>
public sealed class StatusFactsTests
{
    private sealed class DialogOn : IEtabsDialogWindowSource
    {
        public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid) =>
            [new EtabsDialogWindow(0x500, "Define Load Patterns", ["names"])];

        public bool TryDismiss(EtabsDialogWindow dialog) => false;
    }

    private sealed class BlockingSave : ISaveModelService
    {
        public TaskCompletionSource<Result<SaveModelData>> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<SaveModelData>> SaveModelAsync(string filePath) => Release.Task;

        public Task<Result<SaveModelData>> SaveModelOnAppAsync(ETABSApplication app, string filePath) => Release.Task;
    }

    private static GetStatusData Status(ServeDispatcher dispatcher)
    {
        var result = Assert.IsType<Result<GetStatusData>>(
            dispatcher.DispatchAsync("get-status", null, CancellationToken.None).Within("get-status"));
        Assert.True(result.Success, result.Error);
        return result.Data!;
    }

    [Fact]
    public void GetStatusReportsTheLaunchRecordIdTheDispositionAndAnEmptyAbandonedList()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var session = new ProbeSession { DispositionValue = EtabsDisposition.Owned, LaunchId = id };
        var dispatcher = DispatcherRig.Create(session, new ProbeWatchdog());

        var data = Status(dispatcher);

        Assert.Equal(id, data.LaunchRecordId);
        Assert.Equal(EtabsDisposition.Owned, data.Disposition);
        Assert.Empty(data.Abandoned!);
        Assert.Null(data.OrphanDisposition);
        var json = JsonSerializer.Serialize(data, ServeJson.Options);
        Assert.Contains("\"disposition\":\"owned\"", json, StringComparison.Ordinal);
        Assert.Contains("\"abandoned\":[]", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EtabsDisposition.Detaching, "detaching")]
    [InlineData(EtabsDisposition.Detached, "detached")]
    [InlineData(EtabsDisposition.None, "none")]
    public void TheDispositionIsOneOfOwnedDetachingDetachedNone(EtabsDisposition disposition, string wire)
    {
        var dispatcher = DispatcherRig.Create(
            new ProbeSession { DispositionValue = disposition },
            new ProbeWatchdog());

        var json = JsonSerializer.Serialize(Status(dispatcher), ServeJson.Options);

        Assert.Contains($"\"disposition\":\"{wire}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecoveryPassThatDetachedAnIdentityIsReportedAsOrphanDisposition()
    {
        var orphan = new OrphanDispositionCell { Detached = Kit.Identity };
        var dispatcher = DispatcherRig.Create(new ProbeSession(), new ProbeWatchdog(), orphan: orphan);

        var data = Status(dispatcher);

        Assert.Equal(Kit.Identity.Pid, data.OrphanDisposition!.Detached!.Pid);
        Assert.Equal(Kit.Identity.ExecutablePath, data.OrphanDisposition.Detached.ExePath);
    }

    // C5 abandoned: a command a modal dialog is holding is listed running, then finished.
    [Fact]
    public void AnAbandonedRequestIsListedRunningThenFinished()
    {
        var save = new BlockingSave();
        var session = new ProbeSession { Started = true };
        var watchdog = new EtabsModalDialogWatchdog(
            new DialogOn(),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(1));
        var dispatcher = DispatcherRig.Create(session, watchdog, save: save);

        Task<object> command;
        using (ServeRequestContext.Enter(new ServeRequestInfo(7, "save-model")))
        {
            command = dispatcher.DispatchAsync(
                "save-model",
                JsonSerializer.SerializeToElement(new { filePath = @"D:\p\model.edb" }),
                CancellationToken.None);
        }

        var failure = Assert.IsType<Result>(command.Within("the modal failure"), exactMatch: false);
        Assert.Contains(EtabsTypedFailureCodes.ModalDialog, failure.Error, StringComparison.Ordinal);

        var running = Status(dispatcher).Abandoned!;
        Assert.Equal(new AbandonedRequestInfo(7, "save-model", AbandonedRequestState.Running), Assert.Single(running));

        save.Release.SetResult(Result.Ok(new SaveModelData()));
        session.Started = false; // the status service itself is not under test: read the daemon's facts
        DetachTests.WaitFor(
            () => Status(dispatcher).Abandoned![0].State == AbandonedRequestState.Finished,
            "the abandoned work to finish");
    }

    // Test 47 (daemon part): a missing disposition is how an older daemon reads, and the daemon
    // itself always sends one - so "unknown" can only come from the older side.
    [Fact]
    public void TheDaemonAlwaysSendsADispositionSoAMissingOneIsOnlyEverAnOlderDaemon()
    {
        var dispatcher = DispatcherRig.Create(new ProbeSession(), new ProbeWatchdog());

        Assert.NotNull(Status(dispatcher).Disposition);
        Assert.NotNull(Status(dispatcher).Abandoned);
        Assert.Null(new GetStatusData().Disposition);
        Assert.Null(new GetStatusData().Abandoned);
    }
}

/// <summary>Card v2.3 §4 stage 2: <c>serve --recover</c> takes the role lock first, then recovers.</summary>
public sealed class RecoveryDaemonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "c747d-rec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private sealed class OrderedStore(List<string> events) : ISessionRecordStore
    {
        public ManagedEtabsSessionRecord? Record { get; set; }

        public Exception? LockFailure { get; set; }

        public string FilePath => @"C:\memory\record.json";

        public void AcquireExclusive() => AcquireExclusive(TimeSpan.Zero);

        public void AcquireExclusive(TimeSpan wait)
        {
            events.Add($"acquire:{wait.TotalSeconds:0.###}");
            if (LockFailure is not null)
            {
                throw LockFailure;
            }
        }

        public ManagedEtabsSessionRecord? Read()
        {
            events.Add("read-record");
            return Record;
        }

        public void Write(ManagedEtabsSessionRecord record) => events.Add("write-record");

        public void Clear()
        {
            events.Add("clear-record");
            Record = null;
        }
    }

    private sealed class OrderedInspector(List<string> events, ExactProcessTerminationState state) : IProcessInspector
    {
        public EtabsProcessObservation ObserveEtabs()
        {
            events.Add("process-census");
            return new([], 0);
        }

        public ManagedProcessIdentity? Find(int pid)
        {
            events.Add("process-find");
            return null;
        }

        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected)
        {
            events.Add("process-open");
            return null;
        }

        public ExactProcessTerminationResult TerminateExact(ManagedProcessIdentity expected, TimeSpan timeout)
        {
            events.Add("process-terminate");
            return new(state, expected);
        }
    }

    private static ManagedEtabsSessionRecord Orphan() => new(
        1,
        Kit.Identity.Pid,
        Kit.Identity.ProcessStartTimeUtc,
        Kit.Identity.ExecutablePath,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow);

    // Test 71: the lock is the FIRST action; the record is read and a process touched only after.
    [Fact]
    public void TheRecoveryDaemonTakesTheRoleLockBeforeReadingTheRecordOrTouchingAnyProcess()
    {
        var events = new List<string>();
        var store = new OrderedStore(events) { Record = Orphan() };
        var inspector = new OrderedInspector(events, ExactProcessTerminationState.ConfirmedGone);
        using var output = new StringWriter();

        var exit = ServeCommand
            .RunRecoveryLockAsync(store, ServeCommand.RecoveryLockWait, output, TextWriter.Null, Refusal(store))
            .Within("the lock");
        var recovered = new OrphanSessionCleaner(store, inspector).Clean();

        Assert.Equal(0, exit);
        Assert.True(recovered.Success);
        Assert.Equal("acquire:5", events[0]);
        Assert.True(events.IndexOf("read-record") > 0);
        Assert.True(events.IndexOf("process-terminate") > events.IndexOf("read-record"));
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void ARoleLockHeldByAnotherDaemonEndsWithRecordInUseAndNothingIsReadOrTouched()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "editing", "record.json");
        using var holder = new JsonSessionRecordStore(path, TimeSpan.FromMilliseconds(100));
        holder.AcquireExclusive();
        holder.Write(Orphan());
        var before = File.ReadAllText(path);
        using var recovery = new JsonSessionRecordStore(path, TimeSpan.FromMilliseconds(100));
        var events = new List<string>();
        var inspector = new OrderedInspector(events, ExactProcessTerminationState.ConfirmedGone);
        using var output = new StringWriter();

        var exit = ServeCommand
            .RunRecoveryLockAsync(recovery, TimeSpan.FromMilliseconds(150), output, TextWriter.Null, Refusal(recovery))
            .Within("the lock attempt");

        Assert.Equal(ServeRecoveryExitCodes.RecordInUse, exit);
        Assert.Contains("\"startup\":\"refused\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(EtabsRecordErrorCodes.RecordInUse, output.ToString(), StringComparison.Ordinal);
        Assert.False(recovery.HoldsExclusiveLock);
        Assert.Empty(events); // the cleaner was never even constructed with a reachable process
        _ = inspector;
        Assert.Equal(before, File.ReadAllText(path));
    }

    // Test 72 (daemon analogue): an identity the probe cannot read is left alone and the record kept.
    [Fact]
    public void AnUnreadableIdentityIsNeverTargetedAndItsRecordIsRetained()
    {
        var events = new List<string>();
        var store = new OrderedStore(events) { Record = Orphan() };
        var inspector = new OrderedInspector(events, ExactProcessTerminationState.IdentityMismatchOrUnidentified);

        var result = new OrphanSessionCleaner(store, inspector).Clean();

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.IdentityMismatch, result.ErrorCode);
        Assert.True(result.Data.RecordRetained);
        Assert.DoesNotContain("clear-record", events);
        Assert.NotNull(store.Record);
    }

    [Fact]
    public void ARecordPathThatIsUnsafeEndsWithItsOwnExitCode()
    {
        var events = new List<string>();
        var store = new OrderedStore(events)
        {
            LockFailure = new RecordPathUnsafeException(@"C:\x", "the directory is a reparse point")
        };
        using var output = new StringWriter();

        var exit = ServeCommand
            .RunRecoveryLockAsync(store, ServeCommand.RecoveryLockWait, output, TextWriter.Null, Refusal(store))
            .Within("the lock");

        Assert.Equal(ServeRecoveryExitCodes.RecordPathUnsafe, exit);
        Assert.Equal(["acquire:5"], events);
        Assert.Contains(EtabsRecordErrorCodes.RecordPathUnsafe, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARecoveryOnlyDaemonServesOnlyTheReadsAndRefusesEverythingElse()
    {
        var inner = new CountingDispatcher();
        var recovery = new RecoveryOnlyDispatcher(inner);

        var refused = Assert.IsType<Result>(
            recovery.DispatchAsync("open-model", null, CancellationToken.None).Within("open-model"),
            exactMatch: false);
        _ = recovery.DispatchAsync("get-status", null, CancellationToken.None).Within("get-status");

        Assert.False(refused.Success);
        Assert.Contains(RecoveryOnlyDispatcher.Code, refused.Error, StringComparison.Ordinal);
        Assert.Equal(["get-status"], inner.Commands.ToArray());
        Assert.Equal(["get-operation-status", "get-status"], recovery.Capabilities.Order().ToArray());
    }

    private sealed class CountingDispatcher : IServeDispatcher
    {
        public List<string> Commands { get; } = [];

        public IReadOnlyCollection<string> Capabilities { get; } =
            ["get-status", "get-operation-status", "open-model", "save-model"];

        public Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult<object>(Result.Ok());
        }
    }

    private static Func<ManagedEtabsShutdownResult, ServeStartupRefusal> Refusal(ISessionRecordStore store) =>
        recovery => ServeStartupRefusal.Current(recovery, store.FilePath);
}
