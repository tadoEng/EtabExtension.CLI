using System.Text.Json.Serialization;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>Typed refusals and unknowns of <c>save-and-exit</c> (card v2.5 §2, v2.5a A1, A4).</summary>
public static class SaveAndExitCodes
{
    /// <summary>
    /// The open document is not the recorded one (another model, an export's <c>.e2k</c>, a
    /// replaced file, a destination that is not the recorded path). Nothing was saved or exited.
    /// </summary>
    public const string DocumentUnverified = "ETABS_DOCUMENT_UNVERIFIED";

    /// <summary>The save did not succeed (a CSI return code, or the dialog rules); nothing was exited.</summary>
    public const string SaveFailed = "ETABS_SAVE_FAILED";

    /// <summary>The save returned 0 but the file on disk did not change; nothing was exited.</summary>
    public const string SaveNotObserved = SaveModelService.SaveNotObservedCode;

    /// <summary>
    /// The save outcome is unknown (a timeout, a COM fault); ETABS was NOT exited and Save is
    /// never offered again by the prompt.
    /// </summary>
    public const string SaveUnknown = "ETABS_SAVE_UNKNOWN";

    /// <summary>A cancel, a detach, EOF suppression or a shutdown took the arbiter; this command never exits.</summary>
    public const string Abandoned = "ETABS_SAVE_AND_EXIT_ABANDONED";

    /// <summary>Another <c>save-and-exit</c> is still in flight.</summary>
    public const string Busy = "ETABS_SAVE_AND_EXIT_BUSY";

    /// <summary>The file was saved and the exit was initiated, but it could not be confirmed.</summary>
    public const string ExitUnconfirmed = "ETABS_SAVE_AND_EXIT_EXIT_UNCONFIRMED";

    /// <summary>
    /// The file was saved, but a detach won the instance before the exit was carried out: ETABS
    /// is the user's now and was NOT exited by this command.
    /// </summary>
    public const string Detached = "ETABS_SAVE_AND_EXIT_DETACHED";
}

/// <summary>The lifecycle of one <c>save-and-exit</c> (card v2.5a A4).</summary>
public enum SaveAndExitState
{
    /// <summary>Checking the dialog state and the document, then saving. Never destructive.</summary>
    Saving = 0,

    /// <summary>The exit CAS was won: <c>ApplicationExit</c> is being (or was) called.</summary>
    Exiting = 1,

    /// <summary>The exit ran to its answer.</summary>
    Ended = 2,

    /// <summary>A modal, an unverified document, a failed or unobserved save refused the exit.</summary>
    Refused = 3,

    /// <summary>A cancel, a detach, EOF suppression or a shutdown took the arbiter first.</summary>
    Abandoned = 4,

    /// <summary>The save phase expired, or the save faulted: its outcome is unknown. Never exits.</summary>
    Unknown = 5
}

/// <summary>
/// One command's arbiter. <c>ApplicationExit</c> is called ONLY after
/// <see cref="TryEnterExiting"/> wins <c>Saving → Exiting</c>. Every rival (cancel, detach, EOF
/// suppression, shutdown, the phase timeout, a refusal) wins <c>Saving</c> away from it, and a
/// late Save continuation that finds anything but Saving never exits.
/// </summary>
public sealed class SaveAndExitOperation(long requestId)
{
    private int _state;
    private readonly TaskCompletionSource _abandoned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long RequestId { get; } = requestId;

    /// <summary>Completes the moment a cancel, a detach, EOF suppression or a shutdown abandons the command.</summary>
    public Task Abandoned => _abandoned.Task;

    /// <summary>Completes the moment the exit CAS is won (the Exiting clock starts here).</summary>
    public Task ExitingWon => _exiting.Task;

    public SaveAndExitState State => (SaveAndExitState)Volatile.Read(ref _state);

    public bool TryEnterExiting()
    {
        var won = Move(SaveAndExitState.Saving, SaveAndExitState.Exiting);
        if (won)
        {
            _ = _exiting.TrySetResult();
        }

        return won;
    }

    public bool TryAbandon()
    {
        var won = Move(SaveAndExitState.Saving, SaveAndExitState.Abandoned);
        if (won)
        {
            _ = _abandoned.TrySetResult();
        }

        return won;
    }

    /// <summary>The saved file's identity, set before the exit CAS (what the exit answer reports).</summary>
    public EtabsDocumentRecord? SavedDocument { get; set; }

