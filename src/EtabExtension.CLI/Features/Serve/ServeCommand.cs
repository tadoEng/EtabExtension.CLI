using System.CommandLine;
using System.Text.Json;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// How serve startup ended. <see cref="StartupRefused"/> means orphan recovery
/// fail-closed: the refusal frame was written, the request loop never ran, and
/// the retained recovery record is untouched.
/// </summary>
public enum ServeLifecycleOutcome
{
    Served,
    StartupRefused
}

public static class ServeCommand
{
    /// <summary>Nonzero exit for a refused startup — same 0/1 convention as the one-shot commands.</summary>
    internal const int StartupRefusedExitCode = 1;

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "serve",
            "Long-lived daemon: one owned ETABS instance shared across all requests. " +
            "Reads line-delimited JSON requests on stdin, writes one JSON response per line on stdout.");

        var sessionRecordOption = new Option<string?>("--session-record")
        {
            Description =
                "Path of this daemon's managed-session record. Default: the per-user " +
                "sidecar record. Give each of two daemons its own."
        };
        var siblingRecordOption = new Option<string?>("--sibling-record")
        {
            Description =
                "Path of the OTHER daemon's managed-session record. Its ETABS (exact pid, " +
                "start time and executable match) is a sibling, never external or managed."
        };
        var onEofOption = new Option<string?>("--on-eof")
        {
            Description =
                "What the daemon does when stdin ends with no prior shutdown: 'exit' (default; the " +
                "bounded exit) or 'detach-visible' (hand a user-visible ETABS to the user instead)."
        };
        var recoverOption = new Option<string?>("--recover")
        {
            Description =
                "Recovery-only daemon for this role record: takes the role lock first (5 s), then " +
                "recovers the orphan, and serves only get-status/shutdown. Never starts ETABS."
        };
        var recordAnchorOption = new Option<string?>("--record-anchor")
        {
            Description =
                "The root the record directory is trusted under. A reparse point above it is " +
                "accepted; one below it is refused. Default: the per-user app-data folder."
        };
        command.Options.Add(sessionRecordOption);
        command.Options.Add(siblingRecordOption);
        command.Options.Add(onEofOption);
        command.Options.Add(recoverOption);
        command.Options.Add(recordAnchorOption);

        command.SetAction(async parseResult =>
        {
            // One DI scope for the daemon's whole life so the shared session and the
            // (scoped) feature services live and die together.
            await using var scope = services.CreateAsyncScope();
            var provider = scope.ServiceProvider;

            // First, before anything resolves a record store or a census.
            var paths = provider.GetRequiredService<ServeSessionPaths>();
            var recoverPath = FullPathOrNull(parseResult.GetValue(recoverOption));
            paths.SessionRecordPath = recoverPath
                ?? FullPathOrNull(parseResult.GetValue(sessionRecordOption));
            paths.RecoveryOnly = recoverPath is not null;
            paths.TrustedAnchor = FullPathOrNull(parseResult.GetValue(recordAnchorOption));
            if (!ServeEofPolicies.TryParse(parseResult.GetValue(onEofOption), out var eofPolicy))
            {
                Console.Error.WriteLine(
                    $"✗ --on-eof must be '{ServeEofPolicies.ExitWire}' or '{ServeEofPolicies.DetachVisibleWire}'.");
                Environment.Exit(StartupRefusedExitCode);
            }

            paths.EofPolicy = eofPolicy;
            paths.SiblingRecordPath = FullPathOrNull(parseResult.GetValue(siblingRecordOption));
            if (string.Equals(
                    paths.SiblingRecordPath,
                    paths.SessionRecordPath ?? JsonSessionRecordStore.DefaultPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Early warning only (card I4). The authority is the published own identity,
                // which the census removes from the sibling set, so an alias this string
                // comparison cannot see (a junction, a hard link, a copied record) still
                // cannot make our own ETABS look like the sibling's.
                Console.Error.WriteLine("⚠ --sibling-record names this daemon's own record; ignored.");
                paths.SiblingRecordPath = null;
            }

            var dispatcher = provider.GetRequiredService<IServeDispatcher>();
            var orphanCleaner = provider.GetRequiredService<IOrphanSessionCleaner>();
            var shutdown = provider.GetRequiredService<IServeShutdownCoordinator>();
            var records = provider.GetRequiredService<ISessionRecordStore>();
            var startIntent = provider.GetRequiredService<IManagedEtabsStartIntentScope>();

            // Program.cs redirects Console.Out to stderr — write the protocol to the
            // REAL stdout. "\n" line endings keep framing clean for the Rust reader.
            await using var stdout = new StreamWriter(Console.OpenStandardOutput())
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var stdin = new StreamReader(Console.OpenStandardInput());

            if (paths.RecoveryOnly)
            {
                // Card v2.3 §4 stage 2: the role lock FIRST, before the record is read or any
                // process is touched. RecordInUse or a timeout ends this daemon with that
                // code; the host goes to Quarantined and neither retries nor kills.
                var locked = await RunRecoveryLockAsync(
                    records,
                    RecoveryLockWait,
                    stdout,
                    Console.Error,
                    recovery => ServeStartupRefusal.Current(recovery, records.FilePath));
                if (locked != 0)
                {
                    await stdout.FlushAsync();
                    Environment.Exit(locked);
                }

                dispatcher = new RecoveryOnlyDispatcher(dispatcher);
            }

            var outcome = await RunLifecycleAsync(
                orphanCleaner,
                shutdown,
                () => new ServeLoop(
                    dispatcher,
                    shutdown,
                    startIntent,
                    capabilities => ServeHandshake.Current(capabilities) with
                    {
                        RecordPath = records.FilePath
                    },
                    Console.Error,
                    paths.EofPolicy).RunAsync(stdin, stdout),
                stdout,
                Console.Error,
                recovery => ServeStartupRefusal.Current(recovery, records.FilePath));

            if (outcome == ServeLifecycleOutcome.StartupRefused)
            {
                await stdout.FlushAsync();
                Environment.Exit(StartupRefusedExitCode);
            }
        });

        return command;
    }

    private static string? FullPathOrNull(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    /// <summary>The bounded try-acquire of the role lock by a recovery daemon (card v2.3 §4).</summary>
    public static readonly TimeSpan RecoveryLockWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stage 2 of recovery (card v2.3 §4): the recovery daemon's FIRST action. It try-acquires
    /// the role lock for at most <paramref name="wait"/> BEFORE the record is read or any
    /// process is touched. Returns 0 when the lock is held; otherwise the refusal frame was
    /// written and the exit code to end with is returned (<see cref="ServeRecoveryExitCodes"/>):
    /// no retry loop, no kill, no read of the record.
    /// </summary>
    internal static async Task<int> RunRecoveryLockAsync(
        ISessionRecordStore records,
        TimeSpan wait,
        TextWriter output,
        TextWriter diagnostics,
        Func<ManagedEtabsShutdownResult, ServeStartupRefusal> refusalFactory)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(refusalFactory);

        string code;
        string message;
        int exitCode;
        try
        {
            records.AcquireExclusive(wait);
            return 0;
        }
        catch (RecordInUseException ex)
        {
            (code, message, exitCode) = (EtabsRecordErrorCodes.RecordInUse, ex.Message, ServeRecoveryExitCodes.RecordInUse);
        }
        catch (RecordPathUnsafeException ex)
        {
            (code, message, exitCode) = (EtabsRecordErrorCodes.RecordPathUnsafe, ex.Message, ServeRecoveryExitCodes.RecordPathUnsafe);
        }

        var refusal = refusalFactory(OrphanSessionCleaner.LockFailed(code, message));
        await output.WriteLineAsync(JsonSerializer.Serialize(refusal, ServeJson.Options));
        await output.FlushAsync();
        await diagnostics.WriteLineAsync(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
            refusal.Error,
            $"startup=refused; recovery=lock-not-taken; code={refusal.ErrorCode}"));
        await diagnostics.FlushAsync();
        return exitCode;
    }

    /// <summary>
    /// Runs orphan recovery, then the request loop, then converges on the one
    /// idempotent shutdown coordinator.
    ///
    /// <para>Recovery runs before the loop on purpose — a daemon must not serve
    /// requests while a previous managed ETABS process may still be alive. When it
    /// fails closed, startup is still <b>protocol-visible</b>: exactly one
    /// <c>refused</c> frame is written to <paramref name="output"/>, no <c>ready</c>
    /// handshake is emitted, no request is read, and the retained recovery record is
    /// left alone. A consumer therefore sees a typed refusal instead of an opaque
    /// process exit, and can stop respawning into the same refusal.</para>
    /// </summary>
    internal static async Task<ServeLifecycleOutcome> RunLifecycleAsync(
        IOrphanSessionCleaner orphanCleaner,
        IServeShutdownCoordinator shutdown,
        Func<Task> runLoop,
        TextWriter output,
        TextWriter diagnostics,
        Func<ManagedEtabsShutdownResult, ServeStartupRefusal> refusalFactory)
    {
        ArgumentNullException.ThrowIfNull(orphanCleaner);
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(runLoop);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(refusalFactory);

        try
        {
            var recovery = orphanCleaner.Clean();
            if (!recovery.Success)
            {
                var refusal = refusalFactory(recovery);
                await output.WriteLineAsync(
                    JsonSerializer.Serialize(refusal, ServeJson.Options));
                await output.FlushAsync();
                await diagnostics.WriteLineAsync(
                    EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                        refusal.Error,
                        $"startup=refused; code={refusal.ErrorCode}; " +
                        $"state={refusal.State}; recordRetained={refusal.RecordRetained}; " +
                        $"ownedPid={refusal.OwnedPid?.ToString() ?? "unknown"}"));
                await diagnostics.FlushAsync();
                return ServeLifecycleOutcome.StartupRefused;
            }

            await runLoop();
            return ServeLifecycleOutcome.Served;
        }
        finally
        {
            try
            {
                var cleanup = await shutdown.ShutdownAsync();
                if (!cleanup.Success)
                {
                    var diagnostic = cleanup.Error
                        ?? "Serve cleanup failed without a diagnostic.";
                    await diagnostics.WriteLineAsync(
                        EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                            diagnostic,
                            $"state={cleanup.Data?.State.ToString() ?? "unknown"}; " +
                            $"processExitConfirmed={cleanup.Data?.ProcessExitConfirmed ?? false}; " +
                            $"recordRetained={cleanup.Data?.RecordRetained ?? true}"));
                }
                await diagnostics.FlushAsync();
            }
            catch (Exception cleanupException)
            {
                await diagnostics.WriteLineAsync(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IServeShutdownCoordinator.ShutdownAsync",
                        cleanupException));
                await diagnostics.FlushAsync();
            }
        }
    }
}
