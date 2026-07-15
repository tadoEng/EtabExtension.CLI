using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public sealed record ManagedProcessIdentity(
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath);

public sealed record ManagedEtabsSessionRecord(
    int SchemaVersion,
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath,
    Guid ManagedLaunchRecordId,
    DateTimeOffset CreatedAtUtc);

public interface ISessionRecordStore
{
    ManagedEtabsSessionRecord? Read();
    void Write(ManagedEtabsSessionRecord record);
    void Clear();
}

public sealed class JsonSessionRecordStore : ISessionRecordStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EtabExtension", "sidecar", "managed-etabs-session.json");

    public JsonSessionRecordStore(string? path = null) => FilePath = path ?? DefaultPath;

    public string FilePath { get; }

    public ManagedEtabsSessionRecord? Read()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<ManagedEtabsSessionRecord>(
                File.ReadAllText(FilePath), Options);
            return record is { SchemaVersion: 1 }
                && record.ManagedLaunchRecordId != Guid.Empty
                && record.Pid > 0
                && !string.IsNullOrWhiteSpace(record.ExecutablePath)
                ? record
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"⚠ Invalid managed ETABS session record: {ex.Message}");
            return null;
        }
    }

    public void Write(ManagedEtabsSessionRecord record)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
        File.Move(temp, FilePath, true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            var temp = FilePath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"⚠ Could not clear managed ETABS session record: {ex.Message}");
        }
    }
}

public interface IProcessInspector
{
    IReadOnlyList<ManagedProcessIdentity> SnapshotEtabs();
    ManagedProcessIdentity? Find(int pid);
    void Terminate(int pid);
    bool WaitForExit(int pid, TimeSpan timeout);
}

public sealed class WindowsProcessInspector : IProcessInspector
{
    public IReadOnlyList<ManagedProcessIdentity> SnapshotEtabs() =>
        Process.GetProcessesByName("ETABS")
            .Select(TryRead)
            .Where(identity => identity is not null)
            .Cast<ManagedProcessIdentity>()
            .ToList();

    public ManagedProcessIdentity? Find(int pid)
    {
        try { return TryRead(Process.GetProcessById(pid)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }
    }

    public void Terminate(int pid)
    {
        // The target may exit between identity verification and this call;
        // an already-gone process is a successful termination, not a crash.
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    public bool WaitForExit(int pid, TimeSpan timeout)
    {
        try { return Process.GetProcessById(pid).WaitForExit((int)timeout.TotalMilliseconds); }
        catch (ArgumentException) { return true; }
    }

    private static ManagedProcessIdentity? TryRead(Process process)
    {
        using (process)
        {
            try
            {
                var path = process.MainModule?.FileName;
                return string.IsNullOrWhiteSpace(path)
                    ? null
                    : new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            { return null; }
        }
    }
}

public interface IOrphanSessionCleaner
{
    void Clean();
}

public sealed class OrphanSessionCleaner(
    ISessionRecordStore records,
    IProcessInspector processes) : IOrphanSessionCleaner
{
    public void Clean()
    {
        var record = records.Read();
        if (record is null)
        {
            records.Clear();
            return;
        }

        var live = processes.Find(record.Pid);
        if (live is not null && IdentityMatches(record, live))
        {
            Console.Error.WriteLine(
                $"⚠ Managed ETABS orphan detected (PID {record.Pid}, launch {record.ManagedLaunchRecordId}). " +
                "Unsaved state is untrusted; terminating it. A clean reopen is required.");
            processes.Terminate(record.Pid);
            if (!processes.WaitForExit(record.Pid, TimeSpan.FromSeconds(10)))
                Console.Error.WriteLine($"⚠ Timed out waiting for managed ETABS orphan PID {record.Pid} to exit.");
        }
        else if (live is not null)
        {
            Console.Error.WriteLine($"⚠ Stale ETABS session record for PID {record.Pid}; identity tuple did not match. Process was not targeted.");
        }

        records.Clear();
    }

    internal static bool IdentityMatches(ManagedEtabsSessionRecord record, ManagedProcessIdentity live) =>
        record.Pid == live.Pid
        && record.ProcessStartTimeUtc.ToUniversalTime() == live.ProcessStartTimeUtc.ToUniversalTime()
        && string.Equals(Path.GetFullPath(record.ExecutablePath), Path.GetFullPath(live.ExecutablePath), StringComparison.OrdinalIgnoreCase);
}

public interface IManagedEtabsApplication : IDisposable
{
    ETABSApplication Application { get; }
    ManagedProcessIdentity Identity { get; }
    Guid ManagedLaunchRecordId { get; }
    void ExitWithoutSaving();
}

public sealed class ManagedEtabsApplication(
    ETABSApplication application,
    ManagedProcessIdentity identity,
    Guid launchRecordId) : IManagedEtabsApplication
{
    public ETABSApplication Application { get; } = application;
    public ManagedProcessIdentity Identity { get; } = identity;
    public Guid ManagedLaunchRecordId { get; } = launchRecordId;
    public void ExitWithoutSaving() => Application.Application.ApplicationExit(false);
    public void Dispose() => Application.Dispose();
}

public interface IManagedEtabsLauncher
{
    IManagedEtabsApplication Launch();
}

public sealed class ManagedEtabsLauncher(IProcessInspector processes) : IManagedEtabsLauncher
{
    public IManagedEtabsApplication Launch()
    {
        var before = processes.SnapshotEtabs().Select(x => x.Pid).ToHashSet();
        var app = ETABSWrapper.CreateNew()
            ?? throw new InvalidOperationException("Failed to start ETABS hidden instance.");
        try
        {
            if (app.Application.Visible()) app.Application.Hide();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            List<ManagedProcessIdentity> candidates;
            do
            {
                candidates = processes.SnapshotEtabs().Where(x => !before.Contains(x.Pid)).ToList();
                if (candidates.Count == 1)
                    return new ManagedEtabsApplication(app, candidates[0], Guid.NewGuid());
                Thread.Sleep(100);
            } while (DateTime.UtcNow < deadline && candidates.Count == 0);

            throw new InvalidOperationException(
                $"Could not uniquely identify the managed ETABS process after launch (candidates={candidates.Count}).");
        }
        catch
        {
            try { app.Application.ApplicationExit(false); } catch { }
            app.Dispose();
            throw;
        }
    }
}
