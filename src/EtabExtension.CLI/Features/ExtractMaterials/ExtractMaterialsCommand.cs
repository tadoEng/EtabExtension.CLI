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
        var command = new Command(
            "extract-materials",
            "Extract material takeoff from an .edb to a .parquet file via ETABS DatabaseTables API");

        var fileOption = new Option<string>("--file")
        {
            Description = "Path to .edb file",
            Required = true
        };
        fileOption.Aliases.Add("-f");

        var outputOption = new Option<string>("--output")
        {
            Description = "Destination .parquet file path",
            Required = true
        };
        outputOption.Aliases.Add("-o");

        var tableKeyOption = new Option<string>("--table-key")
        {
            Description = "ETABS database table key (default: \"Material List by Story\")",
            Required = false
        };

        command.Options.Add(fileOption);
        command.Options.Add(outputOption);
        command.Options.Add(tableKeyOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var outputPath = parseResult.GetValue(outputOption)!;
            var tableKey = parseResult.GetValue(tableKeyOption);

            var service = services.GetRequiredService<IExtractMaterialsService>();

            var result = string.IsNullOrEmpty(tableKey)
                ? await service.ExtractMaterialsAsync(filePath, outputPath)
                : await service.ExtractMaterialsAsync(filePath, outputPath, tableKey);

            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
