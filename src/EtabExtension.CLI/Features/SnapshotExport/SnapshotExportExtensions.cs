using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.SnapshotExport;

public static class SnapshotExportExtensions
{
    public static IServiceCollection AddSnapshotExportFeature(this IServiceCollection services)
    {
        services.AddSingleton<ISnapshotExportService, SnapshotExportService>();
        return services;
    }
}
