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
        var command = new Command("run-analysis", "Run complete analysis on an .edb using a hidden ETABS instance");

        var fileOption = new Option<string>("--file") { Description = "Path to .edb file", Required = true };
        fileOption.Aliases.Add("-f");
        command.Options.Add(fileOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var service = services.GetRequiredService<IRunAnalysisService>();
            var result = await service.RunAnalysisAsync(filePath);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
