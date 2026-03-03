// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;

/// <summary>
/// Standard unit system presets for ETABS models.
/// Maps to ETABSv1.eUnits enum values.
/// </summary>
public static class Units
{
    // US (Imperial) presets
    public static readonly object US_Lb_In = new UnitPreset("US_Lb_In");
    public static readonly object US_Lb_Ft = new UnitPreset("US_Lb_Ft");
    public static readonly object US_Kip_In = new UnitPreset("US_Kip_In");
    public static readonly object US_Kip_Ft = new UnitPreset("US_Kip_Ft");

    // SI (Metric) presets
    public static readonly object SI_N_mm = new UnitPreset("SI_N_mm");
    public static readonly object SI_N_m = new UnitPreset("SI_N_m");
    public static readonly object SI_kN_mm = new UnitPreset("SI_kN_mm");
    public static readonly object SI_kN_m = new UnitPreset("SI_kN_m");
    public static readonly object SI_kgf_m = new UnitPreset("SI_kgf_m");
    public static readonly object SI_tonf_m = new UnitPreset("SI_tonf_m");

    /// <summary>
    /// Internal marker class to represent unit presets.
    /// </summary>
    private sealed class UnitPreset
    {
        public string Name { get; }

        public UnitPreset(string name) => Name = name;

        public override string ToString() => Name;
    }
}
