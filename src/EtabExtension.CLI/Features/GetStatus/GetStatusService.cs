using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.GetStatus;

public class GetStatusService : IGetStatusService
{
    public async Task<Result<GetStatusData>> GetStatusAsync()
    {
        await Task.CompletedTask;

        if (!ETABSWrapper.IsRunning())
            return Result.Ok(new GetStatusData { IsRunning = false });

        var instances = ETABSWrapper.GetAllRunningInstances();
        var pid = instances.FirstOrDefault()?.ProcessId;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
                return Result.Fail<GetStatusData>(
                    "ETABS is running but COM attach failed. Try restarting ETABS.");

            Console.Error.WriteLine($"✓ Connected to ETABS v{app.FullVersion} (PID {pid})");

            var openFilePath = app.Model.ModelInfo.GetModelFilepath();
            var isModelOpen = !string.IsNullOrEmpty(openFilePath);
            var isLocked = app.Model.ModelInfo.IsLocked();
            var isAnalyzed = app.Model.Analyze.AreAllCasesFinished();

            // Read unit system — mirrors demo script PrintUnitSummary
            UnitSystemInfo? unitSystem = null;
            try
            {
                var units = app.Model.Units.GetPresentUnits();
                var force = ToForceSymbol(units.Force);
                var length = ToLengthSymbol(units.Length);
                var temp = ToTemperatureSymbol(units.Temperature);

                unitSystem = new UnitSystemInfo
                {
                    Force = force,
                    Length = length,
                    Temperature = temp,
                    IsUs = units.IsUS,
                    IsMetric = units.IsMetric
                };

                Console.Error.WriteLine(
                    $"ℹ Units: {force}/{length}/{temp}  isUS={units.IsUS}  isMetric={units.IsMetric}");
            }
            catch (Exception ex)
            {
                // Not fatal — unit read failing should not block status
                Console.Error.WriteLine($"⚠ Could not read units: {ex.Message}");
            }

            return Result.Ok(new GetStatusData
            {
                IsRunning = true,
                Pid = pid,
                EtabsVersion = app.FullVersion,
                OpenFilePath = isModelOpen ? openFilePath : null,
                IsModelOpen = isModelOpen,
                IsLocked = isLocked,
                IsAnalyzed = isAnalyzed,
                UnitSystem = unitSystem
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<GetStatusData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }

    // ── Unit helpers — copied verbatim from demo script ───────────────────────

    private static string ToForceSymbol(eForce force) => force switch
    {
        eForce.lb => "lb",
        eForce.kip => "kip",
        eForce.N => "N",
        eForce.kN => "kN",
        eForce.kgf => "kgf",
        eForce.tonf => "tonf",
        _ => force.ToString()
    };

    private static string ToLengthSymbol(eLength length) => length switch
    {
        eLength.inch => "in",
        eLength.ft => "ft",
        eLength.mm => "mm",
        eLength.cm => "cm",
        eLength.m => "m",
        _ => length.ToString()
    };

    private static string ToTemperatureSymbol(eTemperature temperature) => temperature switch
    {
        eTemperature.F => "F",
        eTemperature.C => "C",
        _ => temperature.ToString()
    };
}
