// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.UnlockModel;

public static class UnlockModelCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("unlock-model", "Clear the post-analysis lock on the currently open model");

        var fileOption = new Option<string>("--file") { Description = "Path to .edb file (must already be open in ETABS)", Required = true };
        fileOption.Aliases.Add("-f");
        command.Options.Add(fileOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var service = services.GetRequiredService<IUnlockModelService>();
            var result = await service.UnlockModelAsync(filePath);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
