using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// Typed failure codes that carry a machine-readable <c>detail={json}</c> tail.
///
/// <para>The wire shape is the existing one - a single <c>error</c> string that leads with
/// <c>[CODE]</c> - so it survives the operation lane's <c>Operation failed: </c> prefix
/// unchanged. The structured fields follow the first <c> detail=</c> marker as one JSON
/// object running to the end of the string; the prose before it never contains that
/// marker.</para>
/// </summary>
public static class EtabsTypedFailureCodes
{
    /// <summary>ETABS put up a modal dialog while a command was waiting on it.</summary>
    public const string ModalDialog = "ETABS_MODAL_DIALOG";

    /// <summary>
    /// The dialog scan threw or timed out, so the command cannot be certified free of a
    /// modal dialog. Retryable; never answered as success (card W3).
    /// </summary>
    public const string DialogScanInconclusive = "ETABS_DIALOG_SCAN_INCONCLUSIVE";

    /// <summary>The model was saved by a newer ETABS than the one running.</summary>
    public const string VersionMismatch = "ETABS_VERSION_MISMATCH";

    internal const string DetailMarker = " detail=";

    internal static string Format(string code, string prose, object detail) =>
        $"[{code}] {prose}{DetailMarker}{JsonSerializer.Serialize(detail, DetailJson)}";

    private static readonly JsonSerializerOptions DetailJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

/// <summary>One top-level dialog-class (<c>#32770</c>) window of the watched process.</summary>
public sealed record EtabsDialogWindow(nint Handle, string Title, IReadOnlyList<string> Texts)
{
    public string Text => string.Join(" ", Texts.Where(text => !string.IsNullOrWhiteSpace(text)));
}

/// <summary>What the watchdog saw, and whether it closed the dialog itself.</summary>
public sealed record EtabsModalDialogObservation(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("dismissed")] bool Dismissed)
{
    public string ToFailure() => EtabsTypedFailureCodes.Format(
        EtabsTypedFailureCodes.ModalDialog,
        Dismissed
            ? "ETABS showed a modal dialog it cannot continue past; the dialog was closed and the command failed."
            : "ETABS is waiting on a modal dialog; it was left open and the command failed. " +
              "The ETABS session is busy until the dialog is answered.",
        this);
}

/// <summary>
/// The facts about one top-level window the modal rule is a pure function of. Gathering
/// them is the only part that touches user32.
/// </summary>
public sealed record EtabsWindowFacts(
    nint Handle,
    string ClassName,
    nint Owner,
    bool OwnerEnabled);

/// <summary>
/// The modal rule (card V2, W3), pure over <see cref="EtabsWindowFacts"/>.
///
/// <para>A <c>#32770</c> window of the watched pid is a modal CANDIDATE when it is owned and
/// its owner is disabled, or when it is ownerless. Visibility is NOT a criterion - the window
/// guard may have hidden a real modal box. There is no class or title allowlist: an ownerless
/// dialog is a candidate unless runtime evidence says otherwise, and a modeless dialog on an
/// ENABLED owner is not.</para>
/// </summary>
public static class EtabsModalDialogRule
{
    public const string DialogClass = "#32770";

    public static bool IsModalCandidate(EtabsWindowFacts window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ClassName != DialogClass)
        {
            return false;
        }

        return window.Owner == nint.Zero || !window.OwnerEnabled;
    }
}

/// <summary>
/// The window-station boundary for the watchdog, so the decision logic is testable without
/// ETABS or a desktop.
/// </summary>
public interface IEtabsDialogWindowSource
{
    /// <summary>
    /// Dialog-class top-level windows of <paramref name="pid"/> that are modal CANDIDATES
    /// under <see cref="EtabsModalDialogRule"/>. Throws when the window station could not be
    /// read: the watchdog reports that as inconclusive, never as "nothing found".
    /// </summary>
    IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid);

    /// <summary>Presses IDCANCEL, or the dialog's single button. True once the window is gone.</summary>
    bool TryDismiss(EtabsDialogWindow dialog);
}

