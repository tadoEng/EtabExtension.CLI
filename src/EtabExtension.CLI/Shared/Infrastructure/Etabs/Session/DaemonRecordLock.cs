using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public static class EtabsRecordErrorCodes
{
    /// <summary>Another live daemon holds the lock of this record directory.</summary>
    public const string RecordInUse = "ETABS_RECORD_IN_USE";

    /// <summary>The record directory or file is a reparse point or a hard link.</summary>
    public const string RecordPathUnsafe = "ETABS_RECORD_PATH_UNSAFE";
}

/// <summary>A second daemon asked for a record directory whose lock is already held.</summary>
public sealed class RecordInUseException(string path, string message) : IOException(message)
{
    public string RecordPath { get; } = path;
}

/// <summary>The record location is not a plain directory and file owned by this daemon.</summary>
public sealed class RecordPathUnsafeException(string path, string message) : IOException(message)
{
    public string RecordPath { get; } = path;
}

/// <summary>
/// The daemon's exclusive claim on its record directory (card V3): <c>daemon.lock</c>
/// opened with <see cref="FileShare.None"/> and held for the daemon's whole life. Only a
/// holder may recover orphans, write, or clear that directory's record, so a record can only
/// ever describe a process its lock holder started.
///
/// <para>Validation: no component of the directory path BELOW the trusted per-user app-data
/// anchor is a reparse point (junctions and symlinks), and neither the lock file nor an
/// existing record file has more than one hard link. The anchor (<c>%LOCALAPPDATA%</c>) is
/// opened and resolved once with <c>GetFinalPathNameByHandle</c>; a junction ABOVE it (a
/// redirected profile) is the user's own legitimate layout and is accepted, one below it is
/// refused, and the lock file's final path must stay under the resolved anchor. A record path
/// outside the anchor (an explicit <c>--session-record</c> elsewhere) has no trusted anchor
/// and is checked from its root, as before.</para>
/// </summary>
public sealed class DaemonRecordLock : IDisposable
{
    public const string LockFileName = "daemon.lock";
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    private readonly FileStream _stream;

    private DaemonRecordLock(FileStream stream, string directory, string resolvedLockPath)
    {
        _stream = stream;
        Directory = directory;
        ResolvedLockPath = resolvedLockPath;
    }

    public string Directory { get; }

    /// <summary>The lock file's path as the OS resolved it from the open handle.</summary>
    public string ResolvedLockPath { get; }

    public static DaemonRecordLock Acquire(string recordPath, TimeSpan wait, string? trustedAnchor = null)
    {
        var fullRecord = Path.GetFullPath(recordPath);
        var directory = Path.GetDirectoryName(fullRecord)
            ?? throw new RecordPathUnsafeException(fullRecord, "The record path has no directory.");

        var anchor = ResolveAnchor(directory, trustedAnchor);
        RejectReparsePointsBelow(directory, anchor?.Requested);
        System.IO.Directory.CreateDirectory(directory);
        RejectReparsePointsBelow(directory, anchor?.Requested);

        var lockPath = Path.Combine(directory, LockFileName);
        var started = System.Diagnostics.Stopwatch.StartNew();
        FileStream? stream = null;
        while (stream is null)
        {
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (started.Elapsed < wait)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new RecordInUseException(
                    lockPath,
                    $"Another daemon holds the record lock '{lockPath}': {ex.Message}");
            }
        }

        try
        {
            RejectHardLinks(stream.SafeFileHandle, lockPath);
            var resolvedLock = ResolveFinalPath(stream.SafeFileHandle);
            if (anchor is not null
                && resolvedLock.Length > 0
                && !IsUnder(Normalize(resolvedLock), anchor.Resolved))
            {
                throw new RecordPathUnsafeException(
                    lockPath,
                    $"The lock file resolves to '{resolvedLock}', outside the trusted anchor '{anchor.Resolved}'.");
            }

            if (File.Exists(fullRecord))
            {
                if ((File.GetAttributes(fullRecord) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new RecordPathUnsafeException(
                        fullRecord,
                        $"The record file '{fullRecord}' is a reparse point.");
                }

                using var record = new FileStream(
                    fullRecord,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                RejectHardLinks(record.SafeFileHandle, fullRecord);
            }

            return new DaemonRecordLock(stream, directory, resolvedLock);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();

    private sealed record Anchor(string Requested, string Resolved);

    /// <summary>
    /// The trusted per-user app-data anchor for <paramref name="directory"/>, opened and
    /// resolved once; null when the directory is outside it (no trusted anchor).
    /// </summary>
    private static Anchor? ResolveAnchor(string directory, string? trustedAnchor)
    {
        string requested;
        try
        {
            requested = Path.GetFullPath(
                trustedAnchor ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                .TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (requested.Length == 0 || !IsUnder(Normalize(directory), Normalize(requested)))
        {
            return null;
        }

        System.IO.Directory.CreateDirectory(requested);
        var resolved = OperatingSystem.IsWindows() ? ResolveDirectoryFinalPath(requested) : requested;
        return new Anchor(requested, Normalize(resolved.Length == 0 ? requested : resolved));
    }

    private static string Normalize(string path)
    {
        var trimmed = path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        return trimmed.TrimEnd('\\');
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects a reparse point on every component strictly BELOW <paramref name="anchor"/>
    /// down to <paramref name="directory"/>; with no anchor, on every component from the root.
    /// </summary>
    private static void RejectReparsePointsBelow(string directory, string? anchor)
    {
        var current = directory;
        while (!string.IsNullOrEmpty(current)
            && (anchor is null || !current.TrimEnd('\\').Equals(anchor, StringComparison.OrdinalIgnoreCase)))
        {
            if (System.IO.Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new RecordPathUnsafeException(
                    directory,
                    $"The record directory path contains a reparse point at '{current}'.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static string ResolveDirectoryFinalPath(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return directory;
        }

        using var handle = NativeMethods.CreateFileW(
            directory,
            0,
            7, // FILE_SHARE_READ | WRITE | DELETE
            nint.Zero,
            3, // OPEN_EXISTING
            0x02000000, // FILE_FLAG_BACKUP_SEMANTICS: open a directory
            nint.Zero);
        return handle.IsInvalid ? string.Empty : ResolveFinalPath(handle);
    }

    private static void RejectHardLinks(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            throw new RecordPathUnsafeException(
                path,
                $"Could not read the link count of '{path}': {new Win32Exception().Message}");
        }

        if (info.NumberOfLinks != 1)
        {
            throw new RecordPathUnsafeException(
                path,
                $"'{path}' has {info.NumberOfLinks} hard links; a record must be a single plain file.");
        }
    }

    private static string ResolveFinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        var buffer = new char[1024];
        var length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        return length == 0 || length > buffer.Length ? string.Empty : new string(buffer, 0, (int)length);
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            out ByHandleFileInformation info);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            [Out] char[] path,
            uint pathLength,
            uint flags);
    }
}
