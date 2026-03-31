// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Group Assignments" table.
///
/// This is a group membership table — no load cases, combos, or ETABS group
/// scoping apply to the query itself. The filter from Rust is accepted for API
/// consistency but only FieldKeys is honoured; any load/group filters are silently ignored.
/// </summary>
public class GroupAssignmentsExtractor : TableExtractorBase
{
    public GroupAssignmentsExtractor(ILogger<GroupAssignmentsExtractor> logger)
        : base(logger) { }

    public override string Slug => "group_assignments";
    public override string Label => "Group Assignments";
    public override bool RequiresAnalysis => false;

    protected override string EtabsTableKey => "Group Assignments";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // Group membership table — query the table directly, no ETABS group filter.
            FieldKeys = filter.FieldKeys,
        };
}
