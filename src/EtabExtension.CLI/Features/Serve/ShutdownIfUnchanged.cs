using System.Text.Json.Serialization;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>Typed refusals of <c>shutdown-if-unchanged</c> (card v2.3 §2, v2.4 §2, §3).</summary>
public static class ShutdownIfUnchangedCodes
{
    /// <summary>A modal dialog of the owned ETABS is open; nothing was exited.</summary>
    public const string ModalOpen = "ETABS_MODAL_OPEN";

    /// <summary>The document or its change signal moved since the save the caller reports.</summary>
    public const string ChangedSinceSave = "ETABS_CHANGED_SINCE_SAVE";

    /// <summary>The check could not be completed (no signal, a COM error); nothing was exited.</summary>
    public const string CheckInconclusive = "ETABS_CHANGE_CHECK_INCONCLUSIVE";

    /// <summary>
    /// The save stamp is unverified, so no "unchanged" assurance exists, and the caller did not
    /// state that edits after the save may be discarded (card v2.4 §3).
    /// </summary>
    public const string DiscardNotConfirmed = "ETABS_DISCARD_NOT_CONFIRMED";

    /// <summary>The check exceeded its own budget before the exit was won; nothing was exited.</summary>
    public const string CheckTimeout = "ETABS_C10_TIMEOUT";

    /// <summary>A cancel, a detach, a shutdown or a timeout won the arbiter; this command never exits.</summary>
    public const string Abandoned = "ETABS_C10_ABANDONED";

    /// <summary>Another <c>shutdown-if-unchanged</c> is still in flight.</summary>
    public const string Busy = "ETABS_C10_BUSY";
}

/// <summary>The lifecycle of one <c>shutdown-if-unchanged</c> (card v2.4 §2).</summary>
public enum ShutdownIfUnchangedState
{
    /// <summary>Reading the dialog state, the document and the change signal. Non-destructive.</summary>
    Checking = 0,

    /// <summary>The exit CAS was won: <c>ApplicationExit</c> is being (or was) called.</summary>
    Exiting = 1,

    /// <summary>The exit ran to its answer.</summary>
    Ended = 2,

    /// <summary>A modal, a change, or inconclusive evidence refused the exit.</summary>
    Refused = 3,

    /// <summary>A cancel, a detach, a shutdown or a timeout took the arbiter first.</summary>
    Abandoned = 4
}

/// <summary>
/// One command's arbiter. <c>ApplicationExit</c> is called ONLY after
/// <see cref="TryEnterExiting"/> wins <c>Checking → Exiting</c>; every rival
/// (<see cref="TryAbandon"/>, <see cref="TryRefuse"/>) wins <c>Checking</c> away from it, and
/// a late Checking continuation that finds anything but Checking never exits.
/// </summary>
public sealed class ShutdownIfUnchangedOperation(long requestId)
{
    private int _state;
    private readonly TaskCompletionSource _abandoned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long RequestId { get; } = requestId;

    /// <summary>Completes the moment a cancel, a detach, a shutdown or a timeout abandons the command.</summary>
    public Task Abandoned => _abandoned.Task;

    public ShutdownIfUnchangedState State => (ShutdownIfUnchangedState)Volatile.Read(ref _state);

    public bool TryEnterExiting() => Move(ShutdownIfUnchangedState.Checking, ShutdownIfUnchangedState.Exiting);

    public bool TryAbandon()
    {
        var won = Move(ShutdownIfUnchangedState.Checking, ShutdownIfUnchangedState.Abandoned);
        if (won)
        {
            _ = _abandoned.TrySetResult();
        }

        return won;
    }

    public bool TryRefuse() => Move(ShutdownIfUnchangedState.Checking, ShutdownIfUnchangedState.Refused);

    public void MarkEnded() => Move(ShutdownIfUnchangedState.Exiting, ShutdownIfUnchangedState.Ended);

