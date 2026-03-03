// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public static class ExtractMaterialsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("extract-materials", "Extract material takeoff from an analyzed .edb to takeoff.parquet");

        var fileOption = new Option<string>("--file") { Description = "Path to .edb file", Required = true };
        var outputOption = new Option<string>("--output") { Description = "Path for output .parquet file", Required = true };
        fileOption.Aliases.Add("-f");
        outputOption.Aliases.Add("-o");

        command.Options.Add(fileOption);
        command.Options.Add(outputOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var outputPath = parseResult.GetValue(outputOption)!;
            var service = services.GetRequiredService<IExtractMaterialsService>();
            var result = await service.ExtractMaterialsAsync(filePath, outputPath);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
