// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Contract for a single ETABS table extractor.
///
/// Each ETABS table gets its own implementation so:
///   • The ETABS table key lives with the table, not the orchestrator.
///   • Adding a new table = one new file + one line in <see cref="TableExtractorRegistry"/>.
///   • Each extractor can document its own filter expectations and column list.
///
/// The orchestrator (<see cref="ExtractResultsService"/>) iterates
/// <see cref="TableExtractorRegistry.Entries"/>, skips tables whose filter is null,
/// and calls Extract on the rest.
/// </summary>
public interface ITableExtractor
{
    /// <summary>
    /// Stable snake_case slug used as the output filename stem and as the
    /// dictionary key in the result JSON.
    /// e.g. "base_reactions", "joint_drifts"
    /// </summary>
    string Slug { get; }

    /// <summary>
    /// Human-readable label for progress output.
    /// e.g. "Base Reactions", "Joint Drifts"
    /// </summary>
    string Label { get; }

    /// <summary>
    /// True when this table requires the model to have been analyzed and locked.
    /// False for geometry/definition tables that are always available.
    ///
    /// When false: extracted even on unanalyzed models (Story Definitions, Pier Section Properties).
    /// When true:  skipped with a clear error if model is not analyzed/locked,
    ///             preventing ETABS from entering a corrupted display state.
    /// </summary>
    bool RequiresAnalysis { get; }

    /// <summary>
    /// Extract the table, write a .parquet file to <paramref name="outputDir"/>,
    /// and return the outcome.
    /// </summary>
    /// <param name="filter">
    /// Filter from the request (never null — the orchestrator skips null filters).
    /// </param>
    /// <param name="outputDir">Directory to write {slug}.parquet into.</param>
    /// <param name="queryService">Pre-built query service bound to the open model.</param>
    /// <param name="parquet">Parquet writer.</param>
    Task<TableExtractionOutcome> ExtractAsync(
        TableFilter filter,
        string outputDir,
        IEtabsTableQueryService queryService,
        IParquetService parquet);
}
