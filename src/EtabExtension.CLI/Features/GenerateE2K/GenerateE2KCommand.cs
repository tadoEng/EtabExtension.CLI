using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.GenerateE2K;

public static class GenerateE2KCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("generate-e2k", "Export .edb to .e2k text format using a hidden ETABS instance");

        var fileOption = new Option<string>("--file") { Description = "Path to input .edb file", Required = true };
        fileOption.Aliases.Add("-f");

        var outputOption = new Option<string?>("--output") { Description = "Path for output .e2k (default: same dir as input)" };
        outputOption.Aliases.Add("-o");

        var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite output if it already exists" };

        command.Options.Add(fileOption);
        command.Options.Add(outputOption);
        command.Options.Add(overwriteOption);

        command.SetAction(async parseResult =>
        {
            var inputFile = parseResult.GetValue(fileOption)!;
            var outputFile = parseResult.GetValue(outputOption);
            var overwrite = parseResult.GetValue(overwriteOption);

            // Default output: same dir as input, .e2k extension
            if (string.IsNullOrEmpty(outputFile))
            {
                outputFile = Path.Combine(
                    Path.GetDirectoryName(inputFile) ?? ".",
                    Path.GetFileNameWithoutExtension(inputFile) + ".e2k");
            }

            var service = services.GetRequiredService<IGenerateE2KService>();
            var result = await service.GenerateE2KAsync(inputFile, outputFile, overwrite);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
