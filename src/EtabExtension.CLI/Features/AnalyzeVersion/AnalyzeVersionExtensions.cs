using EtabExtension.CLI.Features.Serve.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.AnalyzeVersion;

public static class AnalyzeVersionExtensions
{
    /// <summary>
    /// Registers the daemon's <c>analyze-version</c> operation with its service and ETABS seam.
    /// The operation is the only thing that makes the dispatcher advertise the command.
    /// </summary>
    public static IServiceCollection AddAnalyzeVersionOperation(this IServiceCollection services)
    {
        services.AddScoped<IAnalyzeVersionService, AnalyzeVersionService>();
        services.AddScoped<IAnalyzeVersionApiFactory, AnalyzeVersionApiFactory>();
        services.AddScoped<IOperationDefinition, AnalyzeVersionOperation>();
        return services;
    }
}
