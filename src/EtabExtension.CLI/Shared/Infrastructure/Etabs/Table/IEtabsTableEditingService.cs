// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table.Models;
using EtabSharp.DatabaseTables.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;

/// <summary>
/// Edits ETABS database tables in a structured, auditable way.
///
/// All write operations follow the same three-step pattern:
///   1. GetTableForEditing   — read current values + table version
///   2. Modify via callback  — caller supplies the business logic
///   3. SetTableForEditing + ApplyEditedTables — commit to model
///
/// The service never calls SaveFile — that is the caller's responsibility
/// so it can be combined with other edits before a single save.
///
/// IMPORTANT: Save your model before calling any Apply method (ETABS requirement).
///
/// ─────────────────────────────────────────────────────────────────────────────
/// PRIMARY USE CASE — RSA scale factor normalisation
/// ─────────────────────────────────────────────────────────────────────────────
/// In seismic design, ASCE 7 requires the base shear from a Response Spectrum
/// Analysis (RSA) to be at least 85–100 % of the Equivalent Lateral Force (ELF)
/// base shear.  When RSA < ELF the engineer scales up the RSA by editing the
/// TransAccSF / LongAccSF field in the "Load Case Definitions - Response Spectrum"
/// table.
///
/// Example (scale DBE_X so RSA matches 100 % ELF):
///
///   var editService = new EtabsTableEditingService(app, logger);
///
///   // Option A — simple single-field update
///   var result = await editService.SetFieldValueAsync(
///       tableKey   : "Load Case Definitions - Response Spectrum",
///       keyField   : "Name",
///       keyValue   : "DBE_X",
///       targetField: "TransAccSF",
///       newValue   : "1.23");
///
///   // Option B — scale the existing factor by a multiplier
///   var result = await editService.ScaleFieldValueAsync(
///       tableKey   : "Load Case Definitions - Response Spectrum",
///       keyField   : "Name",
///       keyValue   : "DBE_X",
///       targetField: "TransAccSF",
///       scaleFactor: 1.23);
///
///   // Option C — bulk update multiple cases at once
///   var updates = new Dictionary&lt;string, string&gt;
///   {
///       { "DBE_X", "1.23" },
///       { "DBE_Y", "1.18" },
///   };
///   var result = await editService.SetFieldValuesForMultipleRowsAsync(
///       tableKey   : "Load Case Definitions - Response Spectrum",
///       keyField   : "Name",
///       targetField: "TransAccSF",
///       updates    : updates);
/// </summary>
public interface IEtabsTableEditingService
{
    // ── Generic table editing ────────────────────────────────────────────────

    /// <summary>
    /// Full-control table edit: fetches the table, passes it to the caller's
    /// callback for arbitrary modifications, then applies.
    ///
    /// Use this when none of the convenience overloads fit your need.
    /// </summary>
    /// <param name="tableKey">ETABS database table key.</param>
    /// <param name="modifyCallback">
    /// Receives the current TableDataArrayResult and must return a (possibly
    /// mutated) TableDataArrayResult.  Call table.GetStructuredData() /
    /// table.SetStructuredData() for row-level access.
    /// </param>
    /// <param name="groupName">Optional ETABS group to scope the get.</param>
    Task<TableEditResult> EditAsync(
        string tableKey,
        Func<TableDataArrayResult, TableDataArrayResult> modifyCallback,
        string? groupName = null);

    // ── Convenience: single-row / single-field ───────────────────────────────

    /// <summary>
    /// Sets a single field value on the first row where keyField == keyValue.
    ///
    /// Common use: update TransAccSF for one RSA case name.
    /// </summary>
    Task<TableEditResult> SetFieldValueAsync(
        string tableKey,
        string keyField,
        string keyValue,
        string targetField,
        string newValue,
        string? groupName = null);

    /// <summary>
    /// Multiplies the numeric value of targetField on the first row where
    /// keyField == keyValue by scaleFactor.
    ///
    /// Common use: scale TransAccSF by the ELF/RSA ratio.
    /// e.g. if current ScaleFactor = 1.0 and ratio = 1.23 → writes "1.23"
    /// e.g. if current ScaleFactor = 0.9 and ratio = 1.23 → writes "1.107"
    /// </summary>
    Task<TableEditResult> ScaleFieldValueAsync(
        string tableKey,
        string keyField,
        string keyValue,
        string targetField,
        double scaleFactor,
        string? groupName = null);

    // ── Convenience: multi-row updates ──────────────────────────────────────

    /// <summary>
    /// Sets the same targetField to different values for multiple rows, matched
    /// by keyField.  All changes are batched into a single Apply call.
    ///
    /// Common use: update TransAccSF for DBE_X, DBE_Y, MCE_X, MCE_Y in one shot.
    /// </summary>
    /// <param name="updates">
    /// Dictionary of keyValue → newValue.
    /// e.g. { "DBE_X" → "1.23", "DBE_Y" → "1.18" }
    /// </param>
    Task<TableEditResult> SetFieldValuesForMultipleRowsAsync(
        string tableKey,
        string keyField,
        string targetField,
        Dictionary<string, string> updates,
        string? groupName = null);

    /// <summary>
    /// Scales the targetField by individual scale factors for multiple rows.
    ///
    /// Common use: apply per-direction ELF/RSA ratios to all RSA cases at once.
    /// </summary>
    /// <param name="scaleFactors">
    /// Dictionary of keyValue → multiplier.
    /// e.g. { "DBE_X" → 1.23, "DBE_Y" → 1.18 }
    /// </param>
    Task<TableEditResult> ScaleFieldValuesForMultipleRowsAsync(
        string tableKey,
        string keyField,
        string targetField,
        Dictionary<string, double> scaleFactors,
        string? groupName = null);
}
