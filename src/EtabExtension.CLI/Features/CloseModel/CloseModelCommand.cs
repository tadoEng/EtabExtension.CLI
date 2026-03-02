// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.CloseModel;

public static class CloseModelCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("close-model", "Clear the ETABS workspace, leaving ETABS running with a blank model");

        var saveOption = new Option<bool>("--save") { Description = "Save the current model before clearing" };
        var noSaveOption = new Option<bool>("--no-save") { Description = "Discard changes without prompting (default)" };

        command.Options.Add(saveOption);
        command.Options.Add(noSaveOption);

        command.SetAction(async parseResult =>
        {
            var save = parseResult.GetValue(saveOption);
            var service = services.GetRequiredService<ICloseModelService>();
            var result = await service.CloseModelAsync(save);
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
