// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Pier Forces" table.
///
/// Rust typically passes envelope LRFD combos and a piers group:
///   LoadCombos = ["ENV-LRFD-MAX", "ENV-LRFD-MIN"]
///   Groups     = ["Piers"]
///
/// This is a full pass-through — Rust fully controls both the combo selection
/// and the group scope for this table.
///
/// Typical columns: Story, Pier, OutputCase, CaseType,
///                  Location, P, V2, V3, T, M2, M3
/// </summary>
public class PierForcesExtractor : TableExtractorBase
{
    public PierForcesExtractor(ILogger<PierForcesExtractor> logger)
        : base(logger) { }

    public override string Slug => "pier_forces";
    public override string Label => "Pier Forces";
    protected override string EtabsTableKey => "Pier Forces";

    // Full pass-through — Rust controls combos AND groups for this table
    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            LoadCases = filter.LoadCases,
            LoadCombos = filter.LoadCombos,
            Groups = filter.Groups,
            FieldKeys = filter.FieldKeys,
        };
}
