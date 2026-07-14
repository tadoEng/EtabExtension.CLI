using EtabExtension.CLI.Features.GenerateE2K.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.GenerateE2K;

public interface IGenerateE2KService
{
    /// <summary>
    /// Exports an .edb to .e2k text format using a hidden ETABS instance (Mode B).
    /// Never attaches to the user's running ETABS.
    /// </summary>
    Task<Result<GenerateE2KData>> GenerateE2KAsync(string inputFilePath, string outputFilePath, bool overwrite);
    Task<Result<GenerateE2KData>> GenerateE2KOnAppAsync(ETABSApplication app, string inputFilePath, string outputFilePath, bool overwrite);
}
