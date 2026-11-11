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
/// The window-station boundary for the watchdog, so the decision logic is testable without
/// ETABS or a desktop.
/// </summary>
public interface IEtabsDialogWindowSource
{
    /// <summary>
    /// Dialog-class top-level windows of <paramref name="pid"/> that are modal right now:
    /// visible, or blocking a disabled owner (the window guard may have hidden the box).
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
    TimeSpan dismissedSettle) : IEtabsModalDialogWatchdog
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan DefaultDismissedSettle = TimeSpan.FromSeconds(30);

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
        var workTask = work();
        // Off the caller's thread: the caller may be the protocol reader, which must never
        // wait on a window scan or on the session lock behind ownedPid.
        var watchTask = Task.Run(() => WatchAsync(ownedPid, stop.Token), CancellationToken.None);
        var first = await Task.WhenAny(workTask, watchTask).ConfigureAwait(false);

        EtabsModalDialogObservation? seen;
        if (first == workTask)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            seen = await SettleWatchAsync(watchTask).ConfigureAwait(false)
                // The work finished: one last look, so a box that appeared in the final
                // poll interval can never be answered with success.
                ?? Scan(ownedPid);
            if (seen is null)
            {
                return await workTask.ConfigureAwait(false);
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

    private async Task<EtabsModalDialogObservation?> WatchAsync(
        Func<int?> ownedPid,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var seen = Scan(ownedPid);
            if (seen is not null)
            {
                return seen;
            }

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

    private EtabsModalDialogObservation? Scan(Func<int?> ownedPid)
    {
        // A fault in the watchdog itself (the pid read, the window walk, the dismiss) must
        // never fault the watch or fail the command it watches. A fault before a dialog is
        // found skips that poll; a fault while dismissing a dialog that WAS found still
        // reports the dialog, as left open (dismissed:false) - the box is real, and the
        // command behind it is blocked whether or not the dismiss could be attempted.
        int owned;
        EtabsDialogWindow dialog;
#pragma warning disable CA1031 // see above: no watchdog fault may surface as a command failure
        try
        {
            if (ownedPid() is not { } pid)
            {
                return null;
            }

            var dialogs = windows.FindModalDialogs(pid);
            if (dialogs.Count == 0)
            {
                return null;
            }

            owned = pid;
            dialog = dialogs[0];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ ETABS dialog scan failed: {ex.Message}");
            return null;
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
            EtabsApiDiagnosticFormatter.Bounded(dialog.Title),
            EtabsApiDiagnosticFormatter.Bounded(dialog.Text),
            dismissed);
    }
}

/// <summary>
/// user32 implementation. Walks the desktop Z-order chain (as <c>Win32TopLevelWindows</c>
/// does) instead of an EnumWindows callback, so no unmanaged function pointer is needed.
/// Text is read with WM_GETTEXT through SendMessageTimeout so a hung ETABS cannot hang the
/// watchdog.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32EtabsDialogWindowSource : IEtabsDialogWindowSource
{
    private const string DialogClass = "#32770";
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
                && ClassOf(handle) == DialogClass
                && IsModal(handle))
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

    private static bool IsModal(nint handle)
    {
        if (IsWindowVisible(handle))
        {
            return true;
        }

        var owner = GetWindow(handle, GwOwner);
        return owner != nint.Zero && !IsWindowEnabled(owner);
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