    /// <summary>True when the file id on disk differed from the record at verification (a user save replaced it).</summary>
    public bool FileIdChanged { get; set; }

    public bool TryRefuse() => Move(SaveAndExitState.Saving, SaveAndExitState.Refused);

    public bool TryMarkUnknown() => Move(SaveAndExitState.Saving, SaveAndExitState.Unknown);

    public void MarkEnded() => Move(SaveAndExitState.Exiting, SaveAndExitState.Ended);

    private bool Move(SaveAndExitState from, SaveAndExitState to) =>
        Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
}

/// <summary>The daemon's single in-flight <c>save-and-exit</c>, reachable by every rival.</summary>
public sealed class SaveAndExitRegistry
{
    private readonly object _gate = new();
    private SaveAndExitOperation? _current;

    /// <summary>Starts an operation, or returns null while another is still in flight.</summary>
    public SaveAndExitOperation? Begin(long requestId)
    {
        lock (_gate)
        {
            if (_current is { State: SaveAndExitState.Saving or SaveAndExitState.Exiting })
            {
                return null;
            }

            return _current = new SaveAndExitOperation(requestId);
        }
    }

    /// <summary>
    /// A detach, a shutdown, EOF or its suppression abandons a command that is still Saving.
    /// Returns true when one was abandoned. A command already Exiting is untouched: its exit
    /// is already won.
    /// </summary>
    public bool AbandonCurrent()
    {
        lock (_gate)
        {
            return _current?.TryAbandon() ?? false;
        }
    }

    /// <summary>
    /// <c>cancel-operation</c> naming the request. Returns the state the operation was left
    /// in, or null when no <c>save-and-exit</c> of that request exists.
    /// </summary>
    public SaveAndExitState? Cancel(long requestId)
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

public sealed class SaveAndExitRequest
{
    /// <summary>
    /// Optional. The destination is ALWAYS the recorded path (card v2.5a A1c); when this is
    /// given and is not that path, the command refuses.
    /// </summary>
    [JsonPropertyName("filePath")] public string? FilePath { get; init; }

