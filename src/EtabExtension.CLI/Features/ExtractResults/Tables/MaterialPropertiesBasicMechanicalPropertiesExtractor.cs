// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Material Properties - Basic Mechanical Properties" table, narrowed to
/// the two columns a consumer needs to recover a material's volume from its weight:
/// <c>Material</c> and <c>UnitWeight</c> (the weight per unit volume ETABS uses for
/// self-weight, in the run's force/length units).
///
/// This is a material definition table, so no load case, combo or group applies.
/// The caller's filter is accepted for API consistency but is deliberately ignored:
/// the field keys are fixed to <see cref="FixedFieldKeys"/>. The mass density column
/// (<c>UnitMass</c>) is intentionally NOT extracted, because it can be edited
/// independently of <c>UnitWeight</c> and self-weight follows <c>UnitWeight</c>.
/// </summary>
public class MaterialPropertiesBasicMechanicalPropertiesExtractor : TableExtractorBase
{
    /// <summary>The only fields this extractor requests, whatever the caller's filter says.</summary>
    public static readonly string[] FixedFieldKeys = ["Material", "UnitWeight"];

    public MaterialPropertiesBasicMechanicalPropertiesExtractor(
        ILogger<MaterialPropertiesBasicMechanicalPropertiesExtractor> logger)
        : base(logger) { }

    public override string Slug => "material_properties_basic_mechanical_properties";
    public override string Label => "Material Properties - Basic Mechanical Properties";
    public override bool RequiresAnalysis => false;

    protected override string EtabsTableKey => "Material Properties - Basic Mechanical Properties";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // Material definition table: no load case, combo or group filter, and a
            // fixed two-column projection.
            FieldKeys = [.. FixedFieldKeys],
        };
}
