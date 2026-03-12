// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.System.Models;
using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;

/// <summary>
/// Maps a unit preset string (from --units CLI flag or JSON field) to the
/// <see cref="EtabSharp.System.Models.Units"/> object consumed by
/// <see cref="EtabSharp.Interfaces.System.IUnitSystem.SetPresentUnits"/>.
///
/// WHY NOT A set-units COMMAND:
///   A persistent global unit config stored on disk would create hidden shared
///   state that both C# CLI and Rust would need to read and agree on. That coupling
///   is fragile and hard to test. Instead every Mode B command accepts an explicit
///   --units flag. Rust reads the desired unit system from the Excel config file
///   and passes it on each invocation — explicit, auditable, per-project.
///
/// VALID PRESET STRINGS (case-insensitive):
///   US_Kip_Ft (default)  US_Kip_In  US_Lb_Ft  US_Lb_In
///   SI_kN_m   SI_kN_mm   SI_N_m     SI_N_mm   SI_kgf_m  SI_tonf_m
/// </summary>
public static class EtabsUnitPreset
{
    public const string Default = "US_Kip_Ft";

    /// <summary>
    /// All valid preset names in display order.
    /// Exposed so commands can embed the list in --units help text.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        "US_Kip_Ft", "US_Kip_In", "US_Lb_Ft", "US_Lb_In",
        "SI_kN_m",   "SI_kN_mm",  "SI_N_m",   "SI_N_mm",
        "SI_kgf_m",  "SI_tonf_m",
    ];

    /// <summary>
    /// Resolves a preset string to a <see cref="EtabSharp.System.Models.Units"/> object
    /// ready to pass directly to IUnitSystem.SetPresentUnits.
    ///
    /// Returns (units, null) on success.
    /// Returns (US_Kip_Ft units, errorMessage) when the preset is unrecognised.
    /// null or empty → defaults to US_Kip_Ft without error.
    /// </summary>
    public static (Units Units, string? Error) Resolve(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
            return (KipFt, null);

        return preset.Trim().ToUpperInvariant() switch
        {
            "US_KIP_FT" => (KipFt, null),
            "US_KIP_IN" => (Make(eForce.kip, eLength.inch, eTemperature.F), null),
            "US_LB_FT" => (Make(eForce.lb, eLength.ft, eTemperature.F), null),
            "US_LB_IN" => (Make(eForce.lb, eLength.inch, eTemperature.F), null),
            "SI_KN_M" => (Make(eForce.kN, eLength.m, eTemperature.C), null),
            "SI_KN_MM" => (Make(eForce.kN, eLength.mm, eTemperature.C), null),
            "SI_N_M" => (Make(eForce.N, eLength.m, eTemperature.C), null),
            "SI_N_MM" => (Make(eForce.N, eLength.mm, eTemperature.C), null),
            "SI_KGF_M" => (Make(eForce.kgf, eLength.m, eTemperature.C), null),
            "SI_TONF_M" => (Make(eForce.tonf, eLength.m, eTemperature.C), null),
            _ => (KipFt,
                             $"Unknown unit preset '{preset}'. " +
                             $"Valid values: {string.Join(", ", All)}")
        };
    }

    // ── Cached default ────────────────────────────────────────────────────────
    private static Units KipFt => Make(eForce.kip, eLength.ft, eTemperature.F);

    private static Units Make(eForce force, eLength length, eTemperature temperature) =>
        new() { Force = force, Length = length, Temperature = temperature };
}
