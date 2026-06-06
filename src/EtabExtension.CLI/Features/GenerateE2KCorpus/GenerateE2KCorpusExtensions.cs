using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.GenerateE2KCorpus;

public static class GenerateE2KCorpusExtensions
{
    public static IServiceCollection AddGenerateE2KCorpusFeature(
        this IServiceCollection services)
    {
        services.AddScoped<IGenerateE2KCorpusService, GenerateE2KCorpusService>();
        return services;
    }
}
