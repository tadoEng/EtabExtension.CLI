// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Story Definitions" table.
///
/// This is a pure geometry table — no load cases, combos, or groups apply.
/// The filter from Rust is accepted for API consistency but only FieldKeys
/// is honoured; any load/group filters are silently ignored.
///
/// Typical columns: Story, Height, Elevation, MasterStory, SimilarTo, SpliceAbove, SpliceHeight
/// </summary>
public class StoryDefinitionsExtractor : TableExtractorBase
{
    public StoryDefinitionsExtractor(ILogger<StoryDefinitionsExtractor> logger)
        : base(logger) { }

    public override string Slug => "story_definitions";
    public override string Label => "Story Definitions";
    /// Geometry table — always available, even on unanalyzed models.
    public override bool RequiresAnalysis => false;

    protected override string EtabsTableKey => "Story Definitions";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // No load case / combo / group filter — this is a geometry-only table.
            FieldKeys = filter.FieldKeys,
        };
}
