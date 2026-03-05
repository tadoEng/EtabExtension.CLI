// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Microsoft.Extensions.Logging;

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// Extracts the "Modal Participating Mass Ratios" table.
///
/// This is a model analyze table — no load cases, combos, or groups apply.
/// The filter from Rust is accepted for API consistency but only FieldKeys
/// is honoured; any load/group filters are silently ignored.
///
/// Typical columns: Case	Mode	Period	UX	UY	UZ	SumUX	SumUY	SumUZ	RX	RY	RZ	SumRX	SumRY	SumRZ

/// </summary>
public class ModalParticipatingMassRatios : TableExtractorBase
{
    public ModalParticipatingMassRatios(ILogger<ModalParticipatingMassRatios> logger)
        : base(logger) { }

    public override string Slug => "modal_participating_mass_ratios";
    public override string Label => "Modal Participating Mass Ratios";
    public override bool RequiresAnalysis => true;

    protected override string EtabsTableKey => "Modal Participating Mass Ratios";

    protected override TableQueryRequest BuildRequest(
        Features.ExtractResults.Models.TableFilter filter) =>
        new(EtabsTableKey)
        {
            // Modal results only — no load case, combo, or group filter.
            FieldKeys = filter.FieldKeys,
        };
}
