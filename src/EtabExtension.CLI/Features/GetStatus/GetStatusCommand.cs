// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using EtabExtension.CLI.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.GetStatus;

public static class GetStatusCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("get-status", "Return ETABS running state, PID, open file, lock and analysis status");

        command.SetAction(async _ =>
        {
            var service = services.GetRequiredService<IGetStatusService>();
            var result = await service.GetStatusAsync();
            Environment.Exit(result.ExitWithResult());
        });

        return command;
    }
}