    /// <summary>The identity the host recorded when it opened the model. Required.</summary>
    [JsonPropertyName("expectedDocument")] public ExpectedDocument? ExpectedDocument { get; init; }
}

/// <summary>The successful answer: saved, and exited with the exit confirmed.</summary>
public sealed record SaveAndExitData(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("saved")] bool Saved,
    [property: JsonPropertyName("exited")] bool Exited,
    [property: JsonPropertyName("document")] EtabsDocumentRecord? Document,
    [property: JsonPropertyName("shutdown")] ManagedEtabsShutdownData? Shutdown)
{
    /// <summary>
    /// True when the EDB file id differed from the recorded one at verification. ETABS replaces
    /// the file on every save (live, 13 Nov), so this is normal after a save by the user.
    /// </summary>
    [JsonPropertyName("fileIdChanged")]
    public bool FileIdChanged { get; init; }

    /// <summary>Why a changed file id was accepted: <c>etabs-save</c> (the sibling .$et was written with the EDB), else null.</summary>
    [JsonPropertyName("fileIdChangeEvidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileIdChangeEvidence { get; init; }

    /// <summary>The approved residual wording (card v2.5a A2), for the host to show as it is.</summary>
    [JsonPropertyName("residual")]
    public string Residual { get; init; } = SaveAndExitService.ResidualDisclosure;
}

/// <summary>
/// <c>save-and-exit</c>, the C11 of card v2.5 / v2.5a: ONE STA command that refuses on an open
/// modal, verifies the open document against the instance record, saves to the RECORDED path,
/// and - on the same thread, with no host round-trip - exits ETABS.
///
/// <para><b>Document identity (A1).</b> Before ANY save the observed model filename must be the
/// recorded EDB or its same-stem <c>.$et</c>, the file on disk must still have the recorded
/// file id, and the request must not name a different destination. The observed filename is
/// never the destination. Anything else answers <c>ETABS_DOCUMENT_UNVERIFIED</c>: no save, no
/// exit.</para>
///
/// <para><b>Lifecycle (A4).</b> <c>Saving → Exiting → Ended</c>, or <c>Saving → Abandoned |
/// Refused | Unknown</c>. The exit is called ONLY after this command wins the Saving → Exiting
/// CAS; a cancel, a detach, EOF suppression, a shutdown or the phase timeout each take Saving
/// away, so a late Save success that finds anything else never exits. A failed, unobserved or
/// unknown save never exits.</para>
///
/// <para><b>Clocks (A4).</b> lock 2 s + Saving 300 s + Exiting 21 s + margin 8 s = 331 s. The
/// daemon enforces the first three: Saving, measured from INTAKE (it covers the queue wait for
/// the STA), is <see cref="DefaultSavingBudget"/>; the exit is bounded by
/// <see cref="DefaultExitBudget"/> from the moment it is won. The margin belongs to the host.</para>
///
/// <para><b>Residual (A2), disclosed unconditionally:</b> edits not included in the saved state,
/// accepted before closing takes effect, may be lost. No input quiescence and no atomicity are
/// claimed.</para>
/// </summary>
public sealed class SaveAndExitService
{
    /// <summary>
    /// The approved residual wording (card v2.5a A2), used everywhere (UI, docs). The code treats
    /// an edit after the save, and one during delayed exit processing, as this residual.
    /// </summary>
    public const string ResidualDisclosure =
        "Edits not included in the saved state, accepted before closing takes effect, may be lost.";

    /// <summary>The evidence name reported when a changed file id was accepted.</summary>
    public const string EtabsSaveEvidence = "etabs-save";

    /// <summary>
    /// ETABS-save evidence window (live 13 Nov: every ETABS Save writes the .$et about 10-15 ms
    /// AFTER the EDB; an external replacement does not touch it): the sibling .$et last-write
    /// time must be at least this long BEFORE the EDB last-write time...
    /// </summary>
    public static readonly TimeSpan EtEvidenceBefore = TimeSpan.FromMilliseconds(500);

    /// <summary>...and at most this long AFTER it.</summary>
    public static readonly TimeSpan EtEvidenceAfter = TimeSpan.FromSeconds(5);

    /// <summary>The STA lock wait (queueing behind earlier work), part of the 331 s policy.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    /// <summary>The save itself.</summary>
    public static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(300);

    /// <summary>The Saving phase, counted from INTAKE: the lock wait plus the save.</summary>
    public static readonly TimeSpan DefaultSavingBudget = LockWait + SaveWait;

    /// <summary>
    /// The bound on a won exit: graceful 10 s plus forced 10 s plus 1 s, as for C10 and the
    /// shutdown coordinator.
    /// </summary>
    public static readonly TimeSpan DefaultExitBudget = ShutdownIfUnchangedService.DefaultExitBudget;

    /// <summary>The host's margin on top of the daemon's phases.</summary>
    public static readonly TimeSpan HostMargin = TimeSpan.FromSeconds(8);

    /// <summary>The per-command policy the host uses: 2 + 300 + 21 + 8 = 331 s (card v2.5a A4).</summary>
    public static readonly TimeSpan HostPolicy = DefaultSavingBudget + DefaultExitBudget + HostMargin;

    private readonly IEtabsSession _session;
    private readonly IEtabsModalDialogWatchdog _watchdog;
    private readonly IShutdownIfUnchangedApiFactory _apis;
    private readonly SaveAndExitRegistry _registry;
    private readonly ISavedFileStampReader _stamps;
    private readonly TimeSpan _savingBudget;
    private readonly TimeSpan _exitBudget;
    private readonly TimeSpan _exitGrace;

    public SaveAndExitService(
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        IShutdownIfUnchangedApiFactory apis,
        SaveAndExitRegistry registry,
        ISavedFileStampReader? stamps = null)
        : this(session, watchdog, apis, registry, stamps, null, null)
    {
    }

    internal SaveAndExitService(
        IEtabsSession session,
        IEtabsModalDialogWatchdog watchdog,
        IShutdownIfUnchangedApiFactory apis,
        SaveAndExitRegistry registry,
        ISavedFileStampReader? stamps,
        TimeSpan? savingBudget,
        TimeSpan? exitBudget,
        TimeSpan? exitGrace = null)
    {
        _session = session;
        _watchdog = watchdog;
        _apis = apis;
        _registry = registry;
        _stamps = stamps ?? new DiskSavedFileStampReader();
        _savingBudget = savingBudget ?? DefaultSavingBudget;
        _exitBudget = exitBudget ?? DefaultExitBudget;

        // The ordinary exit keeps its graceful interval (10 s, as in C7a); then the bounded
        // cleanup takes the rest of the budget. A shortened test budget scales the grace.
        _exitGrace = exitGrace
            ?? (_exitBudget == DefaultExitBudget
                ? ManagedEtabsShutdownMachine.GracefulExitTimeout
                : TimeSpan.FromTicks(_exitBudget.Ticks / 2));
    }

    /// <summary>Test seam: runs on the STA right at the start of the body.</summary>
    internal Action? BeforeCheck { get; set; }

    /// <summary>Test seam: runs on the STA right after the exit CAS was won, before the session is asked to exit.</summary>
    internal Action? AfterExitCasBeforeShutdown { get; set; }

    /// <summary>Test seam: runs on the STA after the save returned and before the exit CAS.</summary>
    internal Action? AfterSaveBeforeExitCas { get; set; }

    /// <summary>
    /// Runs one command. <paramref name="onSta"/> queues the body on the ETABS STA exactly as
    /// every other synchronous command is; this method never blocks the caller's thread.
    /// </summary>
    public async Task<object> RunAsync(
        SaveAndExitRequest request,
        long requestId,
        Func<Func<Task<object>>, Task<object>> onSta)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onSta);

        var operation = _registry.Begin(requestId);
        if (operation is null)
        {
            return Fail(
                SaveAndExitCodes.Busy,
                "Another save-and-exit is still in flight; nothing was saved by this request and ETABS was not exited.",
                "refused",
                saved: false);
        }

        var body = onSta(() => Task.FromResult(Run(operation, request)));
        var first = await Task.WhenAny(
            body,
            operation.Abandoned,
            operation.ExitingWon,
            Task.Delay(_savingBudget)).ConfigureAwait(false);
        if (first == body)
        {
            return await body.ConfigureAwait(false);
        }

        if (first == operation.Abandoned)
        {
            return AbandonedAnswer();
        }

        if (first != operation.ExitingWon)
        {
            // The Saving phase overran its own budget (a busy or stuck STA, a slow save). Only
            // a still-Saving operation may be taken: the outcome is UNKNOWN and nothing exits.
            if (operation.TryMarkUnknown())
            {
                return Fail(
                    SaveAndExitCodes.SaveUnknown,
                    $"The save did not finish within {_savingBudget.TotalSeconds:0.###}s of the request; its " +
                    "outcome is unknown. ETABS was not exited and was not killed, and a late save can never exit it.",
                    "saving",
                    saved: "unknown");
            }

            if (operation.State == SaveAndExitState.Abandoned)
            {
                return AbandonedAnswer();
            }
        }

        return await SuperviseExitAsync(operation, body).ConfigureAwait(false);
    }

    /// <summary>
    /// A won exit is SUPERVISED like C7a (review P2-2): the ordinary exit on the STA keeps its
    /// graceful interval; past it the bounded cleanup (the session blocked sequence, on its own
    /// STA thread, which respects a detach and the Suppressed state) takes over, so a hung
    /// ApplicationExit neither holds the STA nor the licence. The whole exit is answered
    /// within <see cref="DefaultExitBudget"/>.
    /// </summary>
    private async Task<object> SuperviseExitAsync(SaveAndExitOperation operation, Task<object> body)
    {
        if (await Task.WhenAny(body, Task.Delay(_exitGrace)).ConfigureAwait(false) == body)
        {
            return await body.ConfigureAwait(false);
        }

        _ = body.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        Console.Error.WriteLine(
            "⚠ The ordinary ETABS exit did not return within its grace; taking the bounded cleanup.");
        var completion = new TaskCompletionSource<ManagedEtabsShutdownResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(_session.ShutdownWhileWorkerBlocked());
            }
#pragma warning disable CA1031 // surfaced to the supervisor through the completion
            catch (Exception exception)
#pragma warning restore CA1031
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "etab-cli-save-and-exit-cleanup"
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        var remaining = _exitBudget - _exitGrace;
        try
        {
            var terminal = await completion.Task
                .WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
                .ConfigureAwait(false);
            return ExitAnswer(operation, terminal);
        }
#pragma warning disable CA1031 // a failing cleanup is an unconfirmed exit, never a thrown answer
        catch (Exception)
#pragma warning restore CA1031
        {
            // The exit was INITIATED and may even have completed: only its confirmation is unknown.
            return UnconfirmedExit(
                $"The exit did not return within {_exitBudget.TotalSeconds:0.###}s; its outcome is unconfirmed. " +
                "Observe the instance; an explicit shutdown may now end it.");
        }
    }

    /// <summary>
    /// exited:true ONLY for a confirmed process exit (review P2-3). A detach that won the
    /// instance between the exit CAS and the termination is reported as the detach it is; anything
    /// else is unknown.
    /// </summary>
    private static object ExitAnswer(SaveAndExitOperation operation, ManagedEtabsShutdownResult terminal)
    {
        var shutdown = ServeShutdownWire.ToResult(terminal);
        if (shutdown.Success && shutdown.Data is { ProcessExitConfirmed: true })
        {
            return Result.Ok(new SaveAndExitData(
                "ended",
                Saved: true,
                Exited: true,
                operation.SavedDocument,
                shutdown.Data)
            {
                FileIdChanged = operation.FileIdChanged,
                FileIdChangeEvidence = operation.FileIdChanged ? EtabsSaveEvidence : null
            });
        }

        if (shutdown.Success && shutdown.Data is { Disposition: EtabsDisposition.Detached })
        {
            return Fail(
                SaveAndExitCodes.Detached,
                "The file was saved, but the instance was handed to the user by a detach before the exit " +
                "ran; ETABS was not exited by this command.",
                "exiting",
                saved: true,
                exited: false);
        }

        return UnconfirmedExit(shutdown.Error ?? "The exit could not be confirmed.");
    }

    private object Run(SaveAndExitOperation operation, SaveAndExitRequest request)
    {
        BeforeCheck?.Invoke();

        // A late continuation (the STA was busy, then freed) that finds the arbiter taken never
        // saves and never goes on.
        if (operation.State != SaveAndExitState.Saving)
        {
            return AbandonedAnswer();
        }

        var dialog = _watchdog.CheckNow(() => _session.ProcessId);
        switch (dialog.Kind)
        {
            case EtabsDialogCheckKind.Modal:
                return Refuse(
                    operation,
                    ShutdownIfUnchangedCodes.ModalOpen,
                    "A modal ETABS dialog is open; nothing was saved and ETABS was not exited.",
                    new { stage = "refused", saved = false, exited = false, title = dialog.Dialog?.Title, text = dialog.Dialog?.Text });
            case EtabsDialogCheckKind.Inconclusive:
                return Refuse(
                    operation,
                    EtabsTypedFailureCodes.DialogScanInconclusive,
                    "ETABS could not be checked for a modal dialog; nothing was saved and it was not exited.",
                    new { stage = "refused", saved = false, exited = false, reason = dialog.Reason });
        }

        IEtabsModelFileApi api;
        EtabsDocumentRecord? recorded;
        string? observed;
        try
        {
            recorded = _session.ReadRecordedDocument();
            api = _apis.Create(_session);
            observed = api.GetModelFilename();
        }
#pragma warning disable CA1031 // an unreadable document is an unverified one, never a save
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Unverified(operation, $"the open document could not be read: {EtabsApiDiagnosticFormatter.Bounded(exception.Message)}");
        }

        var unverified = VerifyDocument(request, recorded, observed, out var destination, out var before, out var fileIdChanged);
        if (unverified is not null)
        {
            return Unverified(operation, unverified);
        }

        // Never begin a save for a command that already lost the arbiter.
        if (operation.State != SaveAndExitState.Saving)
        {
            return AbandonedAnswer();
        }

        int returnCode;
        try
        {
            Console.Error.WriteLine($"ℹ Saving to: {destination}");
            returnCode = api.SaveFile(destination);
        }
