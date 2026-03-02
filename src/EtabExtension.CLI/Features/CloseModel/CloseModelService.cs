// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.CloseModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.CloseModel;

public class CloseModelService : ICloseModelService
{
    public async Task<Result<CloseModelData>> CloseModelAsync(bool save)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
                return Result.Fail<CloseModelData>("ETABS is not running.");

            var currentPath = app.Model.ModelInfo.GetModelFilepath();
            var hasFile = !string.IsNullOrEmpty(currentPath);

            Console.Error.WriteLine($"ℹ Currently open: {(hasFile ? Path.GetFileName(currentPath) : "(none)")}");

            if (save && hasFile)
            {
                Console.Error.WriteLine("ℹ Saving...");
                int saveRet = app.Model.Files.SaveFile(currentPath!);
                if (saveRet != 0)
                    Console.Error.WriteLine($"⚠ Save returned {saveRet} — continuing");
                else
                    Console.Error.WriteLine("✓ Saved");
            }

            // InitializeNewModel() confirmed: clears workspace without triggering
            // Save dialog even on modified models. Rust decides save/no-save.
            int initRet = app.Model.ModelInfo.InitializeNewModel(eUnits.kip_ft_F);
            if (initRet != 0)
                return Result.Fail<CloseModelData>($"InitializeNewModel failed (ret={initRet})");

            Console.Error.WriteLine("✓ Workspace cleared");

            return Result.Ok(new CloseModelData
            {
                ClosedFilePath = hasFile ? currentPath : null,
                WasSaved = save && hasFile
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<CloseModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }
}