/// <summary>The only dialogs the watchdog is allowed to close by itself.</summary>
public static class KnownEtabsDialogs
{
    /// <summary>
    /// "This file cannot be opened in an older version" - shown when a model saved by a
    /// newer ETABS is opened (DD-5, 23.3 file on 23.2). It has one answer, and leaving it
    /// up only blocks the session.
    /// </summary>
    public static bool IsOlderVersionBox(EtabsDialogWindow dialog)
    {
        var text = $"{dialog.Title} {dialog.Text}";
        return text.Contains("cannot be opened", StringComparison.OrdinalIgnoreCase)
            && text.Contains("older version", StringComparison.OrdinalIgnoreCase);
    }
}

public interface IEtabsModalDialogWatchdog
{
    /// <summary>True while a command abandoned to an unanswered dialog is still blocked.</summary>
    bool IsBlocked { get; }

    /// <summary>
    /// Runs <paramref name="work"/> while polling the owned ETABS for a modal dialog. A
    /// dialog fails the command with <c>ETABS_MODAL_DIALOG</c> whatever the work returns.
    /// </summary>
    Task<object> GuardAsync(Func<int?> ownedPid, Func<Task<object>> work, CancellationToken ct);

    /// <summary>The refusal for a command arriving while an earlier one is still blocked.</summary>
    Result BlockedFailure();
}

