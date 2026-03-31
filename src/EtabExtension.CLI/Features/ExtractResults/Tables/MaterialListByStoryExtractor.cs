// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Material List by Story" table.
///
/// This is a material/geometry table — no load cases, combos, or groups apply.
/// The filter from Rust is accepted for API consistency but only FieldKeys
/// is honoured; any load/group filters are silently ignored.
/// </summary>
public class MaterialListByStoryExtractor : TableExtractorBase
{
    public MaterialListByStoryExtractor(ILogger<MaterialListByStoryExtractor> logger)
        : base(logger) { }

    public override string Slug => "material_list_by_story";
    public override string Label => "Material List by Story";
    public override bool RequiresAnalysis => false;

    protected override string EtabsTableKey => "Material List by Story";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // Material table — no load case, combo, or group filter.
            FieldKeys = filter.FieldKeys,
        };
}
