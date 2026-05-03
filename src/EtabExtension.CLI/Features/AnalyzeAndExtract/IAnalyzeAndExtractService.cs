using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract;

public interface IAnalyzeAndExtractService
{
    Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractAsync(
        string filePath,
        string outputDir,
        AnalyzeAndExtractRequest request);
}
