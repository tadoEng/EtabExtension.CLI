// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.RunAnalysis;

public static class RunAnalysisCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "run-analysis",
            "Run analysis on an .edb using a hidden ETABS instance. Results are saved back into the .edb.");

        var fileOption = new Option<string>("--file")
        {
            Description = "Path to .edb file",
            Required = true
        };
        fileOption.Aliases.Add("-f");

        var casesOption = new Option<string[]?>("--cases")
        {
            Description =
                "Load case names to run (space-separated or repeat flag). " +
                "When omitted, all cases run (default).",
            Required = false,
            AllowMultipleArgumentsPerToken = true
        };
        casesOption.Aliases.Add("-c");

        var unitsOption = new Option<string?>("--units")
        {
            Description =
                $"Unit preset to normalise to before running analysis. " +
                $"Default: {EtabsUnitPreset.Default}. " +
                $"Valid: {string.Join(", ", EtabsUnitPreset.All)}",
            Required = false
        };
        unitsOption.Aliases.Add("-u");

        command.Options.Add(fileOption);
        command.Options.Add(casesOption);
        command.Options.Add(unitsOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var casesArray = parseResult.GetValue(casesOption);
            var cases = casesArray is { Length: > 0 } ? casesArray.ToList() : null;
            var units = parseResult.GetValue(unitsOption);

            // Validate units before starting ETABS
            var (_, unitsError) = EtabsUnitPreset.Resolve(units);
            if (unitsError is not null)
            {
                var fail = Result.Fail<EtabExtension.CLI.Features.RunAnalysis.Models.RunAnalysisData>(unitsError);
                Environment.Exit(fail.ExitWithResult());
                return;
            }

            var service = services.GetRequiredService<IRunAnalysisService>();
            var result = await service.RunAnalysisAsync(filePath, cases, units);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
