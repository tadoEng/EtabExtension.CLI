using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.AnalyzeAndExtract;

public static class AnalyzeAndExtractExtensions
{
    public static IServiceCollection AddAnalyzeAndExtractFeature(this IServiceCollection services)
    {
        services.AddSingleton<IAnalyzeAndExtractService, AnalyzeAndExtractService>();
        return services;
    }
}
