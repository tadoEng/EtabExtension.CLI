// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.RunAnalysis;

public static class RunAnalysisExtensions
{
    public static IServiceCollection AddRunAnalysisFeature(this IServiceCollection services)
    {
        services.AddScoped<IRunAnalysisService, RunAnalysisService>();
        return services;
    }
}
