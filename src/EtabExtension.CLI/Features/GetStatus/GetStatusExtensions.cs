// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.GetStatus;

public static class GetStatusExtensions
{
    public static IServiceCollection AddGetStatusFeature(this IServiceCollection services)
    {
        services.AddScoped<IGetStatusService, GetStatusService>();
        return services;
    }
}
