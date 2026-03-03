// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
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

        // Accept multiple case names: --cases "DEAD" "LIVE" "EX"
        // When omitted, all cases run (default behaviour).
        var casesOption = new Option<string[]?>("--cases")
        {
            Description =
                "Load case names to run. Repeat the flag or pass space-separated values. " +
                "When omitted, all cases run (default).",
            Required = false,
            AllowMultipleArgumentsPerToken = true
        };
        casesOption.Aliases.Add("-c");

        command.Options.Add(fileOption);
        command.Options.Add(casesOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var casesArray = parseResult.GetValue(casesOption);
            var cases = casesArray is { Length: > 0 } ? casesArray.ToList() : null;

            var service = services.GetRequiredService<IRunAnalysisService>();
            var result = await service.RunAnalysisAsync(filePath, cases);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
