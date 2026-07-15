using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabExtension.CLI.Features.ReadModelMetadata;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Serve;

public static class ServeExtensions
{
    public static IServiceCollection AddServeFeature(this IServiceCollection services)
    {
        // Scoped: the serve command creates one scope for the daemon's lifetime,
        // so the shared ETABS session is a single instance for that run and the
        // dispatcher can depend on the scoped feature services.
        services.AddScoped<ISessionRecordStore, JsonSessionRecordStore>();
        services.AddScoped<IProcessInspector, WindowsProcessInspector>();
        services.AddScoped<IManagedEtabsLauncher, ManagedEtabsLauncher>();
        services.AddScoped<IOrphanSessionCleaner, OrphanSessionCleaner>();
        services.AddScoped<IEtabsSession, EtabsSession>();
        services.AddScoped<IServeDispatcher, ServeDispatcher>();
        services.AddScoped<IReadModelMetadataService, ReadModelMetadataService>();
        return services;
    }
}
