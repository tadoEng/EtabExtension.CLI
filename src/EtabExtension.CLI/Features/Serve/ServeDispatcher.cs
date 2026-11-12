using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.CloseModel;
using EtabExtension.CLI.Features.ExtractMaterials;
using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.GenerateE2K;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Features.ReadModelMetadata;
using EtabExtension.CLI.Features.RunAnalysis;
using EtabExtension.CLI.Features.Serve.Inspection;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Features.SaveModel;
using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// Routes one serve request to the matching feature, executed against the single
/// shared ETABS session. The handler registry is also the authoritative source
/// for the capabilities advertised by the persistent protocol.
/// </summary>
public sealed class ServeDispatcher : IServeDispatcher
{
    private readonly IEtabsSession _session;
    private readonly IGetStatusService _status;
    private readonly IOpenModelService _open;
    private readonly ISnapshotExportService _snapshot;
    private readonly ICloseModelService _close;
    private readonly IUnlockModelService _unlock;
    private readonly ISaveModelService _save;
    private readonly IExtractResultsService _extractResults;
    private readonly IExtractMaterialsService _extractMaterials;
    private readonly IGenerateE2KService _generateE2K;
    private readonly IReadModelMetadataService _metadata;
    private readonly IServeInspectionService _inspection;
    private readonly IEtabsInspectionApiFactory _inspectionApiFactory;
    private readonly ISessionRecordStore _sessionRecords;
    private readonly IOperationManager _operations;
    private readonly ICachedSessionStatus _cachedStatus;
    private readonly IProcessInspector _processes;
    private readonly IRunAnalysisService _runAnalysis;
    private readonly IEtabsWorkEnvelope _envelope;
    private readonly IEtabsModalDialogWatchdog _watchdog;
    private readonly IEtabsInstalledVersion _installedVersion;
    private readonly AbandonedRequests _abandoned;
    private readonly OrphanDispositionCell _orphan;
    private readonly ShutdownIfUnchangedService? _shutdownIfUnchanged;
    private readonly ShutdownIfUnchangedRegistry _c10;
    private readonly Dictionary<
        string,
        Func<JsonElement?, CancellationToken, Task<object>>> _handlers;

    // Synchronous ETABS commands queued on or running on the STA. While any is, get-status is
    // answered from the daemon's own state and never queues behind it (card C8).
    private int _synchronousInFlight;

    public IReadOnlyCollection<string> Capabilities { get; }