    private bool Move(ShutdownIfUnchangedState from, ShutdownIfUnchangedState to) =>
        Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
}

/// <summary>The daemon's single in-flight <c>shutdown-if-unchanged</c>, reachable by every rival.</summary>
public sealed class ShutdownIfUnchangedRegistry
{
    private readonly object _gate = new();
    private ShutdownIfUnchangedOperation? _current;

    /// <summary>Starts an operation, or returns null while another is still in flight.</summary>
    public ShutdownIfUnchangedOperation? Begin(long requestId)
    {
        lock (_gate)
        {
            if (_current is { State: ShutdownIfUnchangedState.Checking or ShutdownIfUnchangedState.Exiting })
            {
                return null;
            }

            return _current = new ShutdownIfUnchangedOperation(requestId);
        }
    }

    /// <summary>
    /// A detach, a shutdown or EOF abandons a C10 that is still Checking. Returns true when
    /// one was abandoned. A C10 already Exiting is untouched: its exit is already won.
    /// </summary>
    public bool AbandonCurrent()
    {
        lock (_gate)
        {
            return _current?.TryAbandon() ?? false;
        }
    }

    /// <summary>
    /// <c>cancel-operation</c> naming the request. Returns the state the operation was left in,
    /// or null when no C10 of that request exists.
    /// </summary>
    public ShutdownIfUnchangedState? Cancel(long requestId)
    {
        lock (_gate)
        {
            if (_current is null || _current.RequestId != requestId)
            {
                return null;
            }

            _ = _current.TryAbandon();
            return _current.State;
        }
    }
}

public sealed record ExpectedDocument(
    [property: JsonPropertyName("fileId")] string? FileId)
{
    /// <summary>
    /// The document's full path (card v2.5a A1: <c>expectedDocument</c> is <c>{path, fileId}</c>).
    /// Optional on the older commands, which compare the file id alone.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

public sealed class ShutdownIfUnchangedRequest
{
    /// <summary>The saved file (the working <c>.edb</c>): the open document must be this model.</summary>
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("expectedDocument")] public ExpectedDocument? ExpectedDocument { get; init; }

    /// <summary>The stamp <c>save-model</c> returned; absent or unverified selects the discard fallback.</summary>
    [JsonPropertyName("savedStamp")] public SavedStamp? SavedStamp { get; init; }

    /// <summary>
    /// Required when the stamp is unverified: the caller states the user was told that edits
    /// made after the save will be discarded (card v2.4 §3).
    /// </summary>
    [JsonPropertyName("discardConfirmed")] public bool DiscardConfirmed { get; init; }
}

/// <summary>Reads the open document and the change signal of the session's ETABS.</summary>
public interface IShutdownIfUnchangedApiFactory
{
    IEtabsModelFileApi Create(IEtabsSession session);
}

/// <summary>
/// <c>shutdown-if-unchanged</c>, the C10 of cards v2.3/v2.4: ONE STA command that refuses on
/// an open modal, re-reads the document and the change signal, and exits ETABS only if both
/// still match the save the caller reports - or, for an unverified stamp, only after the
/// caller confirmed the discard.
///
/// <para><b>Lifecycle.</b> Everything before the exit CAS is non-destructive: a timeout, a
/// cancel, a detach, inconclusive evidence, a modal or a COM error answers a typed failure with
/// no exit and no kill, and the C7a blocked/kill sequence never starts on this command's
/// account. Once the CAS is won the exit follows the ordinary exit rules (the session's own
/// bounded shutdown); an explicit <c>shutdown</c> or EOF may then kill a hung exit.</para>
///
/// <para><b>Residual (card v2.4 §2a), disclosed unconditionally.</b> Edits ETABS accepts
/// after the final check and before the exit takes effect may be lost: ETABS can process
/// input between the separate flag-read and exit COM calls. No atomicity is claimed.</para>
/// </summary>
public sealed class ShutdownIfUnchangedService
{
    /// <summary>
    /// The Checking phase's own bound (scan, document, signal), counted from INTAKE. Timing
    /// arithmetic (review P2-7): the inherited response allowance is 30 s, and a won exit takes
    /// up to <see cref="DefaultExitBudget"/> (21 s), so Checking is 8 s: 8 + 21 = 29 s.
    /// </summary>
    public static readonly TimeSpan DefaultCheckBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The bound on a won exit: the ordinary graceful 10 s plus forced 10 s plus 1 s, matching the
    /// started-exit deadline of the shutdown coordinator. Past it the answer is "unconfirmed" and
    /// the host sends an explicit shutdown (the blocked/kill sequence is then permitted).
    /// </summary>
    public static readonly TimeSpan DefaultExitBudget =
        ManagedEtabsShutdownMachine.GracefulExitTimeout + ManagedEtabsShutdownMachine.ForcedExitTimeout + TimeSpan.FromSeconds(1);

