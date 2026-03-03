// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.UnlockModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.UnlockModel;

public class UnlockModelService : IUnlockModelService
{
    public async Task<Result<UnlockModelData>> UnlockModelAsync(string filePath)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
                return Result.Fail<UnlockModelData>("ETABS is not running.");

            var currentPath = app.Model.ModelInfo.GetModelFilepath();

            // Guard: file must already be open
            if (!PathsAreEqual(currentPath, filePath))
            {
                return Result.Fail<UnlockModelData>(
                    $"File not open in ETABS. Currently open: '{currentPath ?? "(none)"}'. " +
                    $"Open the file first with: etab-cli open-model --file \"{filePath}\"");
            }

            bool wasLocked = app.Model.ModelInfo.IsLocked();
            Console.Error.WriteLine($"ℹ Lock status: {(wasLocked ? "locked" : "not locked")}");

            if (wasLocked)
            {
                app.Model.ModelInfo.SetLocked(false);

                // Verify it cleared
                if (app.Model.ModelInfo.IsLocked())
                    return Result.Fail<UnlockModelData>("SetLocked(false) call succeeded but model is still locked.");

                Console.Error.WriteLine("✓ Lock cleared");
            }

            return Result.Ok(new UnlockModelData
            {
                FilePath = filePath,
                WasLocked = wasLocked
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<UnlockModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }

    private static bool PathsAreEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
    }
}