#pragma warning disable CA1031 // a fault inside the save leaves its outcome unknown: never an exit
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return operation.TryMarkUnknown()
                ? Fail(
                    SaveAndExitCodes.SaveUnknown,
                    $"The save faulted and its outcome is unknown; ETABS was not exited. " +
                    EtabsApiDiagnosticFormatter.Exception(SaveModelService.SaveOperation, exception),
                    "saving",
                    saved: "unknown")
                : AbandonedAnswer();
        }

        if (returnCode != 0)
        {
            return Refuse(
                operation,
                SaveAndExitCodes.SaveFailed,
                EtabsApiDiagnosticFormatter.ApiReturn(SaveModelService.SaveOperation, returnCode) +
                " ETABS was not exited.",
                new { stage = "saving", saved = false, exited = false, returnCode });
        }

        SavedFileStamp? after;
        try
        {
            after = _stamps.Read(destination);
        }
#pragma warning disable CA1031 // an unreadable file after the save leaves its outcome unknown: never an exit
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return operation.TryMarkUnknown()
                ? Fail(
                    SaveAndExitCodes.SaveUnknown,
                    "The saved file could not be read back, so the save is not confirmed; ETABS was not exited. " +
                    EtabsApiDiagnosticFormatter.Bounded(exception.Message),
                    "saving",
                    saved: "unknown")
                : AbandonedAnswer();
        }

        if (after is null || (before is not null && before.Size == after.Size && before.MtimeUtc == after.MtimeUtc))
        {
            return Refuse(
                operation,
                SaveAndExitCodes.SaveNotObserved,
                $"cFile.Save returned 0 but the file on disk did not change, so the save is not reported as done; " +
                "ETABS was not exited.",
                new { stage = "saving", saved = false, exited = false });
        }

        var saved = new EtabsDocumentRecord(destination, after.FileId);
        operation.SavedDocument = saved;
        operation.FileIdChanged = fileIdChanged;
        AfterSaveBeforeExitCas?.Invoke();

        // THE irreversible boundary. A cancel, a detach, EOF suppression, a shutdown or the
        // phase timeout that took the arbiter while the save ran means this command never
        // exits - even though the save itself has now completed.
        if (!operation.TryEnterExiting())
        {
            return AbandonedAnswer();
        }

        // ETABS replaces the EDB on every save, so its file id is new (live, 13 Nov). Adopt it ONLY
        // now that this operation owns the exit - never after Unknown, Abandoned, a timeout or a
        // cancel - and only through the PASSIVE writer, which can never start, verify-with-teardown,
        // exit or kill ETABS. Best effort: the exit that follows ends the record anyway.
        try
        {
            _ = _session.RefreshRecordedDocument(saved);
        }