    /// <summary>The exit could not be confirmed within its budget; the host must observe.</summary>
    public const string ExitUnconfirmedCode = "ETABS_C10_EXIT_UNCONFIRMED";

    private readonly IEtabsSession _session;
    private readonly IEtabsModalDialogWatchdog _watchdog;
    private readonly IShutdownIfUnchangedApiFactory _apis;
    private readonly ShutdownIfUnchangedRegistry _registry;
    private readonly ISavedFileStampReader _stamps;
    private readonly TimeSpan _checkBudget;
    private readonly TimeSpan _exitBudget;
    private readonly Func<string, string, FileIdentityResult> _compareIdentity;

    public ShutdownIfUnchangedService(
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        IShutdownIfUnchangedApiFactory apis,
        ShutdownIfUnchangedRegistry registry,
        ISavedFileStampReader? stamps = null)
        : this(session, watchdog, apis, registry, stamps, null, null)
    {
    }

    internal ShutdownIfUnchangedService(
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        IShutdownIfUnchangedApiFactory apis,
        ShutdownIfUnchangedRegistry registry,
        ISavedFileStampReader? stamps,
        TimeSpan? checkBudget,
        Func<string, string, FileIdentityResult>? compareIdentity,
        TimeSpan? exitBudget = null)
    {
        _exitBudget = exitBudget ?? DefaultExitBudget;
        _session = session;
        _watchdog = watchdog;
        _apis = apis;
        _registry = registry;
        _stamps = stamps ?? new DiskSavedFileStampReader();
        _checkBudget = checkBudget ?? DefaultCheckBudget;
        _compareIdentity = compareIdentity ?? WindowsFileIdentity.Compare;
    }

    /// <summary>Test seam: runs on the STA right at the start of the check.</summary>
    internal Action? BeforeCheck { get; set; }

    /// <summary>Test seam: runs after the checks passed and before the exit CAS.</summary>
    internal Action? AfterCheckBeforeExitCas { get; set; }

