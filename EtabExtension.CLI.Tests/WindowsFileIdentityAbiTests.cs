using System.Reflection;
using System.Runtime.InteropServices;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// F-LIVE-1: the file identity the helper reports must be the real Win32 identity, read
/// at the offsets of <c>BY_HANDLE_FILE_INFORMATION</c>. The app compares it with its own
/// independent read of the same file, so a shifted read makes every Save Version after
/// Open fail, and only the native index-low word is really compared (a fail-open risk).
/// </summary>
public sealed class WindowsFileIdentityAbiTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-file-identity-abi", Guid.NewGuid().ToString("N"));

    public WindowsFileIdentityAbiTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// The production interop struct has the Win32 layout: size 52, FILETIMEs at 4/12/20,
    /// volume at 28, index at 44/48.
    /// Mutation caught: removing <c>Pack = 4</c> (size 56, volume at 32, index at 48/52).
    /// </summary>
    [Fact]
    public void ByHandleFileInformationMatchesWin32Abi()
    {
        var type = typeof(WindowsFileIdentity).GetNestedType(
            "ByHandleFileInformation", BindingFlags.NonPublic);
        Assert.NotNull(type);

        var expected = new (string Field, int Offset)[]
        {
            ("FileAttributes", 0),
            ("CreationTime", 4),
            ("LastAccessTime", 12),
            ("LastWriteTime", 20),
            ("VolumeSerialNumber", 28),
            ("FileSizeHigh", 32),
            ("FileSizeLow", 36),
            ("NumberOfLinks", 40),
            ("FileIndexHigh", 44),
            ("FileIndexLow", 48),
        };
        _output.WriteLine($"examined={expected.Length + 1} (10 field offsets + size)");
        Assert.Equal(10, expected.Length);

        var actual = expected
            .Select(e => (e.Field, Offset: Marshal.OffsetOf(type!, e.Field).ToInt32()))
            .ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(52, Marshal.SizeOf(type!));
    }

    /// <summary>
    /// <see cref="WindowsFileIdentity.ReadFileId"/> equals the identity read by an
    /// INDEPENDENT, explicitly laid-out declaration of the same Win32 structure, and its
    /// volume equals the volume serial <c>GetVolumeInformationW</c> reports for the drive.
    /// Mutation caught: removing <c>Pack = 4</c> from the production struct (the string
    /// differs: volume reads FileSizeHigh, the index words shift).
    /// </summary>
    [Fact]
    public void ReadFileIdMatchesIndependentWin32Oracle()
    {
        Assert.True(OperatingSystem.IsWindows(), "this test needs Windows; it must not pass vacuously");

        var files = new[]
        {
            Path.Combine(_directory, "small.edb"),
            Path.Combine(_directory, "second.edb"),
            Path.Combine(_directory, "nested", "model.EDB"),
        };
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        }

        _output.WriteLine($"examined={files.Length} files");
        Assert.Equal(3, files.Length);

        var volumeSerial = DriveVolumeSerial(files[0]);
        Assert.NotEqual(0u, volumeSerial);

        foreach (var file in files)
        {
            var oracle = OracleRead(file);
            Assert.Equal(volumeSerial, oracle.Volume);
            Assert.False(oracle.IndexHigh == 0 && oracle.IndexLow == 0, "the oracle read no index");

            var expected = $"{oracle.Volume:x8}-{oracle.IndexHigh:x8}{oracle.IndexLow:x8}";
            var actual = WindowsFileIdentity.ReadFileId(file);
            _output.WriteLine($"{Path.GetFileName(file)}: oracle={expected} production={actual}");
            Assert.Equal(expected, actual);
        }
    }

    private static (uint Volume, uint IndexHigh, uint IndexLow) OracleRead(string path)
    {
        using var handle = CreateFileW(path, 0x80, 0x7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"open failed: {Marshal.GetLastWin32Error()}");
        Assert.True(GetFileInformationByHandle(handle, out var info), $"read failed: {Marshal.GetLastWin32Error()}");
        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    private static uint DriveVolumeSerial(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        Assert.True(
            GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0),
            $"GetVolumeInformationW failed: {Marshal.GetLastWin32Error()}");
        return serial;
    }

    /// <summary>The oracle: BY_HANDLE_FILE_INFORMATION with every offset stated explicitly.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct OracleByHandleFileInformation
    {
        [FieldOffset(0)] public uint FileAttributes;
        [FieldOffset(4)] public uint CreationTimeLow;
        [FieldOffset(8)] public uint CreationTimeHigh;
        [FieldOffset(12)] public uint LastAccessTimeLow;
        [FieldOffset(16)] public uint LastAccessTimeHigh;
        [FieldOffset(20)] public uint LastWriteTimeLow;
        [FieldOffset(24)] public uint LastWriteTimeHigh;
        [FieldOffset(28)] public uint VolumeSerialNumber;
        [FieldOffset(32)] public uint FileSizeHigh;
        [FieldOffset(36)] public uint FileSizeLow;
        [FieldOffset(40)] public uint NumberOfLinks;
        [FieldOffset(44)] public uint FileIndexHigh;
        [FieldOffset(48)] public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out OracleByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, char[]? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags, char[]? fileSystemNameBuffer, int fileSystemNameSize);
}
