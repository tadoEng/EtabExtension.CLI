using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// Registers shared ETABS infrastructure.
/// Individual features register their own services via their own Extensions classes.
/// </summary>
public static class EtabsExtensions
{
    public static IServiceCollection AddEtabsInfrastructure(this IServiceCollection services)
    {
        // Nothing shared at infra level — each feature owns its own ETABS lifecycle.
        // Mode A and Mode B instances are created per-command, not injected as singletons.

        // Shared parquet writer — singleton is fine, it is stateless
        services.AddSingleton<IParquetService, ParquetService>();

        // Factory for EtabsTableQueryService / EtabsTableEditingService.
        // Feature services inject IEtabsTableServicesFactory and call
        // factory.Create*(app) after they have opened the model.
        services.AddEtabsTableServices();

        return services;
    }
}
