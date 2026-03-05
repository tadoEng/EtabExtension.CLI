// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Maps each property of <see cref="TableSelections"/> to its extractor.
///
/// WHY A REGISTRY INSTEAD OF REFLECTION OR AUTO-DISCOVERY:
///   • Explicit — adding a new table = one new extractor file + one line here.
///   • The mapping from TableSelections.XxxProperty → ITableExtractor is
///     compile-time safe. No magic strings or runtime scanning.
///   • The order in Entries controls both extraction order and progress output.
///
/// HOW TO ADD A NEW TABLE:
///   1. Create  Features/ExtractResults/Tables/MyNewTableExtractor.cs
///   2. Add a   TableFilter? MyNewTable property to TableSelections
///   3. Add one entry here:  new(s => s.MyNewTable, new MyNewTableExtractor(...))
///   That's it. The orchestrator picks it up automatically.
/// </summary>
public class TableExtractorRegistry
{
    /// <summary>
    /// Ordered list of (selector, extractor) pairs.
    /// The selector pulls the nullable TableFilter from the request's TableSelections.
    /// If the selector returns null the table is skipped for that run.
    /// </summary>
    public IReadOnlyList<TableRegistration> Entries { get; }

    public TableExtractorRegistry(
        StoryDefinitionsExtractor storyDefinitions,
        BaseReactionsExtractor baseReactions,
        StoryForcesExtractor storyForces,
        JointDriftsExtractor jointDrifts,
        PierForcesExtractor pierForces,
        PierSectionPropertiesExtractor pierSectionProperties)
    {
        Entries = new List<TableRegistration>
        {
            // ── Geometry / definitions (no load dependency) ───────────────────
            new(s => s.StoryDefinitions,     storyDefinitions),
            new(s => s.PierSectionProperties, pierSectionProperties),

            // ── Load-dependent results ────────────────────────────────────────
            new(s => s.BaseReactions,  baseReactions),
            new(s => s.StoryForces,    storyForces),
            new(s => s.JointDrifts,    jointDrifts),
            new(s => s.PierForces,     pierForces),
        };
    }
}

/// <summary>
/// Pairs a filter selector with its extractor implementation.
/// </summary>
public record TableRegistration(
    Func<TableSelections, TableFilter?> FilterSelector,
    ITableExtractor Extractor);
