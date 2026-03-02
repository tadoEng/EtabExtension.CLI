// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.OpenModel;

public static class OpenModelExtensions
{
    public static IServiceCollection AddOpenModelFeature(this IServiceCollection services)
    {
        services.AddScoped<IOpenModelService, OpenModelService>();
        return services;
    }
}
