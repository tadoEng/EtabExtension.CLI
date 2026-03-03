// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.Core;
using EtabSharp.System;
using EtabSharp.System.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;

/// <summary>
/// Manages the ETABS unit system within a command session.
///
/// PROBLEM THIS SOLVES:
/// Every Mode B command that extracts data needs to know what units the values are in.
/// If we let ETABS use whatever units the model was saved with, the extracted values
/// are unpredictable (a drift ratio might be 0.004 ft or 0.048 in depending on the file).
/// The solution is to normalise to a known unit system before extraction, then:
///   a) include the unit info in the result JSON so Rust knows the unit context, and
///   b) optionally restore the original units before saving the file.
///
/// USAGE PATTERN (in any Mode B service):
///
///   var unitService = new EtabsUnitService(app);
///   var snapshot    = await unitService.ReadAndNormaliseAsync(Units.US_Kip_Ft);
///   // ... do all extraction work here, all values are now in kip/ft ...
///   await unitService.RestoreAsync(snapshot);  // optional before SaveFile()
///
/// UNIT SNAPSHOTS:
/// ReadAndNormaliseAsync returns a UnitSnapshot containing both the original units
/// and the normalised units. Pass this to the result data so Rust has full context.
/// </summary>
public interface IEtabsUnitService
{
    /// <summary>
    /// Reads the current unit system, then sets ETABS to the target unit system.
    /// Returns a snapshot of both original and active units.
    /// </summary>
    Task<UnitSnapshot> ReadAndNormaliseAsync(object targetUnits);

    /// <summary>
    /// Restores the unit system to the state captured in the snapshot.
    /// Call before SaveFile() if you do not want the normalised units persisted into the .edb.
    /// </summary>
    Task RestoreAsync(UnitSnapshot snapshot);

    /// <summary>
    /// Reads the current unit system without changing it.
    /// </summary>
    Task<UnitInfo> ReadCurrentAsync();
}

/// <summary>
/// Snapshot of unit state at the moment ReadAndNormaliseAsync was called.
/// Stored in the result JSON so Rust knows the unit context of all extracted values.
/// </summary>
public record UnitSnapshot
{
    /// <summary>Units the model was using before normalisation.</summary>
    public required UnitInfo Original { get; init; }

    /// <summary>Units that were applied for extraction. All values in the result are in these units.</summary>
    public required UnitInfo Active { get; init; }

    /// <summary>True if the unit system was actually changed (Original != Active).</summary>
    public bool WasChanged { get; init; }
}

/// <summary>
/// Human-readable unit breakdown for a single point in time.
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

    /// <summary>True when units are a standard US preset.</summary>
    public bool IsUS { get; init; }

    /// <summary>True when units are a standard Metric preset.</summary>
    public bool IsMetric { get; init; }

    /// <summary>
    /// The raw ETABSv1.eUnits enum value — stored so RestoreAsync can
    /// set back exactly the original preset without guessing.
    /// </summary>
    public int RawUnitEnum { get; init; }
}