    public ServeDispatcher(
        IEtabsSession session,
        IGetStatusService status,
        IOpenModelService open,
        ISnapshotExportService snapshot,
        ICloseModelService close,
        IUnlockModelService unlock,
        ISaveModelService save,
        IExtractResultsService extractResults,
        IExtractMaterialsService extractMaterials,
        IGenerateE2KService generateE2K,
        IReadModelMetadataService metadata,
        IServeInspectionService inspection,
        IEtabsInspectionApiFactory inspectionApiFactory,
        ISessionRecordStore sessionRecords,
        IOperationManager operations,
        ICachedSessionStatus cachedStatus,
        IProcessInspector processes,
        IRunAnalysisService runAnalysis,
        IEtabsWorkEnvelope envelope,
        IEtabsModalDialogWatchdog watchdog,
        IEtabsInstalledVersion installedVersion,
        AbandonedRequests? abandoned = null,
        OrphanDispositionCell? orphan = null,
        ShutdownIfUnchangedService? shutdownIfUnchanged = null,
        ShutdownIfUnchangedRegistry? c10 = null)
    {
        _abandoned = abandoned ?? new AbandonedRequests();
        _orphan = orphan ?? new OrphanDispositionCell();
        _shutdownIfUnchanged = shutdownIfUnchanged;
        _c10 = c10 ?? new ShutdownIfUnchangedRegistry();
        _session = session;
        _status = status;
        _open = open;
        _snapshot = snapshot;
        _close = close;
        _unlock = unlock;
        _save = save;
        _extractResults = extractResults;
        _extractMaterials = extractMaterials;
        _generateE2K = generateE2K;
        _metadata = metadata;
        _inspection = inspection;
        _inspectionApiFactory = inspectionApiFactory;
        _sessionRecords = sessionRecords;
        _operations = operations;
        _cachedStatus = cachedStatus;
        _processes = processes;
        _runAnalysis = runAnalysis;
        _envelope = envelope;
        _watchdog = watchdog;
        _installedVersion = installedVersion;

        var handlers = new Dictionary<
            string,
            Func<JsonElement?, CancellationToken, Task<object>>>(StringComparer.Ordinal)
        {
            ["analyze-and-extract"] = DispatchAnalyzeAndExtractAsync,
            ["cancel-operation"] = DispatchCancelOperationAsync,
            ["close-model"] = DispatchCloseModelAsync,
            ["extract-materials"] = DispatchExtractMaterialsAsync,
            ["extract-results"] = DispatchExtractResultsAsync,
            ["generate-e2k"] = DispatchGenerateE2KAsync,
            ["get-model-state"] = DispatchGetModelStateAsync,
            ["get-operation-events"] = DispatchGetOperationEventsAsync,
            ["get-operation-status"] = DispatchGetOperationStatusAsync,
            ["get-status"] = DispatchGetStatusAsync,
            ["inspect-wall-property"] = DispatchInspectWallPropertyAsync,
            ["list-wall-properties"] = DispatchListWallPropertiesAsync,
            ["open-model"] = DispatchOpenModelAsync,
            ["read-model-metadata"] = DispatchReadModelMetadataAsync,
            ["resolve-area-targets"] = DispatchResolveAreaTargetsAsync,
            ["run-analysis"] = DispatchRunAnalysisAsync,
            ["save-model"] = DispatchSaveModelAsync,
            ["shutdown-if-unchanged"] = DispatchShutdownIfUnchangedAsync,
            ["snapshot-export"] = DispatchSnapshotExportAsync,
            ["start-operation"] = DispatchStartOperationAsync,
            ["unlock-model"] = DispatchUnlockModelAsync
        };
        _handlers = handlers;
        Capabilities = handlers.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public Task<object> DispatchAsync(
        string command,
        JsonElement? request,
        CancellationToken ct)
    {
        // Freeze this request's declared intent and its CLI #24 label into one immutable
        // context, on the thread where the declaration is valid. Nothing about the session
        // is touched here: while an async operation runs, the worker holds the session lock
        // for its whole duration, and a protocol thread that blocked on it would stop
        // answering get-operation-status - the one thing that must keep working.
        //
        // That guarantee is now true of the legacy analyze-and-extract lane as well: it
        // ACCEPTS here and defers its answer, rather than awaiting the whole operation and
        // taking the reader down with it. Exactly one dispatch is ever executing, so the
        // ambient request state below stays single-writer.
        _pendingWork.Value = _envelope.Capture(command);

        return _handlers.TryGetValue(command, out var handler)
            ? handler(request, ct)
            : Task.FromResult<object>(Result.Fail(
                $"Command not supported in serve mode yet: '{command}'"));
    }

    private async Task<object> DispatchGetStatusAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        // During an async operation the protocol thread must never issue COM
        // calls. Report the most recent worker-owned snapshot instead. The same holds
        // while a command is stuck behind an unanswered ETABS dialog: the worker is busy.
        // And while a SYNCHRONOUS command runs (card C8): get-status is a control command,
        // accepted and answered without queueing behind the work it reports on.
        if (_operations.HasActiveOperation
            || _watchdog.IsBlocked
            || Volatile.Read(ref _synchronousInFlight) > 0)
        {
            return ReadActiveStatus();
        }

        var work = _pendingWork.Value;
        return await _operations.ExecuteSynchronousAsync(() => _envelope.RunAsync(work, () =>
        {
            try
            {
                var observation = _processes.ObserveEtabs();
                var current = _session.IsStarted
                    ? _status.GetStatusOnApp(_session.GetOrStart(), _session.ProcessId)
                    : Result.Ok(new GetStatusData());
                current = EtabsStatusOwnership.Decorate(
                    current,
                    observation,
                    _session.ProcessId);
                _cachedStatus.Update(current);
                return Task.FromResult<object>(WithDaemonFacts(current));
            }
            catch (Exception ex)
            {
                return Task.FromResult<object>(Result.Fail<GetStatusData>(
                    $"ETABS process observation failed: {ex.Message}"));
            }
        }));
    }

