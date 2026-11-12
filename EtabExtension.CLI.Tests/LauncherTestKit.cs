using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;

namespace EtabExtension.CLI.Tests;

// Fakes shared by the launcher, ownership and session tests. Everything here is ETABS-free:
// no COM, no process, no desktop.

internal static class Kit
{
    public static readonly ManagedProcessIdentity Identity = new(
        4242,
        new DateTimeOffset(2026, 8, 13, 5, 12, 12, TimeSpan.Zero),
        @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe");

    /// <summary>The independent test deadline every hang-type test uses, so a mutation fails instead of hanging.</summary>
    public static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);

    public static T Within<T>(this Task<T> task, string what, TimeSpan? deadline = null)
    {
        if (!task.Wait(deadline ?? TestDeadline))
        {
            throw new TimeoutException($"Test deadline expired waiting for: {what}");
        }

        return task.GetAwaiter().GetResult();
    }

    public static void Within(this Task task, string what)
    {
        if (!task.Wait(TestDeadline))
        {
            throw new TimeoutException($"Test deadline expired waiting for: {what}");
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>Runs on its own thread, so a starved thread pool cannot inflate a timing assertion.</summary>
    public static Task<T> OnDedicatedThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        { IsBackground = true }.Start();
        return completion.Task;
    }

    public static void Signalled(this ManualResetEventSlim signal, string what)
    {
        if (!signal.Wait(TestDeadline))
        {
            throw new TimeoutException($"Test deadline expired waiting for: {what}");
        }
    }
}

internal sealed class FakeSpawnedProcess(ManagedProcessIdentity identity) : ISpawnedEtabsProcess
{
    private int _kills;
    private int _disposed;
    private volatile bool _hasExited;
    private int _polls;

    public int Pid => identity.Pid;

    public bool HasExited => _hasExited;

    public int KillCount => Volatile.Read(ref _kills);

    public bool Disposed => Volatile.Read(ref _disposed) == 1;

    public bool RefusesToExit { get; init; }

    /// <summary>When set, a wait that does not see an exit really sleeps the timeout (real-time budgets).</summary>
    public bool WaitSleeps { get; init; }

    public List<string>? Events { get; set; }

    /// <summary>Polls that report no main window yet.</summary>
    public int NoWindowPolls { get; set; }

    public nint MainWindow { get; set; } = 0x100;

    public Action? BeforeReadIdentity { get; set; }

    public ManualResetEventSlim Killed { get; } = new(false);

    public ManagedProcessIdentity ReadIdentity()
    {
        BeforeReadIdentity?.Invoke();
        return identity;
    }

    public nint RefreshMainWindowHandle() =>
        Interlocked.Increment(ref _polls) > NoWindowPolls ? MainWindow : nint.Zero;

    public int Polls => Volatile.Read(ref _polls);

    public void Kill()
    {
        Interlocked.Increment(ref _kills);
        Events?.Add("process-kill");
        if (!RefusesToExit)
        {
            _hasExited = true;
        }

        Killed.Set();
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (!_hasExited && WaitSleeps)
        {
            Thread.Sleep(timeout);
        }

        return _hasExited;
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    public void MarkExited() => _hasExited = true;
}

internal sealed class FakeSpawner(FakeSpawnedProcess process, List<string> events) : IEtabsProcessSpawner
{
    public int StartCount { get; private set; }

    /// <summary>Runs before <c>Start</c> returns, so a test can hold the process creation open.</summary>
    public Action? BeforeReturn { get; set; }

    public Exception? Failure { get; set; }

    public ISpawnedEtabsProcess Start(string executablePath)
    {
        StartCount++;
        events.Add("process-start");
        BeforeReturn?.Invoke();
        return Failure is null ? process : throw Failure;
    }
}

internal sealed class FakeAttachFactory(FakeRawApi api, List<string> events) : IEtabsRawApiFactory
{
    private int _attachCalls;

    public int AttachCalls => Volatile.Read(ref _attachCalls);

    public int? AttachedPid { get; private set; }

    /// <summary>Calls that return null (not attachable yet) before the first success.</summary>
    public int NullsBeforeSuccess { get; set; }

    /// <summary>Runs inside the attach call; a test blocks here to model a hung COM call.</summary>
    public Action? OnAttach { get; set; }

