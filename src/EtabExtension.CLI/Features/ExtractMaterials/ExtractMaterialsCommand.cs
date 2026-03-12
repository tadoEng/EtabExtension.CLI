// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public static class ExtractMaterialsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "extract-materials",
            "Extract a material/geometry table from an .edb to a .parquet file via a hidden ETABS instance");

        // ── Required ──────────────────────────────────────────────────────────
        var fileOption = new Option<string>("--file")
        {
            Description = "Path to the .edb file",
            Required = true
        };
        fileOption.Aliases.Add("-f");

        var outputDirOption = new Option<string>("--output-dir")
        {
            Description = "Directory to write the .parquet file into. " +
                          "Filename is derived from the table key: {tableSlug}.parquet",
            Required = true
        };
        outputDirOption.Aliases.Add("-o");

        // ── Optional ──────────────────────────────────────────────────────────
        var tableKeyOption = new Option<string?>("--table-key")
        {
            Description = "ETABS database table key (default: \"Material List by Story\")",
            Required = false
        };
        tableKeyOption.Aliases.Add("-t");

        var unitsOption = new Option<string?>("--units")
        {
            Description =
                $"Unit preset to normalise to before extraction. " +
                $"Default: {EtabsUnitPreset.Default}. " +
                $"Valid: {string.Join(", ", EtabsUnitPreset.All)}",
            Required = false
        };
        unitsOption.Aliases.Add("-u");

        var fieldKeysOption = new Option<string[]?>("--field-keys")
        {
            Description = "Specific column keys to include (space-separated or repeat flag). " +
                          "Default: all columns.",
            Required = false,
            AllowMultipleArgumentsPerToken = true
        };

        command.Options.Add(fileOption);
        command.Options.Add(outputDirOption);
        command.Options.Add(tableKeyOption);
        command.Options.Add(unitsOption);
        command.Options.Add(fieldKeysOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var outputDir = parseResult.GetValue(outputDirOption)!;
            var tableKey = parseResult.GetValue(tableKeyOption);
            var units = parseResult.GetValue(unitsOption);
            var fieldKeys = parseResult.GetValue(fieldKeysOption);

            // Validate units before starting ETABS — fast failure with a clear message
            var (_, unitsError) = EtabsUnitPreset.Resolve(units);
            if (unitsError is not null)
            {
                var fail = Result.Fail<ExtractMaterialsData>(unitsError);
                Environment.Exit(fail.ExitWithResult());
                return;
            }

            var request = new ExtractMaterialsRequest
            {
                FilePath = filePath,
                OutputDir = outputDir,
                TableKey = tableKey,
                Units = units,
                FieldKeys = fieldKeys is { Length: > 0 } ? fieldKeys : null,
            };

            var service = services.GetRequiredService<IExtractMaterialsService>();
            var result = await service.ExtractMaterialsAsync(request);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
