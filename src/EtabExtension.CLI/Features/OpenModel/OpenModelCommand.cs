// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.OpenModel;

public static class OpenModelCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("open-model", "Open an .edb file in the user's running ETABS");

        var fileOption = new Option<string>("--file") { Description = "Path to .edb file", Required = true };
        fileOption.Aliases.Add("-f");

        var saveOption = new Option<bool>("--save") { Description = "Save current model before switching" };
        var noSaveOption = new Option<bool>("--no-save") { Description = "Discard unsaved changes (default)" };

        command.Options.Add(fileOption);
        command.Options.Add(saveOption);
        command.Options.Add(noSaveOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var save = parseResult.GetValue(saveOption);
            // --save takes priority; default is no-save

            var service = services.GetRequiredService<IOpenModelService>();
            var result = await service.OpenModelAsync(filePath, save);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
