using EtabExtension.CLI.Shared.Infrastructure.Etabs;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public static class ManagedEtabsShutdownErrorCodes
{
    public const string ApplicationExitFailed = "ETABS_APPLICATION_EXIT_FAILED";
    public const string IdentityMismatch = "ETABS_SHUTDOWN_IDENTITY_MISMATCH";
    public const string ProcessExitUnconfirmed = "ETABS_PROCESS_EXIT_UNCONFIRMED";
}

public enum ManagedEtabsShutdownState
{
    Succeeded,
    ApplicationExitFailed,
    IdentityMismatch,
    ProcessExitUnconfirmed
}

public sealed record ManagedEtabsShutdownData(
    ManagedEtabsShutdownState State,
    bool ProcessExitConfirmed,
    bool Forced,
    bool RecordRetained,
    int? ApplicationExitReturnCode,
    int? OwnedPid);

public sealed record ManagedEtabsShutdownResult(
    bool Success,
    string? ErrorCode,
    string? Error,
    ManagedEtabsShutdownData Data);

public interface IManagedEtabsShutdownMachine
{
    ManagedEtabsShutdownResult Shutdown(IManagedEtabsApplication owned);
    ManagedEtabsShutdownResult ShutdownAfterRecoveryRecordWriteFailure(
        IManagedEtabsApplication owned);

    /// <summary>
    /// <see cref="Shutdown"/> for a session whose STA worker is stuck inside a CSI call that
    /// a modal ETABS dialog is holding (review finding P2-1). The same sequence, with one
    /// difference: <c>ApplicationExit(false)</c> is attempted on a fresh dedicated STA thread
    /// and abandoned if it has not returned within <see cref="ManagedEtabsShutdownMachine.GracefulExitTimeout"/>
    /// - an ETABS sitting in a modal loop may never answer it - and the exact owned process
    /// is then killed through its authoritative handle.
    /// </summary>
    ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked(IManagedEtabsApplication owned);
}

public sealed class ManagedEtabsShutdownMachine : IManagedEtabsShutdownMachine
{
    /// <summary>
    /// The normal path always asks ETABS to exit (<c>ApplicationExit(false)</c>) and waits
    /// this long BEFORE any kill: a killed ETABS may not release its licence seat. The Rust
    /// client's 20 s <c>CLI_SHUTDOWN_ENVELOPE</c> assumes this value.
    /// </summary>
    public static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The graceful bound when the worker is stuck behind a dialog (card I5): the exit
    /// request is made on a dedicated STA thread and abandoned after this long, because an
    /// ETABS in a modal loop may never answer it. Keeps the blocked worst case at 22 s.
    /// </summary>
    public static readonly TimeSpan BlockedGracefulExitTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    private readonly ISessionRecordStore _records;
    private readonly TimeSpan _gracefulExitTimeout;
    private readonly TimeSpan _blockedGracefulExitTimeout;

    public ManagedEtabsShutdownMachine(ISessionRecordStore records)
        : this(records, GracefulExitTimeout, BlockedGracefulExitTimeout)
    {
    }

    /// <summary>Tests shorten the graceful bound; production always uses the defaults.</summary>
    internal ManagedEtabsShutdownMachine(ISessionRecordStore records, TimeSpan gracefulExitTimeout)
        : this(records, gracefulExitTimeout, BlockedGracefulExitTimeout)
    {
    }

    internal ManagedEtabsShutdownMachine(
        ISessionRecordStore records,
        TimeSpan gracefulExitTimeout,
        TimeSpan blockedGracefulExitTimeout)
    {
        _records = records;
        _gracefulExitTimeout = gracefulExitTimeout;
        _blockedGracefulExitTimeout = blockedGracefulExitTimeout;
    }

    public ManagedEtabsShutdownResult Shutdown(IManagedEtabsApplication owned)
        => ShutdownCore(owned, ShutdownAuthority.RequireMatchingRecoveryRecord, ExitAttempt.OnCallingThread);

    public ManagedEtabsShutdownResult ShutdownAfterRecoveryRecordWriteFailure(
        IManagedEtabsApplication owned)
        => ShutdownCore(owned, ShutdownAuthority.AuthoritativeOwnedHandle, ExitAttempt.OnCallingThread);

    public ManagedEtabsShutdownResult ShutdownWhileWorkerBlocked(IManagedEtabsApplication owned)
        => ShutdownCore(owned, ShutdownAuthority.RequireMatchingRecoveryRecord, ExitAttempt.BoundedOnDedicatedSta);

