using System.ComponentModel;
using System.Diagnostics;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// A process this daemon created with <c>Process.Start</c>. The returned object IS the
/// authoritative handle (card v3 ownership by construction): nothing is attributed to us
/// by a census, and the identity is read from the handle itself.
/// </summary>
public interface ISpawnedEtabsProcess : IProcessHandle
{
    int Pid { get; }

    /// <summary>
    /// Pid + start time UTC + executable path read from the handle. May block briefly while
    /// a young process refuses to report its main module; the caller runs it on the startup
    /// worker, inside the startup deadline.
    /// </summary>
    ManagedProcessIdentity ReadIdentity();

    /// <summary>Refreshes and returns <c>MainWindowHandle</c>; zero until the window exists.</summary>
    nint RefreshMainWindowHandle();
}

public interface IEtabsProcessSpawner
{
    /// <summary>Starts the exact executable and returns its handle. Throws when nothing started.</summary>
    ISpawnedEtabsProcess Start(string executablePath);
}

public sealed class WindowsEtabsProcessSpawner : IEtabsProcessSpawner
{
    public ISpawnedEtabsProcess Start(string executablePath)
    {
        var process = Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
        }) ?? throw new InvalidOperationException(
            $"Process.Start returned no process for '{executablePath}'.");
        return new WindowsSpawnedEtabsProcess(process);
    }
}

public sealed class WindowsSpawnedEtabsProcess(Process process) : ISpawnedEtabsProcess
{
    private static readonly TimeSpan IdentityRetryWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdentityRetryInterval = TimeSpan.FromMilliseconds(25);

    public int Pid { get; } = process.Id;

    public ManagedProcessIdentity ReadIdentity()
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return new ManagedProcessIdentity(
                        process.Id,
                        process.StartTime.ToUniversalTime(),
                        Path.GetFullPath(path));
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                if (started.Elapsed >= IdentityRetryWindow)
                {
                    throw;
                }
            }

            if (started.Elapsed >= IdentityRetryWindow)
            {
                throw new InvalidOperationException(
                    $"The started ETABS process (pid={process.Id}) did not report its main module.");
            }

            Thread.Sleep(IdentityRetryInterval);
        }
    }

    public nint RefreshMainWindowHandle()
    {
        process.Refresh();
        return process.MainWindowHandle;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Kill()
    {
        if (!HasExited)
        {
            // Our own handle only. The tree is not killed: the card kills exactly the
            // process this daemon created.
            process.Kill(entireProcessTree: false);
        }
    }

    public bool WaitForExit(TimeSpan timeout) =>
        process.WaitForExit(checked((int)timeout.TotalMilliseconds));

    public void Dispose() => process.Dispose();
}

/// <summary>One visible top-level window of the launching process.</summary>
public sealed record StartupWindow(nint Handle, string Title, string ClassName, nint Owner = 0);

/// <summary>The window-station boundary of the startup-blocker rule (card Z2).</summary>
public interface IEtabsStartupWindowSource
{
    IReadOnlyList<StartupWindow> VisibleTopLevelWindows(int pid);

    /// <summary>
    /// Whether <paramref name="window"/> accepts input. The post-Ready blocker rule asks it
    /// of the MAIN window (review P2-A): a main window that is still enabled cannot be held up.
    /// </summary>
    bool IsWindowEnabled(nint window);

    /// <summary>
    /// Whether <paramref name="window"/> still exists and belongs to <paramref name="pid"/>
    /// (card v2.5a A3: the captured main window is validated on every use). A source with no
    /// way to ask answers "live" for any non-zero handle.
    /// </summary>
    bool IsLiveWindowOf(nint window, int pid) => window != nint.Zero;
}
