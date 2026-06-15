using System.Diagnostics;
using EtabExtension.CLI.Features.GenerateE2K.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.GenerateE2K;

public class GenerateE2KService : IGenerateE2KService
{
    private readonly IEtabsBootstrapService _bootstrap;

    public GenerateE2KService(IEtabsBootstrapService bootstrap)
    {
        _bootstrap = bootstrap;
    }

    public async Task<Result<GenerateE2KData>> GenerateE2KAsync(
        string inputFilePath,
        string outputFilePath,
        bool overwrite)
    {
        // Input validation — before touching COM
        if (!inputFilePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<GenerateE2KData>("Input must be an .edb file");

        if (File.Exists(outputFilePath) && !overwrite)
            return Result.Fail<GenerateE2KData>(
                $"Output file already exists: {outputFilePath}. Use --overwrite to replace.");

        var outputDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var bootstrapResult = await _bootstrap.BootstrapAsync(inputFilePath);
        if (!bootstrapResult.Success || bootstrapResult.Data is null)
            return Result.Fail<GenerateE2KData>(bootstrapResult.Error ?? "Bootstrap failed");

        using var context = bootstrapResult.Data;
        var app = context.App;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Console.Error.WriteLine("ℹ Exporting to .e2k...");
            int exportRet = app.Model.Files.ExportFile(outputFilePath, eFileTypeIO.TextFile);
            stopwatch.Stop();

            if (exportRet != 0 || !File.Exists(outputFilePath))
                return Result.Fail<GenerateE2KData>(
                    $"ExportFile failed (ret={exportRet})");

            var fileSize = new FileInfo(outputFilePath).Length;
            Console.Error.WriteLine($"✓ Exported ({fileSize / 1024.0:F1} KB, {stopwatch.ElapsedMilliseconds} ms)");

            return Result.Ok(new GenerateE2KData
            {
                InputFile = inputFilePath,
                OutputFile = outputFilePath,
                FileSizeBytes = fileSize,
                GenerationTimeMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<GenerateE2KData>($"ETABS COM error: {ex.Message}");
        }
    }
}
