// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;
using EtabSharp.System.Models;
using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;

/// <summary>
/// Implementation of IEtabsUnitService.
///
/// Constructed per-command (not injected as singleton) because it needs an active
/// ETABSApplication instance. The service does not own the app — it only reads and
/// sets units on the model the caller already opened.
///
/// Typical usage in a Mode B service:
///
///   app = ETABSWrapper.CreateNew();
///   app.Application.Hide();
///   app.Model.Files.OpenFile(filePath);
///
///   var unitService = new EtabsUnitService(app);
///   var snapshot    = await unitService.ReadAndNormaliseAsync(Units.US_Kip_Ft);
///   Console.Error.WriteLine(unitService.FormatSnapshot(snapshot));
///
///   // ... extraction ...
///
///   await unitService.RestoreAsync(snapshot);  // restores before save
///   app.Model.Files.SaveFile(filePath);
/// </summary>
public class EtabsUnitService : IEtabsUnitService
{
    private readonly ETABSApplication _app;

    public EtabsUnitService(ETABSApplication app)
    {
        _app = app;
    }

    /// <inheritdoc />
    public async Task<UnitSnapshot> ReadAndNormaliseAsync(object targetUnits)
    {
        await Task.CompletedTask;

        var original = ReadCurrent();

        // Convert the Units preset to the underlying eUnits enum so we can
        // check if the model is already in the target system
        var targetEnum = UnitsPresetToEnum(targetUnits);
        var alreadySet = original.RawUnitEnum == (int)targetEnum;

        if (!alreadySet)
        {
            // targetUnits is passed as-is to SetPresentUnits; it should be compatible with EtabSharp's Units type
            _app.Model.Units.SetPresentUnits((dynamic)targetUnits);
        }

        var active = alreadySet ? original : ReadCurrent();

        return new UnitSnapshot
        {
            Original = original,
            Active = active,
            WasChanged = !alreadySet
        };
    }

    /// <inheritdoc />
    public async Task RestoreAsync(UnitSnapshot snapshot)
    {
        await Task.CompletedTask;

        if (!snapshot.WasChanged) return; // nothing to undo

        // Restore via the raw enum value that was captured at snapshot time
        var originalEnum = (eUnits)snapshot.Original.RawUnitEnum;
        _app.SapModel.SetPresentUnits(originalEnum);
    }

    /// <inheritdoc />
    public async Task<UnitInfo> ReadCurrentAsync()
    {
        await Task.CompletedTask;
        return ReadCurrent();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private UnitInfo ReadCurrent()
    {
        var units = _app.Model.Units.GetPresentUnits();
        var rawEnum = _app.SapModel.GetPresentUnits(); // returns int eUnits value

        return new UnitInfo
        {
            Force = ToForceSymbol(units.Force),
            Length = ToLengthSymbol(units.Length),
            Temperature = ToTemperatureSymbol(units.Temperature),
            IsUS = units.IsUS,
            IsMetric = units.IsMetric,
            RawUnitEnum = (int)rawEnum
        };
    }

    /// <summary>
    /// Formats a UnitSnapshot as a single stderr progress line.
    /// Example: "ℹ Units: kip/ft/F (isUS=True) → was kN/m/C (isMetric=True), normalised"
    /// </summary>
    public static string FormatSnapshot(UnitSnapshot snapshot)
    {
        var active = $"{snapshot.Active.Force}/{snapshot.Active.Length}/{snapshot.Active.Temperature}" +
                     $" (isUS={snapshot.Active.IsUS}, isMetric={snapshot.Active.IsMetric})";

        if (!snapshot.WasChanged)
            return $"ℹ Units: {active} (no change needed)";

        var original = $"{snapshot.Original.Force}/{snapshot.Original.Length}/{snapshot.Original.Temperature}";
        return $"ℹ Units normalised: {original} → {active}";
    }

    // ── Symbol helpers — same as demo script ─────────────────────────────────

    private static string ToForceSymbol(eForce force) => force switch
    {
        eForce.lb => "lb",
        eForce.kip => "kip",
        eForce.N => "N",
        eForce.kN => "kN",
        eForce.kgf => "kgf",
        eForce.tonf => "tonf",
        _ => force.ToString()
    };

    private static string ToLengthSymbol(eLength length) => length switch
    {
        eLength.inch => "in",
        eLength.ft => "ft",
        eLength.mm => "mm",
        eLength.cm => "cm",
        eLength.m => "m",
        _ => length.ToString()
    };

    private static string ToTemperatureSymbol(eTemperature temperature) => temperature switch
    {
        eTemperature.F => "F",
        eTemperature.C => "C",
        _ => temperature.ToString()
    };

    /// <summary>
    /// Maps the EtabSharp Units preset constants back to the underlying ETABSv1.eUnits enum.
    /// Only the presets actually used in the codebase are listed — extend as needed.
    /// </summary>
    private static eUnits UnitsPresetToEnum(object preset) => preset switch
    {
        var u when u == Units.US_Kip_Ft => eUnits.kip_ft_F,
        var u when u == Units.US_Kip_In => eUnits.kip_in_F,
        var u when u == Units.US_Lb_In => eUnits.lb_in_F,
        var u when u == Units.US_Lb_Ft => eUnits.lb_ft_F,
        var u when u == Units.SI_kN_m => eUnits.kN_m_C,
        var u when u == Units.SI_kN_mm => eUnits.kN_mm_C,
        var u when u == Units.SI_N_m => eUnits.N_m_C,
        var u when u == Units.SI_N_mm => eUnits.N_mm_C,
        var u when u == Units.SI_kgf_m => eUnits.kgf_m_C,
        var u when u == Units.SI_tonf_m => eUnits.Ton_m_C,
        _ => eUnits.kip_ft_F   // safe default
    };
}
