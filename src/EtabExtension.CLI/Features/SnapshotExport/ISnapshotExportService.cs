using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.SnapshotExport;

public interface ISnapshotExportService
{
    Task<Result<SnapshotExportData>> SnapshotExportAsync(
        string filePath,
        string outputDir,
        SnapshotExportRequest request);

    /// <summary>Daemon: export against the shared serve-session instance.</summary>
    Task<Result<SnapshotExportData>> SnapshotExportOnAppAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        SnapshotExportRequest request);
}
