// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Story Forces" table.
///
/// Rust typically passes all gravity + lateral load cases (no combos),
/// same set as Base Reactions:
///   LoadCases = ["DEAD", "LIVE", "SDL", "EQX", "EQY", "WIND_X", "WIND_Y"]
///
/// Story forces are always whole-model — no group filter needed or useful here.
///
/// Typical columns: Story, OutputCase, CaseType, Location,
///                  VX, VY, T, MX, MY, P
/// </summary>
public class StoryForcesExtractor : TableExtractorBase
{
    public StoryForcesExtractor(ILogger<StoryForcesExtractor> logger)
        : base(logger) { }

    public override string Slug => "story_forces";
    public override string Label => "Story Forces";
    protected override string EtabsTableKey => "Story Forces";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            LoadCases = filter.LoadCases,
            LoadCombos = filter.LoadCombos,
            LoadPatterns = filter.LoadPatterns,
            // Story forces are always whole-model — group filter ignored
            FieldKeys = filter.FieldKeys,
        };
}
