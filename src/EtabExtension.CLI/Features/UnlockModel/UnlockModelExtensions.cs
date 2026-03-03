// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.UnlockModel;

public static class UnlockModelExtensions
{
    public static IServiceCollection AddUnlockModelFeature(this IServiceCollection services)
    {
        services.AddScoped<IUnlockModelService, UnlockModelService>();
        return services;
    }
}
