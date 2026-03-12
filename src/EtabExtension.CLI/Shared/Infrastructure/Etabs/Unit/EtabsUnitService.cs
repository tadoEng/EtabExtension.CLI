// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.Core;
using EtabSharp.System.Models;
using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;

/// <summary>
/// Manages the ETABS unit system within a Mode B command session.
///
/// Normalises to a caller-supplied unit preset before extraction so all
/// extracted values are in a known, consistent unit system. Records the
/// original units so they can be restored before SaveFile() if needed.
///
/// USAGE PATTERN:
///   var unitService  = new EtabsUnitService(app);
///   var unitSnapshot = await unitService.ReadAndNormaliseAsync(EtabsUnitPreset.Resolve("US_Kip_Ft").Units);
///   Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));
///   // ... extract ...
///   // optionally: await unitService.RestoreAsync(unitSnapshot);
/// </summary>
public class EtabsUnitService : IEtabsUnitService
{
    private readonly ETABSApplication _app;

    public EtabsUnitService(ETABSApplication app)
    {
        _app = app;
    }

    /// <inheritdoc />
    public async Task<UnitSnapshot> ReadAndNormaliseAsync(Units targetUnits)
    {
        await Task.CompletedTask;

        var original = ReadCurrent();

        // Check whether units are already set to avoid a redundant COM call
        var alreadySet = original.RawForce == (int)targetUnits.Force
                      && original.RawLength == (int)targetUnits.Length
                      && original.RawTemperature == (int)targetUnits.Temperature;

        if (!alreadySet)
            _app.Model.Units.SetPresentUnits(targetUnits);

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

        if (!snapshot.WasChanged) return;

        _app.Model.Units.SetPresentUnits(new Units
        {
            Force = (eForce)snapshot.Original.RawForce,
            Length = (eLength)snapshot.Original.RawLength,
            Temperature = (eTemperature)snapshot.Original.RawTemperature
        });
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

        return new UnitInfo
        {
            Force = ToForceSymbol(units.Force),
            Length = ToLengthSymbol(units.Length),
            Temperature = ToTemperatureSymbol(units.Temperature),
            IsUS = units.IsUS,
            IsMetric = units.IsMetric,
            // Store raw enum ints so RestoreAsync can round-trip back exactly
            RawForce = (int)units.Force,
            RawLength = (int)units.Length,
            RawTemperature = (int)units.Temperature,
        };
    }

    /// <summary>
    /// Formats a UnitSnapshot as a single stderr progress line.
    /// e.g. "ℹ Units normalised: kN/m/C → kip/ft/F (isUS=True)"
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

    // ── Symbol helpers ────────────────────────────────────────────────────────

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
}