    private Task<object> DispatchStartOperationAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<StartOperationRequest>(request);
        return Task.FromResult<object>(_watchdog.IsBlocked
            ? _watchdog.BlockedFailure()
            : _operations.Start(req.Kind, req.Payload));
    }

    private Task<object> DispatchGetOperationStatusAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<OperationIdRequest>(request);
        return Task.FromResult<object>(_operations.GetStatus(req.OperationId));
    }

    private Task<object> DispatchGetOperationEventsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<GetOperationEventsRequest>(request);
        return Task.FromResult<object>(_operations.GetEvents(req.OperationId, req.SinceSeq));
    }

    private Task<object> DispatchCancelOperationAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeCancelRequest>(request);
        if (req.RequestId is { } requestId)
        {
            // The host abandons a shutdown-if-unchanged that is still Checking (card v2.4 §2).
            // A command already Exiting cannot be cancelled: its exit is won.
            return Task.FromResult<object>(_c10.Cancel(requestId) switch
            {
                ShutdownIfUnchangedState.Abandoned => Result.Ok(new CancelOperationData(
                    $"request:{requestId}",
                    OperationCancellationState.Honored)),
                null => Result.Fail<CancelOperationData>($"Request not found or not cancellable: '{requestId}'"),
                { } state => Result.Fail<CancelOperationData>(
                    $"Request '{requestId}' can no longer be cancelled (state {state})."),
            });
        }

        return Task.FromResult<object>(_operations.Cancel(
            req.OperationId ?? throw new InvalidOperationException(
                "cancel-operation needs an 'operationId' or a 'requestId'")));
    }

    /// <summary>
    /// The daemon's only user-visible ETABS intent.
    ///
    /// <para>Every other handler here is background work against a session that was
    /// created hidden and stays hidden. This one — and only this one — ends with ETABS on
    /// screen, because the engineer asked to look at a model.</para>
    /// </summary>
    private async Task<object> DispatchOpenModelAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeOpenModelRequest>(request);

        // Read on the protocol thread, while this request's frozen context is current.
        // Either signal keeps ETABS hidden: the request flag, or a request that declared the
        // background-consented intent. The engineer agreed to a HIDDEN second ETABS, so an
        // open that merely omits "background" must not put it on screen.
        var reveal = !req.Background
            && _pendingWork.Value.StartIntent != ManagedEtabsStartIntent.BackgroundByConsent;
        return await ExecuteWatchedComAsync(async () =>
        {
            var app = _session.GetOrStart();
            if (ModelVersionRefusal(req.ExpectedModelVersion) is { } refused)
            {
                return Result.Fail<OpenModelData>(refused);
            }

            var opened = await _open.OpenModelOnAppAsync(app, req.FilePath, req.SaveOnClose);

            // The analysis daemon's open (core-loop §2.4): the model is opened and ETABS
            // stays hidden. Only the explicit user-visible open ever reveals.
            return reveal ? RevealAfterConfirmedOpen(opened) : (object)opened;
        });
    }

    /// <summary>
    /// Shows ETABS once the requested model is confirmed open — never before.
    ///
    /// <para>The ordering is the point of CLI #22. Revealing first is exactly the
    /// packaged-RC symptom: a blank <c>(Untitled)</c> window on screen for seconds while
    /// the real model is still loading, which reads as a hung application. A failed open
    /// therefore reveals nothing at all.</para>
    ///
    /// <para>An open that cannot be made visible is reported as a FAILURE even though the
    /// model is loaded: "Open in ETABS" that leaves nothing on screen has not done what was
    /// asked, and the response names the CSI call that disagreed rather than claiming a
    /// success the engineer cannot see.</para>
    /// </summary>
    private Result<OpenModelData> RevealAfterConfirmedOpen(Result<OpenModelData> opened)
    {
        if (!opened.Success)
        {
            return opened;
        }

        var revealed = _session.RevealForExplicitUserRequest();
        return revealed.Success
            ? opened
            : Result.Fail<OpenModelData>(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                revealed.Error ?? "Managed ETABS could not be confirmed visible.",
                $"modelOpened={opened.Data?.FilePath}"));
    }

    /// <summary>
    /// The legacy synchronous command, kept synchronous ON THE WIRE without holding the
    /// protocol reader hostage for the length of an analysis.
    ///
    /// <para>Frozen Rust compatibility is unchanged: one request in, one response out, on
    /// the same id, carrying the original <c>Result&lt;AnalyzeAndExtractData&gt;</c>. What
    /// changed is WHO waits. This method used to <c>await</c> the entire operation - up to
    /// the 60-minute operation budget - and because <see cref="ServeLoop"/> awaits each
    /// dispatch before reading the next line, the daemon read NOTHING for that whole time.
    /// <c>get-operation-status</c>, <c>get-operation-events</c>, <c>cancel-operation</c>
    /// and even <c>shutdown</c> were unreachable exactly while they mattered most - the
    /// opposite of the guarantee stated in <see cref="DispatchAsync"/>.</para>
    ///
    /// <para>So only the WAITING moves. Everything that needs the live request - accepting
    /// the operation, and with it the <c>EtabsWorkContext</c> capture that has to happen on
    /// the protocol thread while the declaring request is alive - still happens here,
    /// synchronously, before this returns.</para>
    /// </summary>
    private Task<object> DispatchAnalyzeAndExtractAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        var payload = RequirePayload(request);
        if (_watchdog.IsBlocked)
        {
            // Accepting it would queue the operation behind the command a dialog is holding.
            return Task.FromResult<object>(_watchdog.BlockedFailure());
        }

        var started = _operations.Start("analyze-and-extract", payload);
        if (!started.Success || started.Data is null)
        {
            return Task.FromResult<object>(Result.Fail<AnalyzeAndExtractData>(
                started.Error ?? "Could not start analyze-and-extract operation"));
        }

        var operationId = started.Data.OperationId;
        var servedRequest = ServeRequestContext.Current;
        return Task.FromResult<object>(new DeferredServeResponse(NoteAbandonedAfter(
            _watchdog.GuardAsync(
                () => _session.ProcessId,
                () => _operations.WaitAsync(operationId, ct),
                ct),
            servedRequest)));
    }

    /// <summary>
    /// A command a modal dialog abandoned still holds the STA. Once its guard answered, the
    /// still-running work is recorded as an abandoned request (card C5), so the host can tell
    /// "outstanding work" from "none" and later see it finished.
    /// </summary>
    private async Task<object> NoteAbandonedAfter(Task<object> guarded, ServeRequestInfo? request)
    {
        var result = await guarded.ConfigureAwait(false);
        if (request is not null && _watchdog.BlockedWork is { } work)
        {
            _abandoned.Add(request.Id, request.Command, work);
        }

        return result;
    }

    /// <summary>
    /// The daemon's own facts about the instance it holds (card C5): the launch generation, the
    /// disposition, requests abandoned to a dialog, and what recovery detached. Never read from
    /// ETABS, so it is always available.
    /// </summary>
    private Result<GetStatusData> WithDaemonFacts(Result<GetStatusData> status)
    {
        if (!status.Success || status.Data is null)
        {
            return status;
        }

        var detached = _orphan.Detached;
        return Result.Ok(status.Data with
        {
            LaunchRecordId = _session.LaunchRecordId,
            Disposition = _session.Disposition,
            Abandoned = _abandoned.Snapshot(),
            OrphanDisposition = detached is null
                ? null
                : new OrphanDispositionInfo(new DetachedIdentity(
                    detached.Pid,
                    detached.ProcessStartTimeUtc,
                    detached.ExecutablePath))
        });
    }

    private async Task<object> DispatchSnapshotExportAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var loc = Deserialize<ServeFileLocator>(request);
        var snapReq = Deserialize<SnapshotExportRequest>(request);
        return await ExecuteComAsync(async () => await _snapshot.SnapshotExportOnAppAsync(
            _session.GetOrStart(), loc.FilePath, loc.OutputDir, snapReq));
    }

    private async Task<object> DispatchCloseModelAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeCloseModelRequest>(request);
        return await ExecuteComAsync(async () => await _close.CloseModelOnAppAsync(
            _session.GetOrStart(), req.Save));
    }

    /// <summary>
    /// Saves the session's open model to the requested path (EtabExtension #443).
    ///
    /// <para>Never starts ETABS: with no session there is no model to save, and a cold
    /// start would only open an empty one. That refusal is a plain failure, not a consent
    /// request.</para>
    /// </summary>
    private async Task<object> DispatchSaveModelAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeSaveModelRequest>(request);
        if (!_session.IsStarted)
        {
            return Result.Fail<SaveModelData>(
                "No managed ETABS session is running, so there is no open model to save.");
        }

        // An ordinary request: its answer is written when the save returns, and NOTHING is
        // chained to that answer. A reply that lands after the host stopped waiting is dropped
        // by the host and can never cause an exit (card v2.3 §2, I13').
        return await ExecuteWatchedComAsync(async () => await _save.SaveModelOnAppAsync(
            _session.GetOrStart(), req.FilePath, req.ExpectedDocument?.FileId));
    }

    /// <summary>
    /// <c>shutdown-if-unchanged</c> (C10, cards v2.3/v2.4): one STA command that refuses on a
    /// modal, re-reads the document and the change signal, and exits only if they still match.
    /// </summary>
    private async Task<object> DispatchShutdownIfUnchangedAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ShutdownIfUnchangedRequest>(request);
        if (_shutdownIfUnchanged is null)
        {
            return Result.Fail("shutdown-if-unchanged is not available in this daemon.");
        }

        // Refused without queueing, exactly like every other synchronous command: an operation
        // or an abandoned dialog already holds the STA, and nothing here may exit under them.
        if (_operations.HasActiveOperation)
        {
            return Result.Fail(
                "A daemon operation is active; shutdown-if-unchanged is unavailable until it completes. " +
                "ETABS was not exited.");
        }

        if (_watchdog.IsBlocked)
        {
            return _watchdog.BlockedFailure();
        }

        var requestId = ServeRequestContext.Current?.Id ?? 0;
        var work = _pendingWork.Value;
        return await _shutdownIfUnchanged.RunAsync(
            req,
            requestId,
            check => RunSynchronousAsync(() => _envelope.RunUncertifiedAsync(work, check)));
    }

    private async Task<object> DispatchUnlockModelAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeFileRequest>(request);
        return await ExecuteComAsync(async () => await _unlock.UnlockModelOnAppAsync(
            _session.GetOrStart(), req.FilePath));
    }

    private async Task<object> DispatchExtractResultsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        return await ExecuteComAsync(async () => await _extractResults.ExtractOnAppAsync(
            _session.GetOrStart(), Deserialize<ExtractResultsRequest>(request)));
    }

    private async Task<object> DispatchExtractMaterialsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        return await ExecuteComAsync(async () => await _extractMaterials.ExtractMaterialsOnAppAsync(
            _session.GetOrStart(), Deserialize<ExtractMaterialsRequest>(request)));
    }

    private async Task<object> DispatchGenerateE2KAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeGenerateE2KRequest>(request);
        return await ExecuteWatchedComAsync(async () =>
        {
            var app = _session.GetOrStart();
            return ModelVersionRefusal(req.ExpectedModelVersion) is { } refused
                ? Result.Fail(refused)
                : await _generateE2K.GenerateE2KOnAppAsync(
                    app, req.FilePath, req.OutputFile, req.Overwrite);
        });
    }

    private async Task<object> DispatchReadModelMetadataAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeFileRequest>(request);
        return await ExecuteComAsync(async () => await _metadata.ReadOnAppAsync(
            _session.GetOrStart(), req.FilePath));
    }

    private async Task<object> DispatchRunAnalysisAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeRunAnalysisRequest>(request);

        // Deliberately NOT under the modal-dialog watchdog in C0. A running analysis shows
        // the ETABS analysis monitor over a disabled main window, and the watchdog's current
        // modal test (visible, or owner disabled) may not tell it apart from a blocking box.
        // Watching run-analysis waits on the stricter modal test and its live probe (C0
        // follow-up, review finding P2-3). The commands that hit the DD-5 "older version"
        // box - open-model, save-model, generate-e2k - are watched, and so is the legacy
        // analyze-and-extract lane.
        return await ExecuteComAsync(async () => await _runAnalysis.RunAnalysisOnAppAsync(
            _session.GetOrStart(), req.FilePath, req.Cases, req.Units));
    }

    private Task<object> DispatchGetModelStateAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(
                _inspection.GetModelState(api, _sessionRecords.Read()));
        });
    }

    private Task<object> DispatchListWallPropertiesAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(_inspection.ListWallProperties(api));
        });
    }

    private Task<object> DispatchInspectWallPropertyAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<InspectWallPropertyRequest>(request);
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(_inspection.InspectWallProperty(api, req.Name));
        });
    }

    private Task<object> DispatchResolveAreaTargetsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ResolveAreaTargetsRequest>(request);
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(
                _inspection.ResolveAreaTargets(api, req.SourceProperty));
        });
    }

    private static T Deserialize<T>(JsonElement? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("Missing 'request' payload for this command");
        }

        return request.Value.Deserialize<T>(ServeJson.Options)
            ?? throw new InvalidOperationException("Request payload deserialised to null");
    }

    private static JsonElement RequirePayload(JsonElement? request) => request
        ?? throw new InvalidOperationException("Missing 'request' payload for this command");

    /// <summary>
    /// The context captured for the request currently being dispatched.
    ///
    /// <para>Written on the protocol thread at the top of
    /// <see cref="DispatchAsync"/> and read there again when the synchronous lane hands
    /// work to the COM worker - both on the same thread, and the serve loop dispatches
    /// strictly one request at a time. From the hand-off onwards the value travels as an
    /// immutable copy, so nothing the worker relies on can be overwritten underneath
    /// it.</para>
    ///
    /// <para>A DEFERRED response does not weaken that. The deferring handler reads this
    /// field (if at all) before it returns, and the loop only reads the next line once the
    /// dispatch task has completed - so a later request's write still cannot land while an
    /// earlier dispatch is looking at it. What outlives the request is the captured
    /// <see cref="EtabsWorkContext"/> copy, never this field.</para>
    /// </summary>
    private readonly AsyncLocal<EtabsWorkContext> _pendingWork = new();

    /// <summary>
    /// Every ETABS-backed command runs through here, which makes it the one place a
    /// CLI #24 certification can cover them all.
    ///
    /// <para>The check runs AFTER the work and BEFORE the response leaves: a background
    /// command that put ETABS on screen must not answer success just because ETABS hid
    /// itself again first. A failed certification replaces the result outright rather than
    /// annotating it - a partially-successful export whose session breached the visibility
    /// contract is not a success the desktop should act on.</para>
    /// </summary>
    /// <summary>
    /// The synchronous ETABS lane. Same envelope as the queued lane - captured context in,
    /// stage labelled, completion certified - so the two lanes cannot drift apart in what
    /// the visibility contract means.
    /// </summary>
    private Task<object> ExecuteComAsync(Func<Task<object>> action)
    {
        if (_operations.HasActiveOperation)
        {
            return Task.FromResult<object>(Result.Fail(
                "A daemon operation is active; synchronous ETABS commands are unavailable until it completes"));
        }

        // A command abandoned to an unanswered dialog still holds the STA worker; queueing
        // behind it would stop the protocol reader, so refuse with the same typed code.
        if (_watchdog.IsBlocked)
        {
            return Task.FromResult<object>(_watchdog.BlockedFailure());
        }

        var work = _pendingWork.Value;
        return RunSynchronousAsync(() => _envelope.RunAsync(work, action));
    }

    /// <summary>
    /// Queues one synchronous command on the STA and counts it while it is queued or running, so
    /// a concurrent get-status knows the worker is busy and answers from the daemon's own state
    /// (card C8).
    /// </summary>
    private async Task<object> RunSynchronousAsync(Func<Task<object>> work)
    {
        _ = Interlocked.Increment(ref _synchronousInFlight);
        try
        {
            return await _operations.ExecuteSynchronousAsync(work).ConfigureAwait(false);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _synchronousInFlight);
        }
    }

    /// <summary>
    /// <see cref="ExecuteComAsync"/> under the modal-dialog watchdog (core-loop §2.6), for
    /// the commands that open or write a model: a modal ETABS dialog fails the command with
    /// ETABS_MODAL_DIALOG instead of hanging it until the client times out.
    /// </summary>
    private Task<object> ExecuteWatchedComAsync(Func<Task<object>> action) =>
        NoteAbandonedAfter(
            _watchdog.GuardAsync(() => _session.ProcessId, () => ExecuteComAsync(action), CancellationToken.None),
            ServeRequestContext.Current);

    /// <summary>
    /// The version pre-check (core-loop §2.5), against the running ETABS's executable file
    /// version. Null when the caller stated no version or the model may open.
    /// </summary>
    private string? ModelVersionRefusal(string? expectedModelVersion)
    {
        if (expectedModelVersion is null)
        {
            return null;
        }

        return EtabsModelVersionCheck.Check(expectedModelVersion, _installedVersion.Read());
    }

    private Result<GetStatusData> ReadActiveStatus()
    {
        try
        {
            return WithDaemonFacts(_cachedStatus.Read(_session, _processes.ObserveEtabs()));
        }
        catch (Exception ex)
        {
            return Result.Fail<GetStatusData>(
                $"ETABS process observation failed: {ex.Message}");
        }
    }
}
