using System.Text.Json.Serialization;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// What the daemon does when stdin ends with no prior <c>shutdown</c> (card C2).
/// </summary>
public enum ServeEofPolicy
{
    /// <summary>The bounded exit of C7a, whatever the instance's visibility.</summary>
    Exit,

    /// <summary>
    /// A user-visible instance is detached (C1); anything else takes the bounded exit. A
    /// detach that fails ends the daemon with the <c>userVisible</c> record left intact and
    /// exits NOTHING, so the next recovery detaches it (C3).
    /// </summary>
    DetachVisible
}

public static class ServeEofPolicies
{
    public const string ExitWire = "exit";
    public const string DetachVisibleWire = "detach-visible";

    /// <summary>Parses <c>--on-eof</c>. Null is the default (<see cref="ServeEofPolicy.Exit"/>); anything else unknown is an error.</summary>
    public static bool TryParse(string? wire, out ServeEofPolicy policy)
    {
        switch (wire)
        {
            case null:
            case ExitWire:
                policy = ServeEofPolicy.Exit;
                return true;
            case DetachVisibleWire:
                policy = ServeEofPolicy.DetachVisible;
                return true;
            default:
                policy = ServeEofPolicy.Exit;
                return false;
        }
    }
}

/// <summary>The request a dispatch is running for (its wire id and command).</summary>
public sealed record ServeRequestInfo(long Id, string Command);

/// <summary>
/// The id of the request being dispatched, flowed with the logical call. The dispatcher reads
/// it in the synchronous part of a handler (an abandoned work entry, a cancellable C10), where
/// the loop set it a moment earlier on the same call chain.
/// </summary>
public static class ServeRequestContext
{
    private static readonly AsyncLocal<ServeRequestInfo?> CurrentInfo = new();

    public static ServeRequestInfo? Current => CurrentInfo.Value;

    public static IDisposable Enter(ServeRequestInfo info)
    {
        var previous = CurrentInfo.Value;
        CurrentInfo.Value = info;
        return new Restore(previous);
    }

    private sealed class Restore(ServeRequestInfo? previous) : IDisposable
    {
        public void Dispose() => CurrentInfo.Value = previous;
    }
}

/// <summary>
/// Requests the daemon answered whose ETABS work it could not stop (card C5, <c>abandoned</c>):
/// a command a modal dialog is holding. Each entry reads <c>running</c> until its work
/// returns and <c>finished</c> afterwards; the host's drain evidence needs a finished entry.
/// </summary>
public sealed class AbandonedRequests
{
    private const int Limit = 32;

    private readonly object _gate = new();
    private readonly List<(long Id, string Command, Task Work)> _entries = [];

    public void Add(long requestId, string command, Task work)
    {
        lock (_gate)
        {
            if (_entries.Any(entry => entry.Id == requestId))
            {
                return;
            }

            _entries.Add((requestId, command, work));
            while (_entries.Count > Limit)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<AbandonedRequestInfo> Snapshot()
    {
        lock (_gate)
        {
            return _entries
                .Select(entry => new AbandonedRequestInfo(
                    entry.Id,
                    entry.Command,
                    entry.Work.IsCompleted ? AbandonedRequestState.Finished : AbandonedRequestState.Running))
                .ToList();
        }
    }
}

/// <summary>The successful answer to <c>detach</c> (card C1, step 7).</summary>
public sealed record DetachData(
    [property: JsonPropertyName("detached")] bool Detached,
    [property: JsonPropertyName("identity")] DetachedIdentity? Identity,
    [property: JsonPropertyName("launchRecordId")] Guid? LaunchRecordId,
    [property: JsonPropertyName("disposition")] EtabsDisposition Disposition = EtabsDisposition.Detached);

/// <summary>Exit codes of a recovery-only daemon (<c>serve --recover</c>, card v2.3 §4 stage 2).</summary>
public static class ServeRecoveryExitCodes
{
    /// <summary>Another daemon holds the role lock, or taking it timed out: no recovery ran.</summary>
    public const int RecordInUse = 75;

    /// <summary>The record directory is unsafe (a reparse point or a hard link): no recovery ran.</summary>
    public const int RecordPathUnsafe = 76;
}
