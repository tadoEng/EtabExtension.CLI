// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public static class ExtractMaterialsExtensions
{
    public static IServiceCollection AddExtractMaterialsFeature(this IServiceCollection services)
    {
        // IParquetService            — registered by AddEtabsInfrastructure (singleton)
        // IEtabsTableServicesFactory — registered by AddEtabsInfrastructure (singleton)
        services.AddScoped<IExtractMaterialsService, ExtractMaterialsService>();
        return services;
    }
}