public sealed class EtabsModalDialogWatchdog(
    IEtabsDialogWindowSource windows,
    TimeSpan pollInterval,
    TimeSpan dismissedSettle,
    TimeSpan? scanTimeout = null) : IEtabsModalDialogWatchdog
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan DefaultDismissedSettle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _scanTimeout = scanTimeout ?? DefaultScanTimeout;

    /// <summary>What one window scan concluded.</summary>
    private sealed record ScanOutcome(EtabsModalDialogObservation? Found, string? Inconclusive)
    {
        public static readonly ScanOutcome Clean = new(null, null);
    }

    /// <summary>The most recent scan verdict of one guarded command's watch.</summary>
    private sealed class ScanTrace
    {
        private string? _inconclusive;

        public string? Inconclusive
        {
            get => Volatile.Read(ref _inconclusive);
            set => Volatile.Write(ref _inconclusive, value);
        }
    }

    /// <summary>
    /// The abandoned work and the dialog that abandoned it, published together. Readers on
    /// the protocol thread (<see cref="IsBlocked"/>, <see cref="BlockedFailure"/>) must never
    /// see the task of one block paired with the observation of another, so the pair is one
    /// immutable object behind one volatile reference rather than two separate fields.
    /// </summary>
    private sealed record BlockedState(Task Work, EtabsModalDialogObservation By);

    private BlockedState? _blocked;

    public EtabsModalDialogWatchdog()
        : this(PlatformWindows(), DefaultPollInterval, DefaultDismissedSettle)
    {
    }

    private static Win32EtabsDialogWindowSource PlatformWindows() => OperatingSystem.IsWindows()
        ? new Win32EtabsDialogWindowSource()
        : throw new PlatformNotSupportedException("The ETABS dialog watchdog requires Windows.");

    public bool IsBlocked => Volatile.Read(ref _blocked) is { Work.IsCompleted: false };

    public Result BlockedFailure() => Result.Fail(
        (Volatile.Read(ref _blocked)?.By
            ?? new EtabsModalDialogObservation("unknown", string.Empty, false)).ToFailure());

    public async Task<object> GuardAsync(
        Func<int?> ownedPid,
        Func<Task<object>> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownedPid);
        ArgumentNullException.ThrowIfNull(work);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trace = new ScanTrace();
        var workTask = work();
        // Off the caller's thread: the caller may be the protocol reader, which must never
        // wait on a window scan or on the session lock behind ownedPid.
        var watchTask = Task.Run(() => WatchAsync(ownedPid, trace, stop.Token), CancellationToken.None);
        var first = await Task.WhenAny(workTask, watchTask).ConfigureAwait(false);

        EtabsModalDialogObservation? seen;
        if (first == workTask)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            seen = await SettleWatchAsync(watchTask).ConfigureAwait(false);
            if (seen is null)
            {
                // The work finished: one last look, so a box that appeared in the final
                // poll interval can never be answered with success - and a look that could
                // not be taken is never answered with success either (card W3).
                var last = Scan(ownedPid);
                if (last.Found is null)
                {
                    return last.Inconclusive is null
                        ? await workTask.ConfigureAwait(false)
                        : Result.Fail(InconclusiveFailure(last.Inconclusive));
                }

                seen = last.Found;
            }
        }
        else
        {
            seen = await watchTask.ConfigureAwait(false);
        }

        if (seen is null)
        {
            return await workTask.ConfigureAwait(false);
        }

        if (seen.Dismissed)
        {
            // The box is gone, so the blocked call returns; give it a bounded moment so
            // the session is free again before the failure is answered.
            await Task.WhenAny(workTask, Task.Delay(dismissedSettle, ct)).ConfigureAwait(false);
        }

        if (!workTask.IsCompleted)
        {
            Volatile.Write(ref _blocked, new BlockedState(workTask, seen));
            _ = workTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        return Result.Fail(seen.ToFailure());
    }

    private static string InconclusiveFailure(string reason) => EtabsTypedFailureCodes.Format(
        EtabsTypedFailureCodes.DialogScanInconclusive,
        "ETABS could not be checked for a modal dialog, so the command cannot be certified " +
        "complete. Retry.",
        new { reason });

    private async Task<EtabsModalDialogObservation?> WatchAsync(
        Func<int?> ownedPid,
        ScanTrace trace,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var outcome = Scan(ownedPid);
            if (outcome.Found is not null)
            {
                return outcome.Found;
            }

            trace.Inconclusive = outcome.Inconclusive;

            try
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private static async Task<EtabsModalDialogObservation?> SettleWatchAsync(
        Task<EtabsModalDialogObservation?> watchTask)
    {
        try
        {
            return await watchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private ScanOutcome Scan(Func<int?> ownedPid)
    {
        // A fault in the watchdog itself must never fault the watch or the command it
        // watches - but it is not "nothing found" either: it is INCONCLUSIVE, and a command
        // may not answer success over it (card W3). A fault while dismissing a dialog that
        // WAS found still reports the dialog, as left open (dismissed:false).
        int owned;
        EtabsDialogWindow dialog;
#pragma warning disable CA1031 // see above: no watchdog fault may surface as a command failure
        try
        {
            if (ownedPid() is not { } pid)
            {
                return ScanOutcome.Clean;
            }

            var scan = Task.Run(() => windows.FindModalDialogs(pid));
            if (!scan.Wait(_scanTimeout))
            {
                Console.Error.WriteLine(
                    $"⚠ ETABS dialog scan did not finish within {_scanTimeout.TotalSeconds:0.###}s.");
                _ = scan.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return new(null, $"the dialog scan did not finish within {_scanTimeout.TotalSeconds:0.###}s");
            }

            var dialogs = scan.GetAwaiter().GetResult();
            if (dialogs.Count == 0)
            {
                return ScanOutcome.Clean;
            }

            owned = pid;
            dialog = dialogs[0];
        }
        catch (Exception ex)
        {
            var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
            Console.Error.WriteLine($"⚠ ETABS dialog scan failed: {cause.Message}");
            return new(null, $"the dialog scan failed: {EtabsApiDiagnosticFormatter.Bounded(cause.Message)}");
        }

        bool dismissed;
        try
        {
            dismissed = KnownEtabsDialogs.IsOlderVersionBox(dialog) && windows.TryDismiss(dialog);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ ETABS dialog dismiss failed: {ex.Message}");
            dismissed = false;
        }
#pragma warning restore CA1031

        Console.Error.WriteLine(
            $"⚠ ETABS modal dialog (PID {owned}): title='{dialog.Title}', dismissed={dismissed}");
        return new(
            new EtabsModalDialogObservation(
                EtabsApiDiagnosticFormatter.Bounded(dialog.Title),
                EtabsApiDiagnosticFormatter.Bounded(dialog.Text),
                dismissed),
            null);
    }
}

/// <summary>
/// user32 implementation. Walks the desktop Z-order chain (as <c>Win32TopLevelWindows</c>
/// does) instead of an EnumWindows callback, so no unmanaged function pointer is needed.
/// Text is read with WM_GETTEXT through SendMessageTimeout so a hung ETABS cannot hang the
/// watchdog.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32EtabsDialogWindowSource : IEtabsDialogWindowSource, IEtabsStartupWindowSource
{
    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const uint GwChild = 5;
    private const uint WmGetText = 0x000D;
    private const uint WmCommand = 0x0111;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int IdCancel = 2;
    private const int WindowScanLimit = 20_000;
    private const int TextCapacity = 1024;

    public IReadOnlyList<EtabsDialogWindow> FindModalDialogs(int pid)
    {
        var found = new List<EtabsDialogWindow>();
        var handle = GetTopWindow(nint.Zero);
        for (var scanned = 0; handle != nint.Zero && scanned < WindowScanLimit; scanned++)
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (unchecked((int)processId) == pid
                && EtabsModalDialogRule.IsModalCandidate(FactsOf(handle)))
            {
                found.Add(new(handle, TextOf(handle), ChildTexts(handle, "Static")));
            }

            handle = GetWindow(handle, GwHwndNext);
        }

        return found;
    }

    public bool TryDismiss(EtabsDialogWindow dialog)
    {
        var buttons = Children(dialog.Handle)
            .Where(child => ClassOf(child) == "Button")
            .ToList();
        int? command = buttons.Any(button => GetDlgCtrlID(button) == IdCancel)
            ? IdCancel
            : buttons.Count == 1 ? GetDlgCtrlID(buttons[0]) : null;
        if (command is null || !PostMessageW(dialog.Handle, WmCommand, (nint)command.Value, nint.Zero))
        {
            return false;
        }

        for (var waited = 0; waited < 20; waited++)
        {
            if (!IsWindow(dialog.Handle))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return !IsWindow(dialog.Handle);
    }

    /// <summary>Visible top-level windows of <paramref name="pid"/>, for the startup-blocker rule.</summary>
    public IReadOnlyList<StartupWindow> VisibleTopLevelWindows(int pid)
    {
        var found = new List<StartupWindow>();
        var handle = GetTopWindow(nint.Zero);
        for (var scanned = 0; handle != nint.Zero && scanned < WindowScanLimit; scanned++)
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (unchecked((int)processId) == pid && IsWindowVisible(handle))
            {
                found.Add(new(handle, TextOf(handle), ClassOf(handle)));
            }

            handle = GetWindow(handle, GwHwndNext);
        }

        return found;
    }

    private static EtabsWindowFacts FactsOf(nint handle)
    {
        var owner = GetWindow(handle, GwOwner);
        return new(
            handle,
            ClassOf(handle),
            owner,
            owner == nint.Zero || IsWindowEnabled(owner));
    }

    private static IEnumerable<nint> Children(nint parent)
    {
        var child = GetWindow(parent, GwChild);
        for (var scanned = 0; child != nint.Zero && scanned < WindowScanLimit; scanned++)
        {
            yield return child;
            child = GetWindow(child, GwHwndNext);
        }
    }

    private static List<string> ChildTexts(nint parent, string className) => Children(parent)
        .Where(child => ClassOf(child) == className)
        .Select(TextOf)
        .Where(text => text.Length > 0)
        .ToList();

    private static string ClassOf(nint handle)
    {
        var name = new char[256];
        var length = GetClassNameW(handle, name, name.Length);
        return length > 0 ? new string(name, 0, length) : string.Empty;
    }

    private static string TextOf(nint handle)
    {
        var text = new char[TextCapacity];
        if (SendMessageTimeoutW(
                handle, WmGetText, TextCapacity, text, SmtoAbortIfHung, 500, out var copied) == nint.Zero)
        {
            return string.Empty;
        }

        var length = (int)Math.Clamp((long)copied, 0, TextCapacity);
        return new string(text, 0, length).TrimEnd(char.MinValue);
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetTopWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int GetDlgCtrlID(nint hWnd);

    [DllImport("user32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(
        nint hWnd,
        [Out] char[] lpClassName,
        int nMaxCount);

    [DllImport("user32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(
        nint hWnd,
        uint msg,
        nint wParam,
        [Out] char[] lParam,
        uint fuFlags,
        uint uTimeout,
        out nint lpdwResult);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
}