#pragma warning disable CA1031 // the exit does not depend on refreshing a record it is about to end
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"⚠ The saved document could not be re-recorded: {exception.Message}");
        }

        ManagedEtabsShutdownResult terminal;
        try
        {
            AfterExitCasBeforeShutdown?.Invoke();
            terminal = _session.Shutdown();
        }
#pragma warning disable CA1031 // the exit was initiated: a throw is an unconfirmed exit, never a lost answer
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return UnconfirmedExit(
                "The exit was initiated but faulted: " + EtabsApiDiagnosticFormatter.Bounded(exception.Message));
        }
        finally
        {
            operation.MarkEnded();
        }

        return ExitAnswer(operation, terminal);
    }

    /// <summary>
    /// The reason the document cannot be verified, or null when it can (card v2.5a A1). On null,
    /// <paramref name="destination"/> is the recorded path - the ONLY place a save may go - and
    /// <paramref name="before"/> is the file's stamp just before the save.
    /// </summary>
    private string? VerifyDocument(
        SaveAndExitRequest request,
        EtabsDocumentRecord? recorded,
        string? observedFilename,
        out string destination,
        out SavedFileStamp? before,
        out bool fileIdChanged)
    {
        destination = string.Empty;
        before = null;
        fileIdChanged = false;
        if (recorded is null || string.IsNullOrWhiteSpace(recorded.Path))
        {
            return "no opened document is recorded for this instance";
        }

        if (string.IsNullOrEmpty(recorded.FileId))
        {
            return "the recorded document has no file identity";
        }

        string recordedFull;
        try
        {
            recordedFull = Path.GetFullPath(recorded.Path);
        }
#pragma warning disable CA1031 // an unresolvable recorded path is unverifiable
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return $"the recorded document path could not be resolved: {EtabsApiDiagnosticFormatter.Bounded(exception.Message)}";
        }

        // The request must present the identity it recorded at open; a different one means the
        // host is talking about another document.
        var expected = request.ExpectedDocument;
        if (expected?.FileId is null
            || !string.Equals(expected.FileId, recorded.FileId, StringComparison.Ordinal))
        {
            return "the expected document is not the recorded one";
        }

        if (expected.Path is not null && !SamePath(expected.Path, recordedFull))
        {
            return "the expected document path is not the recorded path";
        }

        // (c) The destination is the recorded path only.
        if (request.FilePath is not null && !SamePath(request.FilePath, recordedFull))
        {
            return "the requested destination is not the recorded path";
        }

        // (a) The model ETABS has open: the recorded EDB, or its same-stem .$et. Never an .e2k
        // an export left behind, never another model.
        var current = EtabsCurrentModelPath.ResolveOpenFile(observedFilename);
        if (current is null)
        {
            return $"ETABS did not report an open model file ('{EtabsCurrentModelPath.Describe(observedFilename)}')";
        }

        string currentFull;
        try
        {
            currentFull = Path.GetFullPath(current);
        }
