using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
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

    public ServeDispatcher(
        IEtabsSession session,
        IGetStatusService status,
        IOpenModelService open,
        IAnalyzeAndExtractService analyze,
        ISnapshotExportService snapshot)
    {
        _session = session;
        _status = status;
        _open = open;
        _analyze = analyze;
        _snapshot = snapshot;
    }

    public async Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
    {
        switch (command)
        {
            case "get-status":
                // Lazy: don't start ETABS merely to poll status. Report not-running
                // until a command that needs the model has started the session.
                return _session.IsStarted
                    ? _status.GetStatusOnApp(_session.GetOrStart())
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

            // TODO(#188 follow-up): unlock-model, close-model, extract-results,
            // read-model-metadata against the shared session.
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
