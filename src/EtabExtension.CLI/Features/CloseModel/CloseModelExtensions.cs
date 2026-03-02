// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.CloseModel;

public static class CloseModelExtensions
{
    public static IServiceCollection AddCloseModelFeature(this IServiceCollection services)
    {
        services.AddScoped<ICloseModelService, CloseModelService>();
        return services;
    }
}
