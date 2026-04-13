// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

public class EtabsBootstrapService : IEtabsBootstrapService
{
    public async Task<Result<EtabsContext>> BootstrapAsync(string filePath, string? unitsPreset = null)
    {
        // ── Pre-flight ────────────────────────────────────────────────────────
        if (!File.Exists(filePath))
            return Result.Fail<EtabsContext>($"File not found: {filePath}");

        // ── Resolve units (fail fast before starting ETABS) ───────────────────
        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(unitsPreset);
        if (unitsError is not null)
            return Result.Fail<EtabsContext>(unitsError);

        ETABSApplication? app = null;
        try
        {
            // ── Start ETABS ───────────────────────────────────────────────────
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
                return Result.Fail<EtabsContext>("Failed to start ETABS hidden instance.");

            app.Application.Hide();
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            // ── Open file ─────────────────────────────────────────────────────
            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
            {
                app.Application.ApplicationExit(false);
                app.Dispose();
                return Result.Fail<EtabsContext>($"OpenFile failed (ret={openRet})");
            }

            // ── Normalise units ───────────────────────────────────────────────
            var unitService = new EtabsUnitService(app);
            var unitSnapshot = await unitService.ReadAndNormaliseAsync(targetUnits);
            Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));

            return Result.Ok(new EtabsContext(app, unitSnapshot));
        }
        catch (Exception ex)
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
            return Result.Fail<EtabsContext>($"Fatal error during ETABS bootstrap: {ex.Message}");
        }
    }
}
