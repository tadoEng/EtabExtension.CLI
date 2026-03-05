// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Base Reactions" table.
///
/// Rust typically passes all gravity + lateral load cases (no combos):
///   LoadCases = ["DEAD", "LIVE", "SDL", "EQX", "EQY", "WIND_X", "WIND_Y"]
///
/// Typical columns: OutputCase, CaseType, FX, FY, FZ, MX, MY, MZ
/// </summary>
public class BaseReactionsExtractor : TableExtractorBase
{
    public BaseReactionsExtractor(ILogger<BaseReactionsExtractor> logger)
        : base(logger) { }

    public override string Slug => "base_reactions";
    public override string Label => "Base Reactions";
    protected override string EtabsTableKey => "Base Reactions";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            LoadCases = filter.LoadCases,
            LoadCombos = filter.LoadCombos,
            // Base reactions are always whole-model — group filter ignored
            FieldKeys = filter.FieldKeys,
        };
}
