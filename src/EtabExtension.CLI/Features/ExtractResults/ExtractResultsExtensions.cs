// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.ExtractResults;

public static class ExtractResultsExtensions
{
    /// <summary>
    /// Registers the extract-results feature.
    ///
    /// Depends on:
    ///   • IParquetService               (singleton, from AddEtabsInfrastructure)
    ///   • IEtabsTableServicesFactory    (singleton, from AddEtabsInfrastructure)
    ///
    /// Registers:
    ///   • Each concrete ITableExtractor as singleton
    ///   • TableExtractorRegistry as singleton (holds all extractors)
    ///   • IExtractResultsService as scoped
    /// </summary>
    public static IServiceCollection AddExtractResultsFeature(
        this IServiceCollection services)
    {
        // ── Individual table extractors (singleton — stateless, logger injected) ──
        services.AddSingleton<MaterialListByStoryExtractor>();
        services.AddSingleton<MaterialPropertiesConcreteDataExtractor>();
        services.AddSingleton<GroupAssignmentsExtractor>();
        services.AddSingleton<StoryDefinitionsExtractor>();
        services.AddSingleton<BaseReactionsExtractor>();
        services.AddSingleton<StoryForcesExtractor>();
        services.AddSingleton<JointDriftsExtractor>();
        services.AddSingleton<PierForcesExtractor>();
        services.AddSingleton<PierSectionPropertiesExtractor>();
        services.AddSingleton<ModalParticipatingMassRatios>();

        // ── Registry (singleton — just holds the ordered list) ────────────────
        services.AddSingleton<TableExtractorRegistry>();

        // ── Orchestrator ──────────────────────────────────────────────────────
        services.AddScoped<IExtractResultsService, ExtractResultsService>();

        return services;
    }
}
