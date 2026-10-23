// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.SaveModel;

public static class SaveModelCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "save-model",
            "Save the open model to an explicit .edb path (must be the model ETABS has open)");

        var fileOption = new Option<string>("--file") { Description = "Full path of the open .edb to save to", Required = true };
        fileOption.Aliases.Add("-f");
        command.Options.Add(fileOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var service = services.GetRequiredService<ISaveModelService>();
            var result = await service.SaveModelAsync(filePath);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
