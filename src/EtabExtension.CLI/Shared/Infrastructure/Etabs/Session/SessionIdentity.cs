using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtabExtension.CLI.Features.GetStatus.Models;
using Microsoft.Extensions.Configuration;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public sealed record ManagedProcessIdentity(
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath);

public sealed record EtabsProcessObservation(
    IReadOnlyList<ManagedProcessIdentity> Identified,
    int UnidentifiedCount)
{
    /// <summary>
    /// The identified processes that exactly match the sibling daemon's record
    /// (<see cref="EtabsSiblingCensus.Classify"/>). A subset of <see cref="Identified"/>.
    /// </summary>
    public IReadOnlyList<ManagedProcessIdentity> Siblings { get; init; } = [];

    /// <summary><see cref="Identified"/> without the sibling's process.</summary>
    public IReadOnlyList<ManagedProcessIdentity> NonSibling =>
        Identified.Where(identity => !Siblings.Contains(identity)).ToList();
}

public static class EtabsOwnershipResolver
{
    /// <summary>
    /// Managed iff identified ∖ (own ∪ siblings) = ∅ and our own process was seen;
    /// External when an identified pid is neither ours nor the sibling's; Ambiguous
    /// whenever an ETABS could not be identified at all. A sibling is never Managed or
    /// External.
    /// </summary>
    public static EtabsInstanceOwnership Resolve(
        EtabsProcessObservation observation,
        int? managedPid)
    {
        if (observation.UnidentifiedCount > 0)
        {
            return EtabsInstanceOwnership.Ambiguous;
        }

        var nonSibling = observation.NonSibling;
        if (nonSibling.Count == 0)
        {
            return EtabsInstanceOwnership.None;
        }

        return nonSibling.All(identity => identity.Pid == managedPid)
            ? EtabsInstanceOwnership.Managed
            : EtabsInstanceOwnership.External;
    }
}

/// <summary>
/// Where in its life a record's launch is. Diagnostics only: an older reader ignores the
/// field, and nothing decides ownership from it.
/// </summary>
public enum ManagedEtabsRecordPhase
{
    /// <summary>Written immediately after <c>Process.Start</c>, before the attach.</summary>
    Provisional,

    /// <summary>Written at commit, once the session is API-ready.</summary>
    Ready
}

public sealed record ManagedEtabsSessionRecord(
    int SchemaVersion,
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath,
    Guid ManagedLaunchRecordId,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>Optional: absent in records written before the provisional phase existed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManagedEtabsRecordPhase? Phase { get; init; }
}

public interface ISessionRecordStore
{
    /// <summary>
    /// Where the recovery record lives. Reported in the startup refusal frame so a
    /// consumer can name the retained evidence instead of guessing at it.
    /// </summary>
    string FilePath { get; }

    ManagedEtabsSessionRecord? Read();
    void Write(ManagedEtabsSessionRecord record);
    void Clear();

    /// <summary>
    /// Takes this daemon's exclusive claim on the record directory for its whole life
    /// (card V3). Idempotent. A store without a claim to take (a test double) ignores it.
    /// Throws <see cref="RecordInUseException"/> or <see cref="RecordPathUnsafeException"/>.
    /// </summary>
    void AcquireExclusive()
    {
    }
}

public sealed class JsonSessionRecordStore : ISessionRecordStore, IDisposable
{
    private readonly object _lockGate = new();
    private DaemonRecordLock? _lock;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EtabExtension", "sidecar", "managed-etabs-session.json");

    private readonly TimeSpan _lockWait;

    public JsonSessionRecordStore(string? path = null, TimeSpan? lockWait = null)
    {
        FilePath = path ?? DefaultPath;
        _lockWait = lockWait ?? DaemonRecordLock.DefaultWait;
    }

    public string FilePath { get; }

    /// <summary>True while this store holds the directory's <c>daemon.lock</c>.</summary>
    public bool HoldsExclusiveLock
    {
        get
        {
            lock (_lockGate)
            {
                return _lock is not null;
            }
        }
    }

    public void AcquireExclusive() => AcquireExclusive(_lockWait);

    internal void AcquireExclusive(TimeSpan wait)
    {
        lock (_lockGate)
        {
            _lock ??= DaemonRecordLock.Acquire(FilePath, wait);
        }
    }

    public void Dispose()
    {
        lock (_lockGate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    private void RequireLock()
    {
        if (!HoldsExclusiveLock)
        {
            throw new InvalidOperationException(
                "The record directory lock is not held; only the lock holder may write or clear " +
                $"'{FilePath}'.");
        }
    }

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
        RequireLock();
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
        File.Move(temp, FilePath, true);
    }

    public void Clear()
    {
        RequireLock();
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
    EtabsProcessObservation ObserveEtabs();
    ManagedProcessIdentity? Find(int pid);

    /// <summary>
    /// Opens an authoritative handle on the process matching <paramref name="expected"/>
    /// exactly — pid, start time and executable path. Returns null when the live process
    /// does not match, so ownership is never assumed from a pid alone.
    /// </summary>
    IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected);

    ExactProcessTerminationResult TerminateExact(
        ManagedProcessIdentity expected,
        TimeSpan timeout);
}

public enum ExactProcessTerminationState
{
    NotFound,
    ConfirmedGone,
    IdentityMismatchOrUnidentified,
    ExitUnconfirmed
}

public sealed record ExactProcessTerminationResult(
    ExactProcessTerminationState State,
    ManagedProcessIdentity? ObservedIdentity);

public sealed class WindowsProcessInspector : IProcessInspector
{
    public EtabsProcessObservation ObserveEtabs()
    {
        var identified = new List<ManagedProcessIdentity>();
        var unidentifiedCount = 0;
        foreach (var process in Process.GetProcessesByName("ETABS"))
        {
            using (process)
            {
                var identity = TryRead(process);
                if (identity is null)
                {
                    unidentifiedCount++;
                }
                else
                {
                    identified.Add(identity);
                }
            }
        }

        return new(identified, unidentifiedCount);
    }