    private ManagedEtabsShutdownResult ShutdownCore(
        IManagedEtabsApplication owned,
        ShutdownAuthority authority,
        ExitAttempt exitAttempt)
    {
        ArgumentNullException.ThrowIfNull(owned);

        // Deterministic, unconditional, and first. Every route into shutdown — graceful,
        // forced, identity-mismatched, or a session that had already exited — retires the
        // Windows window suppression before anything else is attempted, so no sweep thread
        // outlives the session it was created for and nothing can be suppressed against a
        // pid that is about to go back to the OS. Disposal restores nothing: a process on
        // its way out must not flash a window.
        owned.DisposeWindowGuard();

        var record = _records.Read();
        var recordMatchesOwned = IdentityMatches(record, owned);

        if (authority == ShutdownAuthority.RequireMatchingRecoveryRecord
            && !recordMatchesOwned)
        {
            return Failed(
                ManagedEtabsShutdownState.IdentityMismatch,
                ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                "Managed ETABS recovery record does not match the authoritative owned process handle.",
                owned.Identity.Pid,
                processExitConfirmed: false,
                forced: false,
                recordRetained: record is not null,
                applicationExitReturnCode: null);
        }

        if (owned.HasExited)
        {
            var recordRetained = ClearMatchingRecord(record, recordMatchesOwned);
            owned.ReleaseOwnedProcessHandle();

            // Exit is already confirmed, so the passive reference cleanup still applies —
            // an early exit is not a reason to leave COM references held.
            owned.ReleaseApiReferences();
            return Succeeded(
                owned.Identity.Pid,
                forced: false,
                applicationExitReturnCode: null,
                recordRetained);
        }

        int? applicationExitReturnCode = null;
        string? applicationExitError = null;
        TimeSpan? gracefulWait = _gracefulExitTimeout;
        if (exitAttempt == ExitAttempt.BoundedOnDedicatedSta)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var attempt = AttemptExitOnDedicatedSta(owned, _blockedGracefulExitTimeout);
            if (attempt.Returned && attempt.Failure is null)
            {
                applicationExitReturnCode = attempt.ReturnCode;
                applicationExitError = applicationExitReturnCode is { } code and not 0
                    ? EtabsApiDiagnosticFormatter.ApiReturn("cOAPI.ApplicationExit(false)", code)
                    : null;
                var remaining = _blockedGracefulExitTimeout - started.Elapsed;
                gracefulWait = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            else if (attempt.Failure is not null)
            {
                // Also expected here: the RCW belongs to the stuck worker's apartment, so a
                // call from this thread typically throws (RPC_E_WRONG_THREAD) without ever
                // reaching ETABS. No exit was requested, so there is nothing to wait for and
                // nothing to report as an exit failure - straight to the kill.
                Console.Error.WriteLine(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.Exception("cOAPI.ApplicationExit(false)", attempt.Failure),
                    "session blocked; exit not deliverable from the shutdown thread; " +
                    $"killing the exact owned ETABS (PID {owned.Identity.Pid})"));
                gracefulWait = null;
            }
            else
            {
                // Expected while a modal box holds ETABS: the exit request was made and not
                // answered. Not an exit FAILURE - the bounded sequence moves on to the kill,
                // exactly as it does when an answered exit does not end the process in time.
                Console.Error.WriteLine(
                    "⚠ cOAPI.ApplicationExit(false) did not return within " +
                    $"{_blockedGracefulExitTimeout.TotalSeconds:0.###}s while the session was blocked; " +
                    $"killing the exact owned ETABS (PID {owned.Identity.Pid}).");
                // The graceful bound is already spent on the unanswered request.
                gracefulWait = null;
            }
        }
        else
        {
            try
            {
                applicationExitReturnCode = owned.ExitWithoutSaving();
                if (applicationExitReturnCode != 0)
                {
                    applicationExitError = EtabsApiDiagnosticFormatter.ApiReturn(
                        "cOAPI.ApplicationExit(false)",
                        applicationExitReturnCode.Value);
                }
            }
            catch (Exception exception)
            {
                applicationExitError = EtabsApiDiagnosticFormatter.Exception(
                    "cOAPI.ApplicationExit(false)",
                    exception);
            }
        }

        var processExitConfirmed = gracefulWait is { } wait && TryWait(owned, wait);
        var forced = false;
        string? cleanupError = null;
        if (!processExitConfirmed)
        {
            forced = true;
            try
            {
                owned.Kill();
            }
            catch (Exception exception)
            {
                cleanupError = EtabsApiDiagnosticFormatter.Exception(
                    "Process.Kill(authoritative-owned-handle)",
                    exception);
            }

            processExitConfirmed = TryWait(owned, ForcedExitTimeout);
        }

