// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Joint Drifts" table.
///
/// Rust typically passes only lateral load cases and a group of control joints:
///   LoadCases = ["EQX", "EQY", "WIND_X", "WIND_Y"]
///   Groups    = ["DriftJoints"]           ← or ["AllJoints"] for full model
///
/// Without a group filter this table can be very large (every joint × every case).
/// It is strongly recommended that Rust always passes a Groups filter.
///
/// Typical columns: Joint, OutputCase, CaseType, StepType, StepNum,
///                  U1, U2, U3, R1, R2, R3 (displacements/rotations)
///                  or Drift1, Drift2, Drift3 depending on ETABS version/settings.
/// </summary>
public class JointDriftsExtractor : TableExtractorBase
{
    public JointDriftsExtractor(ILogger<JointDriftsExtractor> logger)
        : base(logger) { }

    public override string Slug => "joint_drifts";
    public override string Label => "Joint Drifts";
    protected override string EtabsTableKey => "Joint Drifts";

    // Full pass-through — Rust controls cases AND groups for this table
    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            LoadCases = filter.LoadCases,
            LoadCombos = filter.LoadCombos,
            LoadPatterns = filter.LoadPatterns,
            Groups = filter.Groups,
            FieldKeys = filter.FieldKeys,
        };
}
