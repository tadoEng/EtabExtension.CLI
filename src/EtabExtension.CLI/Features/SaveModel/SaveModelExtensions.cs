// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.SaveModel;

public static class SaveModelExtensions
{
    public static IServiceCollection AddSaveModelFeature(this IServiceCollection services)
    {
        services.AddScoped<ISaveModelService, SaveModelService>();
        return services;
    }
}
