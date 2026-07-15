using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract;

public interface IAnalyzeAndExtractService
{
    /// <summary>One-shot: starts its own hidden ETABS, runs, and disposes it.</summary>
    Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractAsync(
        string filePath,
        string outputDir,
        AnalyzeAndExtractRequest request);

    /// <summary>
    /// Daemon path: runs against an already-started, caller-owned ETABS instance
    /// (the shared serve session). Does NOT create or dispose the app.
    /// </summary>
    Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractOnAppAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        AnalyzeAndExtractRequest request,
        IEtabsOperationProgress? progress = null);
}
