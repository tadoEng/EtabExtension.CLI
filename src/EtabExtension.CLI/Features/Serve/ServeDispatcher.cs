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
using EtabExtension.CLI.Features.ReadModelMetadata;
using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// Routes one serve request to the matching feature, executed against the single
/// shared ETABS session. All commands here operate on the SAME instance
/// (<see cref="IEtabsSession.GetOrStart"/>) — that is the fix for the
/// multi-instance golden-path bug.
/// </summary>
public sealed class ServeDispatcher : IServeDispatcher
{
    private readonly IEtabsSession _session;
    private readonly IGetStatusService _status;
    private readonly IOpenModelService _open;
    private readonly IAnalyzeAndExtractService _analyze;
    private readonly ISnapshotExportService _snapshot;
    private readonly ICloseModelService _close;
    private readonly IUnlockModelService _unlock;
    private readonly IExtractResultsService _extractResults;
    private readonly IExtractMaterialsService _extractMaterials;
    private readonly IGenerateE2KService _generateE2K;
    private readonly IReadModelMetadataService _metadata;

    public ServeDispatcher(
        IEtabsSession session,
        IGetStatusService status,
        IOpenModelService open,
        IAnalyzeAndExtractService analyze,
        ISnapshotExportService snapshot,
        ICloseModelService close,
        IUnlockModelService unlock,
        IExtractResultsService extractResults,
        IExtractMaterialsService extractMaterials,
        IGenerateE2KService generateE2K,
        IReadModelMetadataService metadata)
    {
        _session = session;
        _status = status;
        _open = open;
        _analyze = analyze;
        _snapshot = snapshot;
        _close = close;
        _unlock = unlock;
        _extractResults = extractResults;
        _extractMaterials = extractMaterials;
        _generateE2K = generateE2K;
        _metadata = metadata;
    }

    public async Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
    {
        switch (command)
        {
            case "get-status":
                // Lazy: don't start ETABS merely to poll status. Report not-running
                // until a command that needs the model has started the session.
                return _session.IsStarted
                    ? _status.GetStatusOnApp(_session.GetOrStart(), _session.ProcessId)
                    : Result.Ok(new GetStatusData { IsRunning = false });

            case "open-model":
            {
                var req = Deserialize<ServeOpenModelRequest>(request);
                return await _open.OpenModelOnAppAsync(
                    _session.GetOrStart(), req.FilePath, req.SaveOnClose);
            }

            case "analyze-and-extract":
            {
                // Flattened payload: {filePath, outputDir, <AnalyzeAndExtractRequest fields>}.
                var loc = Deserialize<ServeFileLocator>(request);
                var aeReq = Deserialize<AnalyzeAndExtractRequest>(request);
                return await _analyze.AnalyzeAndExtractOnAppAsync(
                    _session.GetOrStart(), loc.FilePath, loc.OutputDir, aeReq);
            }

            case "snapshot-export":
            {
                var loc = Deserialize<ServeFileLocator>(request);
                var snapReq = Deserialize<SnapshotExportRequest>(request);
                return await _snapshot.SnapshotExportOnAppAsync(
                    _session.GetOrStart(), loc.FilePath, loc.OutputDir, snapReq);
            }

            case "close-model":
            {
                var req = Deserialize<ServeCloseModelRequest>(request);
                return await _close.CloseModelOnAppAsync(_session.GetOrStart(), req.Save);
            }

            case "unlock-model":
            {
                var req = Deserialize<ServeFileRequest>(request);
                return await _unlock.UnlockModelOnAppAsync(_session.GetOrStart(), req.FilePath);
            }

            case "extract-results":
                return await _extractResults.ExtractOnAppAsync(
                    _session.GetOrStart(), Deserialize<ExtractResultsRequest>(request));

            case "extract-materials":
                return await _extractMaterials.ExtractMaterialsOnAppAsync(
                    _session.GetOrStart(), Deserialize<ExtractMaterialsRequest>(request));

            case "generate-e2k":
            {
                var req = Deserialize<ServeGenerateE2KRequest>(request);
                return await _generateE2K.GenerateE2KOnAppAsync(
                    _session.GetOrStart(), req.FilePath, req.OutputFile, req.Overwrite);
            }

            case "read-model-metadata":
            {
                var req = Deserialize<ServeFileRequest>(request);
                return await _metadata.ReadOnAppAsync(_session.GetOrStart(), req.FilePath);
            }

            default:
                return Result.Fail($"Command not supported in serve mode yet: '{command}'");
        }
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
}