#pragma warning disable CA1031 // an unresolvable observed path is unverifiable
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return $"the observed model path could not be resolved: {EtabsApiDiagnosticFormatter.Bounded(exception.Message)}";
        }

        if (!SamePath(currentFull, recordedFull) && !SaveModelService.IsLockSiblingOf(currentFull, recordedFull))
        {
            return $"ETABS has '{EtabsCurrentModelPath.Describe(observedFilename)}' open, not the recorded model";
        }

        // (b) The EDB on disk is still the file that was opened (not replaced, not moved).
        SavedFileStamp? onDisk;
        try
        {
            onDisk = _stamps.Read(recordedFull);
        }
#pragma warning disable CA1031 // an unreadable file is unverifiable
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return $"the recorded file could not be read: {EtabsApiDiagnosticFormatter.Bounded(exception.Message)}";
        }

        if (onDisk is null)
        {
            return "the recorded file is gone";
        }

        // ETABS replaces the EDB on every save (live, 13 Nov), so a new file id is normal after
        // the user's own Ctrl+S - but an EXTERNAL replacement looks the same. A changed id is
        // accepted ONLY with ETABS-save evidence: the same-stem .$et beside the EDB was written
        // within [EDB - EtEvidenceBefore, EDB + EtEvidenceAfter]. An unreadable id refuses.
        if (string.IsNullOrEmpty(onDisk.FileId))
        {
            return "the recorded file's identity could not be read";
        }

        fileIdChanged = !string.Equals(onDisk.FileId, recorded.FileId, StringComparison.Ordinal);
        if (fileIdChanged)
        {
            SavedFileStamp? et;
            try
            {
                et = _stamps.Read(Path.ChangeExtension(recordedFull, ".$et"));
            }
#pragma warning disable CA1031 // unreadable evidence is no evidence
            catch (Exception)
#pragma warning restore CA1031
            {
                et = null;
            }

            if (et is null
                || et.MtimeUtc < onDisk.MtimeUtc - EtEvidenceBefore
                || et.MtimeUtc > onDisk.MtimeUtc + EtEvidenceAfter)
            {
                return "the recorded file was replaced or moved (its file id changed) without ETABS-save evidence";
            }
        }

        destination = recordedFull;
        before = onDisk;
        return null;
    }

    private static bool SamePath(string left, string rightFull)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), rightFull, StringComparison.OrdinalIgnoreCase);
        }
