using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.SnapshotExport;

public interface ISnapshotExportService
{
    Task<Result<SnapshotExportData>> SnapshotExportAsync(
        string filePath,
        string outputDir,
        SnapshotExportRequest request);
}
