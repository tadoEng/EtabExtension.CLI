// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Pier Section Properties" table.
///
/// This is a geometry/section table — no load cases or combos apply.
/// The filter from Rust is accepted but only Groups and FieldKeys are honoured;
/// load filters are silently ignored.
///
/// Groups can be useful here to scope to a subset of piers:
///   Groups = ["Piers"]
///
/// Typical columns: Story, Pier, AxisAngle, CGX-Bot, CGY-Bot, CGX-Top, CGY-Top,
///                  WidthBot, ThickBot, WidthTop, ThickTop, Area-Bot, Area-Top
/// </summary>
public class PierSectionPropertiesExtractor : TableExtractorBase
{
    public PierSectionPropertiesExtractor(ILogger<PierSectionPropertiesExtractor> logger)
        : base(logger) { }

    public override string Slug => "pier_section_properties";
    public override string Label => "Pier Section Properties";
    protected override string EtabsTableKey => "Pier Section Properties";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // No load case / combo filter — this is a geometry/section table.
            // Groups is honoured so Rust can scope to a subset of piers.
            Groups = filter.Groups,
            FieldKeys = filter.FieldKeys,
        };
}
