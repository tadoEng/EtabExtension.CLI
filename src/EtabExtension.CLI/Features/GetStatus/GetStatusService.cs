// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.GetStatus;

public class GetStatusService : IGetStatusService
{
    public async Task<Result<GetStatusData>> GetStatusAsync()
    {
        await Task.CompletedTask;

        // OS-level check first — no COM needed
        if (!ETABSWrapper.IsRunning())
        {
            return Result.Ok(new GetStatusData { IsRunning = false });
        }

        var instances = ETABSWrapper.GetAllRunningInstances();
        var pid = instances.FirstOrDefault()?.ProcessId;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
            {
                // Process exists but COM attach failed — rare, treat as error
                return Result.Fail<GetStatusData>(
                    "ETABS is running but COM attach failed. Try restarting ETABS.");
            }

            Console.Error.WriteLine($"✓ Connected to ETABS v{app.FullVersion} (PID {pid})");

            var openFilePath = app.Model.ModelInfo.GetModelFilepath();
            var isModelOpen = !string.IsNullOrEmpty(openFilePath);
            var isLocked = app.Model.ModelInfo.IsLocked();
            var isAnalyzed = app.Model.Analyze.AreAllCasesFinished();

            return Result.Ok(new GetStatusData
            {
                IsRunning = true,
                Pid = pid,
                EtabsVersion = app.FullVersion,
                OpenFilePath = isModelOpen ? openFilePath : null,
                IsModelOpen = isModelOpen,
                IsLocked = isLocked,
                IsAnalyzed = isAnalyzed
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<GetStatusData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM RCW only — ETABS keeps running
        }
    }
}