    public Exception? Failure { get; set; }

    public IEtabsRawApi? TryAttach(int pid)
    {
        var call = Interlocked.Increment(ref _attachCalls);
        events.Add("attach");
        AttachedPid = pid;
        OnAttach?.Invoke();
        if (Failure is not null)
        {
            throw Failure;
        }

        return call <= NullsBeforeSuccess ? null : api;
    }
}

internal sealed class FakeStartupWindows : IEtabsStartupWindowSource
{
    private readonly object _gate = new();

    public List<(int Pid, StartupWindow Window)> Windows { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>
    /// Whether the main window accepts input. Disabled by default: a window that holds a
    /// startup up does so while the main window cannot be used (review P2-A).
    /// </summary>
    public bool MainEnabled { get; set; }

    public bool IsWindowEnabled(nint window) => MainEnabled;

    public IReadOnlyList<StartupWindow> VisibleTopLevelWindows(int pid)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        lock (_gate)
        {
            return Windows.Where(entry => entry.Pid == pid).Select(entry => entry.Window).ToList();
        }
    }
}

internal sealed class MemoryRecordStore(List<string>? events = null) : ISessionRecordStore
{
    private readonly object _gate = new();
    private ManagedEtabsSessionRecord? _record;

    public string FilePath => @"C:\memory\managed-etabs-session.json";

    public ManagedEtabsSessionRecord? Record
    {
        get
        {
            lock (_gate)
            {
                return _record;
            }
        }
        set
        {
            lock (_gate)
            {
                _record = value;
            }
        }
    }

    public List<ManagedEtabsSessionRecord> Writes { get; } = [];

    public int ClearCount { get; private set; }

    /// <summary>Runs inside <c>Write</c>; a test blocks here to model a stalled record write.</summary>
    public Action<ManagedEtabsSessionRecord>? OnWrite { get; set; }

    public Exception? WriteException { get; set; }

    public ManagedEtabsSessionRecord? Read() => Record;

    public void Write(ManagedEtabsSessionRecord record)
    {
        events?.Add($"record-write:{record.Phase?.ToString() ?? "none"}");
        OnWrite?.Invoke(record);
        if (WriteException is not null)
        {
            throw WriteException;
        }

        lock (_gate)
        {
            Writes.Add(record);
            _record = record;
        }
    }

    public void Clear()
    {
        events?.Add("record-clear");
        lock (_gate)
        {
            ClearCount++;
            _record = null;
        }
    }
}

internal sealed class FakeCensus : IProcessInspector
{
    public List<string> Events { get; set; } = [];

    public ManagedProcessIdentity[] Preflight { get; set; } = [];

    public int Unidentified { get; set; }

    public int TerminateExactCalls { get; private set; }

    public int ObserveCalls { get; private set; }

    /// <summary>What <c>Find(pid)</c> reports for the session's identity verification.</summary>
    public ManagedProcessIdentity? Live { get; set; }

    public EtabsProcessObservation ObserveEtabs()
    {
        ObserveCalls++;
        Events.Add(ObserveCalls == 1 ? "census-preflight" : "census-later");
        return new(Preflight, Unidentified);
    }

    public ManagedProcessIdentity? Find(int pid) =>
        Live is { } live && live.Pid == pid ? live : Preflight.FirstOrDefault(i => i.Pid == pid);

    public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected) => null;

    public ExactProcessTerminationResult TerminateExact(ManagedProcessIdentity expected, TimeSpan timeout)
    {
        TerminateExactCalls++;
        return new(ExactProcessTerminationState.ConfirmedGone, expected);
    }
}

internal sealed class FakeClock : IManagedEtabsClock
{
    public TimeSpan Elapsed { get; private set; }

    public List<TimeSpan> Waits { get; } = [];

    public long Timestamp => Elapsed.Ticks;

    public TimeSpan ElapsedSince(long timestamp) => TimeSpan.FromTicks(Elapsed.Ticks - timestamp);

    public void Wait(TimeSpan interval)
    {
        Waits.Add(interval);
        Elapsed += interval;
    }
}

internal sealed class FakeWindowGuardFactory : IManagedEtabsWindowGuardFactory
{
    public List<string> Events { get; set; } = [];