        if (!processExitConfirmed)
        {
            return Failed(
                ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                cleanupError ?? applicationExitError
                    ?? "The exact owned ETABS process did not confirm exit after graceful and forced cleanup.",
                owned.Identity.Pid,
                processExitConfirmed: false,
                forced,
                recordRetained: record is not null,
                applicationExitReturnCode);
        }

        var confirmedRecordRetained = ClearMatchingRecord(record, recordMatchesOwned);
        owned.ReleaseOwnedProcessHandle();

        // Passive COM-reference cleanup, never a substitute for the exit above: only after
        // the authoritative ApplicationExit(false) and a confirmed process exit.
        owned.ReleaseApiReferences();
        return applicationExitError is null
            ? Succeeded(
                owned.Identity.Pid,
                forced,
                applicationExitReturnCode,
                confirmedRecordRetained)
            : Failed(
                ManagedEtabsShutdownState.ApplicationExitFailed,
                ManagedEtabsShutdownErrorCodes.ApplicationExitFailed,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    applicationExitError,
                    $"forced={forced.ToString().ToLowerInvariant()}; processExitConfirmed=true"),
                owned.Identity.Pid,
                processExitConfirmed: true,
                forced,
                recordRetained: confirmedRecordRetained,
                applicationExitReturnCode);
    }

    private bool ClearMatchingRecord(
        ManagedEtabsSessionRecord? record,
        bool recordMatchesOwned)
    {
        if (recordMatchesOwned)
        {
            _records.Clear();
            return false;
        }

        return record is not null;
    }

    private static bool IdentityMatches(
        ManagedEtabsSessionRecord? record,
        IManagedEtabsApplication owned) =>
        record is not null
        && record.ManagedLaunchRecordId == owned.ManagedLaunchRecordId
        && OrphanSessionCleaner.IdentityMatches(record, owned.Identity);

    private static bool TryWait(IManagedEtabsApplication owned, TimeSpan timeout)
    {
        try
        {
            return owned.WaitForExit(timeout);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(EtabsApiDiagnosticFormatter.Exception(
                "Process.WaitForExit(authoritative-owned-handle)",
                exception));
            return false;
        }
    }

    private static ManagedEtabsShutdownResult Succeeded(
        int ownedPid,
        bool forced,
        int? applicationExitReturnCode,
        bool recordRetained) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: recordRetained,
            ApplicationExitReturnCode: applicationExitReturnCode,
            OwnedPid: ownedPid));

    private static ManagedEtabsShutdownResult Failed(
        ManagedEtabsShutdownState state,
        string errorCode,
        string error,
        int? ownedPid,
        bool processExitConfirmed,
        bool forced,
        bool recordRetained,
        int? applicationExitReturnCode) => new(
        false,
        errorCode,
        error,
        new(
            state,
            processExitConfirmed,
            forced,
            recordRetained,
            applicationExitReturnCode,
            ownedPid));

    /// <summary>
    /// Runs <c>ApplicationExit(false)</c> on a fresh background STA thread and waits at most
    /// <paramref name="bound"/>. A thread that never returns is abandoned: it is a background
    /// thread, it holds no lock, and the process it is calling is about to be killed.
    /// </summary>
    private static ExitAttemptOutcome AttemptExitOnDedicatedSta(
        IManagedEtabsApplication owned,
        TimeSpan bound)
    {
        int? returnCode = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                returnCode = owned.ExitWithoutSaving();
            }
#pragma warning disable CA1031 // reported to the caller as the exit failure
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "etab-cli-blocked-shutdown-exit"
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        // Join is the memory barrier for returnCode/failure; they are read only after it.
        return thread.Join(bound)
            ? new ExitAttemptOutcome(true, returnCode, failure)
            : new ExitAttemptOutcome(false, null, null);
    }

    private readonly record struct ExitAttemptOutcome(bool Returned, int? ReturnCode, Exception? Failure);

    private enum ShutdownAuthority
    {
        RequireMatchingRecoveryRecord,
        AuthoritativeOwnedHandle
    }

    private enum ExitAttempt
    {
        /// <summary>The normal path: on the session's own STA worker, unbounded.</summary>
        OnCallingThread,

        /// <summary>The worker is stuck behind a dialog: dedicated STA, bounded, then kill.</summary>
        BoundedOnDedicatedSta
    }
}
