// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Material Properties - Concrete Data" table.
///
/// This is a material definition table — no load cases, combos, or groups apply.
/// The filter from Rust is accepted for API consistency but only FieldKeys
/// is honoured; any load/group filters are silently ignored.
/// </summary>
public class MaterialPropertiesConcreteDataExtractor : TableExtractorBase
{
    public MaterialPropertiesConcreteDataExtractor(
        ILogger<MaterialPropertiesConcreteDataExtractor> logger)
        : base(logger) { }

    public override string Slug => "material_properties_concrete_data";
    public override string Label => "Material Properties - Concrete Data";
    public override bool RequiresAnalysis => false;

    protected override string EtabsTableKey => "Material Properties - Concrete Data";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // Material definition table — no load case, combo, or group filter.
            FieldKeys = filter.FieldKeys,
        };
}