    /// <summary>
    /// Runs one command. <paramref name="onSta"/> queues the body on the ETABS STA exactly as
    /// every other synchronous command is; this method never blocks the caller's thread.
    /// </summary>
    public async Task<object> RunAsync(
        ShutdownIfUnchangedRequest request,
        long requestId,
        Func<Func<Task<object>>, Task<object>> onSta)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onSta);

        var operation = _registry.Begin(requestId);
        if (operation is null)
        {
            return Fail(
                ShutdownIfUnchangedCodes.Busy,
                "Another shutdown-if-unchanged is still in flight; nothing was exited.");
        }

        var body = onSta(() => Task.FromResult(Check(operation, request)));
        var first = await Task.WhenAny(body, Task.Delay(_checkBudget), operation.Abandoned).ConfigureAwait(false);
        if (first == body)
        {
            return await body.ConfigureAwait(false);
        }

        // The Checking phase overran its own budget (a busy or stuck STA). Only a still-Checking
        // operation may be abandoned; one that already won the exit follows the exit rules.
        if (operation.TryAbandon())
        {
            return Fail(
                ShutdownIfUnchangedCodes.CheckTimeout,
                $"The check did not finish within {_checkBudget.TotalSeconds:0.###}s; ETABS was not exited " +
                "and was not killed. The late check can never exit it.");
        }

        // Already abandoned by a cancel, a detach or a shutdown while the body is still queued or
        // blocked: answered at once with the typed abandonment (review P2-7). The late body
        // finds the arbiter taken and can never exit.
        if (operation.State == ShutdownIfUnchangedState.Abandoned)
        {
            return Abandoned();
        }

        // Exiting was won (or the body is finishing a refusal): wait for it, but only within the
        // exit's own bound, never for ever.
        if (await Task.WhenAny(body, Task.Delay(_exitBudget)).ConfigureAwait(false) == body)
        {
            return await body.ConfigureAwait(false);
        }

        // The exit was INITIATED and may even have completed: only its confirmation is unknown, so
        // the evidence says "unknown" and never asserts that nothing was exited (round 2, P2-4).
        return Result.Fail(EtabsTypedFailureCodes.Format(
            ExitUnconfirmedCode,
            $"The exit did not return within {_exitBudget.TotalSeconds:0.###}s; its outcome is unconfirmed. " +
            "Observe the instance; an explicit shutdown may now end it.",
            new { exited = "unknown", exitConfirmed = false }));
    }

    private object Check(ShutdownIfUnchangedOperation operation, ShutdownIfUnchangedRequest request)
    {
        BeforeCheck?.Invoke();

        // A late Checking continuation (the STA was busy, then freed) that finds the arbiter
        // taken never goes on.
        if (operation.State != ShutdownIfUnchangedState.Checking)
        {
            return Abandoned();
        }

        var dialog = _watchdog.CheckNow(() => _session.ProcessId);
        switch (dialog.Kind)
        {
            case EtabsDialogCheckKind.Modal:
                return Refuse(
                    operation,
                    ShutdownIfUnchangedCodes.ModalOpen,
                    "A modal ETABS dialog is open; ETABS was not exited.",
                    new { title = dialog.Dialog?.Title, text = dialog.Dialog?.Text, exited = false });
            case EtabsDialogCheckKind.Inconclusive:
                return Refuse(
                    operation,
                    EtabsTypedFailureCodes.DialogScanInconclusive,
                    "ETABS could not be checked for a modal dialog; it was not exited.",
                    new { reason = dialog.Reason, exited = false });
        }

        var stamp = request.SavedStamp;
        var verified = stamp is { Verified: true };
        if (!verified && !request.DiscardConfirmed)
        {
            // Fallback = explicit discard mode (card v2.4 §3): no "unchanged" assurance exists,
            // so the exit needs the caller's explicit confirmation of the discard.
            return Refuse(
                operation,
                ShutdownIfUnchangedCodes.DiscardNotConfirmed,
                "The save stamp is unverified, so no unchanged assurance exists and the discard was not " +
                "confirmed; ETABS was not exited.",
                new { exited = false, stampVerified = false });
        }

        // Discard consent waives CHANGE DETECTION, never document identity (review P1-2): in
        // both modes the open model must still be the requested file and the expected document,
        // or the engineer switched models after the prompt and the exit is refused.
        var changed = FindChange(request, verified ? stamp : null, out var inconclusive);
        if (inconclusive is not null)
        {
            return Refuse(
                operation,
                ShutdownIfUnchangedCodes.CheckInconclusive,
                "The document and its change signal could not be re-read; ETABS was not exited.",
                new { reason = inconclusive, exited = false });
        }

        if (changed is not null)
        {
            return Refuse(
                operation,
                ShutdownIfUnchangedCodes.ChangedSinceSave,
                "The open model is not the one the close was decided for, or it changed since the " +
                "reported save; ETABS was not exited. Save again.",
                new { reason = changed, exited = false });
        }

        AfterCheckBeforeExitCas?.Invoke();

        // THE irreversible boundary. Whichever CAS wins decides: a cancel, a detach, a shutdown
        // or a timeout that took the arbiter means this command never exits.
        if (!operation.TryEnterExiting())
        {
            return Abandoned();
        }

        try
        {
            return ServeShutdownWire.ToResult(_session.Shutdown());
        }
        finally
        {
            operation.MarkEnded();
        }
    }

    /// <summary>
    /// The first reason the document or its signal moved since the save, or null when both match.
    /// <paramref name="inconclusive"/> is set when the evidence could not be read, which is never
    /// the same as "unchanged".
    /// </summary>
    private string? FindChange(ShutdownIfUnchangedRequest request, SavedStamp? stamp, out string? inconclusive)
    {
        inconclusive = null;
        IEtabsModelFileApi api;
        ChangeSignalReading reading = ChangeSignalReading.Unsupported;
        string? reported;
        try
        {
            api = _apis.Create(_session);
            if (stamp is not null)
            {
                reading = api.ReadChangeSignal();
            }

            reported = api.GetModelFilename();
        }
#pragma warning disable CA1031 // any read failure is inconclusive, never "unchanged"
        catch (Exception exception)
#pragma warning restore CA1031
        {
            inconclusive = $"a COM read failed: {EtabsApiDiagnosticFormatter.Bounded(exception.Message)}";
            return null;
        }

        if (stamp is not null)
        {
            if (reading.State is ChangeSignalState.Unsupported or ChangeSignalState.Incomplete)
            {
                inconclusive = $"the change signal is {SaveModelService.ChangeSignalName(reading.State)}";
                return null;
            }

            if (reading.State == ChangeSignalState.Dirty)
            {
                return "ETABS reports unsaved changes";
            }
        }

        string requestedFull;
        try
        {
            requestedFull = Path.GetFullPath(request.FilePath);
        }
#pragma warning disable CA1031 // an unresolvable path is inconclusive
        catch (Exception exception)
#pragma warning restore CA1031
        {
            inconclusive = $"the file path could not be resolved: {EtabsApiDiagnosticFormatter.Bounded(exception.Message)}";
            return null;
        }

        if (SaveModelService.ValidateOpenModelIs(reported, requestedFull, _compareIdentity) is not null)
        {
            return "the open document is not the saved model";
        }

        var now = _stamps.Read(requestedFull);
        if (now is null && (stamp is not null || request.ExpectedDocument?.FileId is not null))
        {
            return "the saved file is gone";
        }

        if (request.ExpectedDocument?.FileId is { } expected
            && !string.Equals(now?.FileId, expected, StringComparison.Ordinal))
        {
            return "the saved file is not the expected document";
        }

        if (stamp is null)
        {
            return null;
        }

        if (stamp.FileId is not null && !string.Equals(now!.FileId, stamp.FileId, StringComparison.Ordinal))
        {
            return "the saved file was replaced";
        }

        if (now!.Size != stamp.Size || now.MtimeUtc != stamp.Mtime)
        {
            return "the saved file changed on disk since the reported save";
        }

        return null;
    }

    private static object Refuse(
        ShutdownIfUnchangedOperation operation,
        string code,
        string prose,
        object detail) => operation.TryRefuse()
            ? Result.Fail(EtabsTypedFailureCodes.Format(code, prose, detail))
            : Abandoned();

    private static object Abandoned() => Fail(
        ShutdownIfUnchangedCodes.Abandoned,
        "A cancel, a detach, a shutdown or a timeout took this command first; ETABS was not exited by it.");

    private static Result Fail(string code, string prose) =>
        Result.Fail(EtabsTypedFailureCodes.Format(code, prose, new { exited = false }));
}
