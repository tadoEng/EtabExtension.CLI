// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.OpenModel;

public class OpenModelService : IOpenModelService
{
    private static async Task<int?> WaitForPidAsync(bool newestFirst)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);

        while (true)
        {
            var instances = ETABSWrapper.GetAllRunningInstances();
            var pid = newestFirst
                ? instances.OrderByDescending(i => i.ProcessId).FirstOrDefault()?.ProcessId
                : instances.FirstOrDefault()?.ProcessId;

            if (pid is not null)
                return pid;

            if (DateTime.UtcNow >= deadline)
                return null;

            await Task.Delay(200);
        }
    }

    public async Task<Result<OpenModelData>> OpenModelAsync(
        string filePath, bool save, bool newInstance)
    {
        await Task.CompletedTask;

        if (!File.Exists(filePath))
            return Result.Fail<OpenModelData>($"File not found: {filePath}");

        if (!filePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<OpenModelData>("Only .edb files can be opened");

        return newInstance
            ? await OpenInNewInstanceAsync(filePath)
            : await OpenInRunningInstanceAsync(filePath, save);
    }

    // ── Mode A — open in the user's running ETABS ────────────────────────────

    private static async Task<Result<OpenModelData>> OpenInRunningInstanceAsync(
        string filePath, bool save)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
                return Result.Fail<OpenModelData>(
                    "ETABS is not running. Start ETABS first, or use --new-instance to launch one.");

            var currentPath = app.Model.ModelInfo.GetModelFilepath();
            var hasCurrentFile = !string.IsNullOrEmpty(currentPath);

            Console.Error.WriteLine(
                $"ℹ Currently open: {(hasCurrentFile ? Path.GetFileName(currentPath) : "(none)")}");

            if (hasCurrentFile && save)
            {
                Console.Error.WriteLine("ℹ Saving current file...");
                int saveRet = app.Model.Files.SaveFile(currentPath!);
                if (saveRet != 0)
                    Console.Error.WriteLine($"⚠ SaveFile returned {saveRet} — continuing");
                else
                    Console.Error.WriteLine("✓ Saved");
            }

            // OpenFile() closes the current model and opens the new one atomically.
            // No InitializeNewModel needed — OpenFile handles the transition cleanly.
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
                Pid = pid,
                OpenedInNewInstance = false
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

    // ── Mode B variant — spawn a new visible ETABS instance ──────────────────
    // startApplication=true so ETABS window appears (user-visible, not hidden).
    // We do NOT call ApplicationExit — user controls this instance going forward.

    private static async Task<Result<OpenModelData>> OpenInNewInstanceAsync(string filePath)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            Console.Error.WriteLine("ℹ Starting new ETABS instance...");
            app = ETABSWrapper.CreateNew(startApplication: true);
            if (app is null)
                return Result.Fail<OpenModelData>("Failed to start new ETABS instance.");

            // Do NOT hide — user asked for a visible new instance
            Console.Error.WriteLine($"✓ New ETABS instance started (v{app.FullVersion})");

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
                return Result.Fail<OpenModelData>($"OpenFile failed (ret={openRet})");

            var pid = await WaitForPidAsync(newestFirst: true);
            if (pid is null)
                return Result.Fail<OpenModelData>(
                    "ETABS opened the file, but the new process PID could not be confirmed within 3 seconds.");

            Console.Error.WriteLine($"✓ Opened in new instance (PID {pid}): {Path.GetFileName(filePath)}");

            return Result.Ok(new OpenModelData
            {
                FilePath = filePath,
                PreviousFilePath = null,
                Pid = pid,
                OpenedInNewInstance = true
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<OpenModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            // New instance: release COM proxy only.
            // User controls the visible ETABS window — we do NOT call ApplicationExit.
            app?.Dispose();
        }
    }
}
