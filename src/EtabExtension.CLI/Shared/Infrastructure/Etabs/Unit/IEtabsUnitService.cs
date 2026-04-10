// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.System.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;

/// <summary>
/// Manages the ETABS unit system within a command session.
/// See <see cref="EtabsUnitService"/> for the implementation and usage pattern.
/// </summary>
public interface IEtabsUnitService
{
    /// <summary>
    /// Reads the current unit system, then sets ETABS to <paramref name="targetUnits"/>.
    /// Returns a snapshot of both original and active units.
    /// </summary>
    Task<UnitSnapshot> ReadAndNormaliseAsync(Units targetUnits);

    /// <summary>
    /// Restores the unit system to the state captured in the snapshot.
    /// Call before SaveFile() if you do not want the normalised units persisted.
    /// No-op when snapshot.WasChanged is false.
    /// </summary>
    Task RestoreAsync(UnitSnapshot snapshot);

    /// <summary>
    /// Reads the current unit system without changing it.
    /// </summary>
    Task<UnitInfo> ReadCurrentAsync();
}

/// <summary>
/// Snapshot of unit state at the moment ReadAndNormaliseAsync was called.
/// </summary>
public record UnitSnapshot
{
    /// <summary>Units the model was using before normalisation.</summary>
    public required UnitInfo Original { get; init; }

    /// <summary>Units applied for extraction. All values in the result are in these units.</summary>
    public required UnitInfo Active { get; init; }

    /// <summary>True if the unit system was actually changed.</summary>
    public bool WasChanged { get; init; }
}

/// <summary>
/// Human-readable unit breakdown at a single point in time.
/// Serialised into result JSON for every command that touches unit-bearing data.
/// </summary>
public record UnitInfo
{
    /// <summary>e.g. "kip", "kN", "N", "lb", "kgf", "tonf"</summary>
    public required string Force { get; init; }

    /// <summary>e.g. "ft", "in", "m", "cm", "mm"</summary>
    public required string Length { get; init; }

    /// <summary>"F" or "C"</summary>
    public required string Temperature { get; init; }

    public bool IsUs { get; init; }
    public bool IsMetric { get; init; }

    /// <summary>
    /// Raw ETABSv1.eForce enum value — stored so RestoreAsync can round-trip
    /// back to the exact original preset without guessing.
    /// </summary>
    public int RawForce { get; init; }

    /// <summary>Raw ETABSv1.eLength enum value.</summary>
    public int RawLength { get; init; }

    /// <summary>Raw ETABSv1.eTemperature enum value.</summary>
    public int RawTemperature { get; init; }
}
