// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// Encapsulates a started ETABS instance and its unit state.
/// Ensures ETABS is closed correctly when disposed.
/// </summary>
public record EtabsContext(ETABSApplication App, UnitSnapshot? Units) : IDisposable
{
    public void Dispose()
    {
        App.Application.ApplicationExit(false);
        App.Dispose();
    }
}

/// <summary>
/// Common logic for starting a hidden ETABS instance (Mode B), opening a model,
/// and normalising units.
///
/// FLOW:
///   1. Pre-flight: Check file exists and resolve unit preset.
///   2. Start hidden ETABS instance.
///   3. Open .edb file.
///   4. Normalise units (e.g. to US_Kip_Ft) so extraction is consistent.
/// </summary>
public interface IEtabsBootstrapService
{
    /// <summary>
    /// Starts ETABS, opens the file, and normalises units.
    /// </summary>
    /// <param name="filePath">Path to the .edb file.</param>
    /// <param name="unitsPreset">Optional unit preset name (e.g. "US_Kip_Ft", "SI_kN_m").</param>
    /// <returns>A Result containing the EtabsContext on success.</returns>
    Task<Result<EtabsContext>> BootstrapAsync(string filePath, string? unitsPreset = null);
}
