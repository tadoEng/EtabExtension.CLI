using System.Text.Json;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// The dispatcher of a recovery-only daemon (<c>serve --recover</c>, card v2.3 §4): it holds the
/// role lock, has run the orphan recovery, and answers ONLY the reads the host needs
/// (<c>get-status</c>, the operation lookups). Every other command is refused, so a recovery
/// daemon can never start, open, save or touch an ETABS - recovery may only clear or detach.
/// </summary>
public sealed class RecoveryOnlyDispatcher(IServeDispatcher inner) : IServeDispatcher
{
    public const string Code = "ETABS_RECOVERY_ONLY";

    private static readonly string[] Allowed = ["get-operation-events", "get-operation-status", "get-status"];

    public IReadOnlyCollection<string> Capabilities { get; } =
        inner.Capabilities.Where(name => Allowed.Contains(name, StringComparer.Ordinal)).ToArray();

    public Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct) =>
        Allowed.Contains(command, StringComparer.Ordinal)
            ? inner.DispatchAsync(command, request, ct)
            : Task.FromResult<object>(Result.Fail(
                $"[{Code}] This daemon only recovers an orphan record; '{command}' is refused. " +
                "No ETABS was started or touched."));
}