#pragma warning disable CA1031 // an unresolvable path is not the same path
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static object Unverified(SaveAndExitOperation operation, string reason) => Refuse(
        operation,
        SaveAndExitCodes.DocumentUnverified,
        $"The open document could not be verified as the recorded model ({reason}); nothing was saved " +
        "and ETABS was not exited.",
        new { stage = "refused", saved = false, exited = false, reason });

    private static object Refuse(SaveAndExitOperation operation, string code, string prose, object detail) =>
        operation.TryRefuse()
            ? Result.Fail(EtabsTypedFailureCodes.Format(code, prose, detail))
            : AbandonedAnswer();

    private static object AbandonedAnswer() => Fail(
        SaveAndExitCodes.Abandoned,
        "A cancel, a detach, EOF suppression or a shutdown took this command first; ETABS was not exited by it. " +
        "Whether the file was saved is unknown.",
        "abandoned",
        saved: "unknown");

    private static Result UnconfirmedExit(string prose) => Fail(
        SaveAndExitCodes.ExitUnconfirmed,
        prose,
        "exiting",
        saved: true,
        exited: "unknown");

    /// <summary>
    /// A typed failure whose detail is <c>{stage, saved, exited}</c>. <c>saved</c> and
    /// <c>exited</c> are a boolean or the string <c>"unknown"</c>; an unconfirmed exit never
    /// says <c>exited:false</c>.
    /// </summary>
    private static Result Fail(string code, string prose, string stage, object saved, object? exited = null) =>
        Result.Fail(EtabsTypedFailureCodes.Format(code, prose, new { stage, saved, exited = exited ?? false }));
}
