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
        return services;
    }
}