    public ManagedProcessIdentity? Find(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return TryRead(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }
    }

    public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        Process process;
        try
        {
            process = Process.GetProcessById(expected.Pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }

        var observed = TryRead(process);
        if (observed is null || !IdentityMatches(expected, observed))
        {
            process.Dispose();
            return null;
        }

        return new WindowsOwnedEtabsProcess(process, observed);
    }

    public ExactProcessTerminationResult TerminateExact(
        ManagedProcessIdentity expected,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(expected);

        Process process;
        try
        {
            process = Process.GetProcessById(expected.Pid);
        }
        catch (ArgumentException)
        {
            return new(ExactProcessTerminationState.NotFound, ObservedIdentity: null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new(
                ExactProcessTerminationState.IdentityMismatchOrUnidentified,
                ObservedIdentity: null);
        }

        using (process)
        {
            var observed = TryRead(process);
            if (observed is null || !IdentityMatches(expected, observed))
            {
                return new(
                    ExactProcessTerminationState.IdentityMismatchOrUnidentified,
                    observed);
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                return process.HasExited
                    || process.WaitForExit(checked((int)timeout.TotalMilliseconds))
                    ? new(ExactProcessTerminationState.ConfirmedGone, observed)
                    : new(ExactProcessTerminationState.ExitUnconfirmed, observed);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return HasExited(process)
                    ? new(ExactProcessTerminationState.ConfirmedGone, observed)
                    : new(ExactProcessTerminationState.ExitUnconfirmed, observed);
            }
        }
    }

    private static ManagedProcessIdentity? TryRead(Process process)
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

    private static bool IdentityMatches(
        ManagedProcessIdentity expected,
        ManagedProcessIdentity observed) =>
        expected.Pid == observed.Pid
        && expected.ProcessStartTimeUtc.ToUniversalTime()
            == observed.ProcessStartTimeUtc.ToUniversalTime()
        && string.Equals(
            Path.GetFullPath(expected.ExecutablePath),
            Path.GetFullPath(observed.ExecutablePath),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        { return false; }
    }
}

public interface IOrphanSessionCleaner
{
    ManagedEtabsShutdownResult Clean();
}

public sealed class OrphanSessionCleaner(
    ISessionRecordStore records,
    IProcessInspector processes) : IOrphanSessionCleaner
{
    public ManagedEtabsShutdownResult Clean()
    {
        // Recovery is only ever run by the daemon that holds the record directory's lock
        // (card V3): only a lock holder writes the record, so only a lock holder may judge
        // what it says. Without the lock nothing is read, killed or cleared.
        try
        {
            records.AcquireExclusive();
        }
        catch (RecordInUseException ex)
        {
            return LockFailed(EtabsRecordErrorCodes.RecordInUse, ex.Message);
        }
        catch (RecordPathUnsafeException ex)
        {
            return LockFailed(EtabsRecordErrorCodes.RecordPathUnsafe, ex.Message);
        }

        var record = records.Read();
        if (record is null)
        {
            records.Clear();
            return Succeeded(ownedPid: null, forced: false);
        }

        var expected = new ManagedProcessIdentity(
            record.Pid,
            record.ProcessStartTimeUtc,
            record.ExecutablePath);
        var termination = processes.TerminateExact(expected, TimeSpan.FromSeconds(10));
        switch (termination.State)
        {
            case ExactProcessTerminationState.NotFound:
                records.Clear();
                return Succeeded(record.Pid, forced: false);
            case ExactProcessTerminationState.ConfirmedGone:
                records.Clear();
                return Succeeded(record.Pid, forced: true);
            case ExactProcessTerminationState.IdentityMismatchOrUnidentified:
                return Failed(
                    ManagedEtabsShutdownState.IdentityMismatch,
                    ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                    "Managed ETABS orphan identity was mismatched or unreadable; process was not targeted.",
                    record.Pid,
                    forced: false);
            case ExactProcessTerminationState.ExitUnconfirmed:
                return Failed(
                    ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                    ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                    "Exact-handle orphan termination did not confirm process exit.",
                    record.Pid,
                    forced: true);
            default:
                throw new InvalidOperationException(
                    $"Unknown exact process termination state: {termination.State}.");
        }
    }

    private static ManagedEtabsShutdownResult LockFailed(string errorCode, string error) => new(
        false,
        errorCode,
        error,
        new(
            ManagedEtabsShutdownState.IdentityMismatch,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: null));

    private static ManagedEtabsShutdownResult Succeeded(int? ownedPid, bool forced) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: false,
            ApplicationExitReturnCode: null,
            OwnedPid: ownedPid));

    private static ManagedEtabsShutdownResult Failed(
        ManagedEtabsShutdownState state,
        string errorCode,
        string error,
        int ownedPid,
        bool forced) => new(
        false,
        errorCode,
        error,
        new(
            state,
            ProcessExitConfirmed: false,
            Forced: forced,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: ownedPid));

    internal static bool IdentityMatches(ManagedEtabsSessionRecord record, ManagedProcessIdentity live) =>
        record.Pid == live.Pid
        && record.ProcessStartTimeUtc.ToUniversalTime() == live.ProcessStartTimeUtc.ToUniversalTime()
        && string.Equals(Path.GetFullPath(record.ExecutablePath), Path.GetFullPath(live.ExecutablePath), StringComparison.OrdinalIgnoreCase);
}
