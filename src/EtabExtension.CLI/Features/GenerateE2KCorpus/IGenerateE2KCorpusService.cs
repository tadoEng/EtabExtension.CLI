using EtabExtension.CLI.Features.GenerateE2KCorpus.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public interface IGenerateE2KCorpusService
{
    /// <summary>
    /// Creates deterministic ETABS models through the ETABS API, saves each
    /// model as EDB, and exports the corresponding E2K text artifact.
    /// </summary>
    Task<Result<GenerateE2KCorpusData>> GenerateAsync(
        string outputDir,
        GenerateE2KCorpusRequest request);
}
