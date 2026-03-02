// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.OpenModel;

public class OpenModelService : IOpenModelService
{
    public async Task<Result<OpenModelData>> OpenModelAsync(string filePath, bool save)
    {
        await Task.CompletedTask;

        if (!File.Exists(filePath))
            return Result.Fail<OpenModelData>($"File not found: {filePath}");

        if (!filePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<OpenModelData>("Only .edb files can be opened");

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
                return Result.Fail<OpenModelData>("ETABS is not running. Start ETABS first.");

            var currentPath = app.Model.ModelInfo.GetModelFilepath();
            var hasCurrentFile = !string.IsNullOrEmpty(currentPath);

            Console.Error.WriteLine($"ℹ Currently open: {(hasCurrentFile ? Path.GetFileName(currentPath) : "(none)")}");

            if (hasCurrentFile)
            {
                if (save)
                {
                    Console.Error.WriteLine("ℹ Saving current file...");
                    int saveRet = app.Model.Files.SaveFile(currentPath!);
                    if (saveRet != 0)
                        Console.Error.WriteLine($"⚠ Save returned {saveRet} — continuing");
                }
                // --no-save: OpenFile() will close without prompt because
                // InitializeNewModel is not needed here — OpenFile handles it atomically
            }

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
                return Result.Fail<OpenModelData>($"OpenFile failed (ret={openRet})");

            var pid = ETABSWrapper.GetAllRunningInstances().FirstOrDefault()?.ProcessId;
            Console.Error.WriteLine($"✓ Opened: {Path.GetFileName(filePath)}");

            return Result.Ok(new OpenModelData
            {
                FilePath = filePath,
                PreviousFilePath = hasCurrentFile ? currentPath : null,
                Pid = pid
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<OpenModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }
}
