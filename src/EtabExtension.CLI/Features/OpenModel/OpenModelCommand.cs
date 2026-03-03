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
        var command = new Command("open-model", "Open an .edb file in ETABS");

        var fileOption = new Option<string>("--file")
        {
            Description = "Path to .edb file",
            Required = true
        };
        fileOption.Aliases.Add("-f");

        var saveOption = new Option<bool>("--save")
        {
            Description = "Save the currently open model before switching (Mode A only)"
        };

        var noSaveOption = new Option<bool>("--no-save")
        {
            Description = "Discard unsaved changes without prompting (default)"
        };

        var newInstanceOption = new Option<bool>("--new-instance")
        {
            Description =
                "Start a new visible ETABS instance and open the file in it. " +
                "When omitted, opens in the already-running ETABS."
        };

        command.Options.Add(fileOption);
        command.Options.Add(saveOption);
        command.Options.Add(noSaveOption);
        command.Options.Add(newInstanceOption);

        command.SetAction(async parseResult =>
        {
            var filePath = parseResult.GetValue(fileOption)!;
            var save = parseResult.GetValue(saveOption);
            var newInstance = parseResult.GetValue(newInstanceOption);

            var service = services.GetRequiredService<IOpenModelService>();
            var result = await service.OpenModelAsync(filePath, save, newInstance);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
