// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Material Properties - General" table, narrowed to the two columns a
/// consumer needs to classify a material: <c>Material</c> and <c>Type</c> (the ETABS
/// material type: Concrete, Steel, Rebar, ...).
///
/// This is a material definition table, so no load case, combo or group applies.
/// The caller's filter is accepted for API consistency but is deliberately ignored:
/// the field keys are fixed to <see cref="FixedFieldKeys"/>.
/// </summary>
public class MaterialPropertiesGeneralExtractor : TableExtractorBase
{
    /// <summary>The only fields this extractor requests, whatever the caller's filter says.</summary>
    public static readonly string[] FixedFieldKeys = ["Material", "Type"];

    public MaterialPropertiesGeneralExtractor(
        ILogger<MaterialPropertiesGeneralExtractor> logger)
        : base(logger) { }

    public override string Slug => "material_properties_general";
    public override string Label => "Material Properties - General";
    public override bool RequiresAnalysis => false;

    protected override string EtabsTableKey => "Material Properties - General";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // Material definition table: no load case, combo or group filter, and a
            // fixed two-column projection.
            FieldKeys = [.. FixedFieldKeys],
        };
}