    public List<FakeWindowGuard> Activated { get; } = [];

    public nint[] StillVisible { get; set; } = [];

    /// <summary>Lets a test hook the guard it is about to be handed.</summary>
    public Action<FakeWindowGuard>? OnActivated { get; set; }

    public IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess)
    {
        Events.Add("window-guard-activate");
        var guard = new FakeWindowGuard(ownedProcess, Events) { StillVisible = StillVisible };
        OnActivated?.Invoke(guard);
        Activated.Add(guard);
        return guard;
    }
}

internal sealed class FakeWindowGuard(IOwnedEtabsProcess owned, List<string> events)
    : IManagedEtabsWindowGuard
{
    public IOwnedEtabsProcess Owned { get; } = owned;
    public ManagedProcessIdentity Identity => Owned.Identity;
    public bool IsActive => !Disposed && !ReleasedForUser;
    public bool Disposed { get; private set; }
    public bool ReleasedForUser { get; private set; }
    public int SuppressionConfirmations { get; private set; }
    public nint[] StillVisible { get; set; } = [];

    public ManagedEtabsVisibilityState State { get; private set; } =
        ManagedEtabsVisibilityState.StartingVisibleByConsent;

    public ManagedEtabsExposureEvidence Exposure { get; set; } = ManagedEtabsExposureEvidence.None;

    public int EnterBackgroundHiddenCalls { get; private set; }

    public List<string> Stages { get; } = [];

    public ManagedEtabsExposureEvidence CertifyExposure() => Exposure;

    public void MarkStage(string stage) => Stages.Add(stage);

    public void BeginExplicitReveal()
    {
        events.Add("window-guard-begin-explicit-reveal");
        ReleasedForUser = true;
        State = ManagedEtabsVisibilityState.RevealPending;
    }

    public void EnterUserVisible()
    {
        events.Add("window-guard-enter-user-visible");
        State = ManagedEtabsVisibilityState.UserVisible;
    }

    public ManagedEtabsWindowConfirmation ConfirmSuppressedAndCloseConsentInterval()
    {
        var confirmation = ConfirmSuppressed();
        if (confirmation.Confirmed)
        {
            events.Add("window-guard-enter-background-hidden");
            EnterBackgroundHiddenCalls++;
            State = ManagedEtabsVisibilityState.BackgroundHidden;
        }

        return confirmation;
    }

    /// <summary>Runs on every suppression confirmation with its 1-based number.</summary>
    public Action<int>? OnConfirmSuppressed { get; set; }

    public ManagedEtabsWindowConfirmation ConfirmSuppressed()
    {
        events.Add("window-guard-confirm-suppressed");
        SuppressionConfirmations++;
        OnConfirmSuppressed?.Invoke(SuppressionConfirmations);
        return StillVisible.Length == 0
            ? new(true, 1, TimeSpan.Zero, [], null)
            : new(
                false,
                3,
                TimeSpan.FromSeconds(5),
                StillVisible,
                $"{ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed}; " +
                $"ownedPid={Identity.Pid}; visibleOwnedWindows={StillVisible.Length}");
    }

    public ManagedEtabsWindowConfirmation ConfirmRevealed()
    {
        events.Add("window-guard-confirm-revealed");
        return new(true, 1, TimeSpan.Zero, [(nint)1], null);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeResolver(string path) : IEtabsExecutableResolver
{
    public string Resolve() => path;
}

internal sealed class FakeVersionProbe(List<string>? events = null) : IEtabsVersionProbe
{
    public (int MajorVersion, string FullVersion) Read(string executablePath)
    {
        events?.Add("version-probe");
        return (23, "23.3.0");
    }
}

internal sealed class FakeRawApi : IEtabsRawApi
{
    public List<string> Events { get; set; } = [];
    public bool HasSapModelValue { get; set; } = true;
    public Exception? OapiVersionException { get; set; }
    public int HideReturnCode { get; set; }
    public bool IgnoreHide { get; set; }
    public int ExitReturnCode { get; set; }
    public Exception? ExitException { get; set; }
    public Action? OnExit { get; set; }
    public bool IsVisible { get; private set; } = true;
    public int HideCalls { get; private set; }
    public int UnhideCalls { get; private set; }
    public int ExitCount { get; private set; }
    public int InitializeCount { get; private set; }
    public int WrapCount { get; private set; }
    public object? WrappedInstance { get; private set; }
    public int WrappedMajorVersion { get; private set; }
    public double WrappedApiVersion { get; private set; }
    public string? WrappedFullVersion { get; private set; }

    public bool Visible()
    {
        Events.Add("visible");
        return IsVisible;
    }

    public int Hide()
    {
        Events.Add("hide");
        HideCalls++;
        if (HideReturnCode != 0 || IgnoreHide)
        {
            return HideReturnCode;
        }

        IsVisible = false;
        return 0;
    }

    public int Unhide()
    {
        Events.Add("unhide");
        UnhideCalls++;
        IsVisible = true;
        return 0;
    }

    public double GetOapiVersionNumber()
    {
        Events.Add("oapi-version");
        return OapiVersionException is null ? 2.16 : throw OapiVersionException;
    }

    public bool HasSapModel
    {
        get
        {
            Events.Add("sap-model");
            return HasSapModelValue;
        }
    }

    public int InitializeNewModel()
    {
        Events.Add("initialize");
        InitializeCount++;
        return 0;
    }

    public int ApplicationExit(bool fileSave)
    {
        Events.Add($"application-exit-save={fileSave}");
        ExitCount++;
        OnExit?.Invoke();
        return ExitException is null ? ExitReturnCode : throw ExitException;
    }

    public void CompleteApiReadiness(int majorVersion, double apiVersion, string fullVersion)
    {
        Events.Add("wrap-existing");
        WrapCount++;
        WrappedInstance = this;
        WrappedMajorVersion = majorVersion;
        WrappedApiVersion = apiVersion;
        WrappedFullVersion = fullVersion;
    }

    public ETABSApplication Application =>
        throw new InvalidOperationException("Fake must not expose COM");

    public void ReleaseApiReferences() => Events.Add("release-api-references");
}

/// <summary>Everything one launcher test needs, wired together.</summary>
internal sealed class LauncherRig
{
    public List<string> Events { get; } = [];
    public FakeRawApi Api { get; }
    public FakeSpawnedProcess Process { get; }
    public FakeSpawner Spawner { get; }
    public FakeAttachFactory Attach { get; }
    public MemoryRecordStore Records { get; }
    public FakeCensus Census { get; } = new();
    public FakeWindowGuardFactory Guards { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeStartupWindows Windows { get; } = new();
    public StringWriter Diagnostics { get; } = new();
    public LaunchPublicationCell Cell { get; } = new();
    public ManagedEtabsLauncher Launcher { get; }

    public LauncherRig(
        ManagedEtabsLauncherOptions? options = null,
        IManagedEtabsClock? clock = null,
        FakeSpawnedProcess? process = null,
        Func<FakeCensus, IProcessInspector>? inspectorFactory = null)
    {
        Api = new FakeRawApi { Events = Events };
        Process = process ?? new FakeSpawnedProcess(Kit.Identity);
        Spawner = new FakeSpawner(Process, Events);
        Attach = new FakeAttachFactory(Api, Events);
        Records = new MemoryRecordStore(Events);
        Census.Events = Events;
        Guards.Events = Events;
        Launcher = new ManagedEtabsLauncher(
            inspectorFactory?.Invoke(Census) ?? Census,
            new FakeResolver(Kit.Identity.ExecutablePath),
            Attach,
            new FakeVersionProbe(Events),
            Diagnostics,
            Guards,
            clock ?? Clock,
            Spawner,
            Windows,
            options ?? new ManagedEtabsLauncherOptions());
    }

    public EtabsLaunchState NewState() => new(Records, Cell);
}

/// <summary>A stamp reader whose file did not exist before the save and exists after it.</summary>
internal sealed class ChangingFileStamps : EtabExtension.CLI.Features.SaveModel.ISavedFileStampReader
{
    private int _reads;

    public EtabExtension.CLI.Features.SaveModel.SavedFileStamp? Read(string path) =>
        Interlocked.Increment(ref _reads) == 1
            ? null
            : new EtabExtension.CLI.Features.SaveModel.SavedFileStamp(
                "0000abcd-0000000000001234",
                4096,
                new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
}
