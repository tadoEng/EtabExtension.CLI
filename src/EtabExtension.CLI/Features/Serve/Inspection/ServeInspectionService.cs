using System.Security.Cryptography;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using ETABSv1;

namespace EtabExtension.CLI.Features.Serve.Inspection;

public interface IServeInspectionService
{
    Result<GetModelStateData> GetModelState(
        IEtabsInspectionApi api,
        ManagedEtabsSessionRecord? identity);
    Result<ListWallPropertiesData> ListWallProperties(IEtabsInspectionApi api);
    Result<InspectWallPropertyData> InspectWallProperty(IEtabsInspectionApi api, string name);
    Result<ResolveAreaTargetsData> ResolveAreaTargets(IEtabsInspectionApi api, string sourceProperty);
}

public sealed class ServeInspectionService : IServeInspectionService
{
    private const eUnits ExecutionUnits = eUnits.kN_m_C;

    public Result<GetModelStateData> GetModelState(
        IEtabsInspectionApi api,
        ManagedEtabsSessionRecord? identity) => Execute(api, units =>
    {
        if (identity is null)
        {
            throw new InvalidOperationException(
                "Managed ETABS session record is unavailable; model identity cannot be established.");
        }

        var modelPath = api.GetModelFilename();
        var ret = api.GetCaseStatus(out var caseNames, out var statuses);
        RequireSuccess("cAnalyze.GetCaseStatus", ret);
        var finishedCaseCount = statuses.Count(status => status == 4);

        return new GetModelStateData(
            modelPath,
            units.Original,
            units.Original,
            units.Execution,
            api.GetModelIsLocked(),
            new AnalysisResultsStateData(
                finishedCaseCount > 0,
                Math.Max(caseNames.Length, statuses.Length),
                finishedCaseCount),
            FingerprintSavedFile(modelPath),
            new ManagedIdentityData(
                identity.SchemaVersion,
                identity.Pid,
                identity.ProcessStartTimeUtc,
                identity.ExecutablePath,
                identity.ManagedLaunchRecordId));
    });

    public Result<ListWallPropertiesData> ListWallProperties(IEtabsInspectionApi api) =>
        Execute(api, units =>
        {
            var ret = api.GetWallPropertyNames(out var names);
            RequireSuccess("cPropArea.GetNameList(PropType=1)", ret);
            return new ListWallPropertiesData(names, units.Original, units.Execution);
        });

    public Result<InspectWallPropertyData> InspectWallProperty(
        IEtabsInspectionApi api,
        string name) => Execute(api, units =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var ret = api.GetWall(name, out var wall);
        RequireSuccess("cPropArea.GetWall", ret);

        ret = api.GetModifiers(name, out var modifiers);
        RequireSuccess("cPropArea.GetModifiers", ret);
        if (modifiers.Length != 10)
        {
            throw new InvalidOperationException(
                $"cPropArea.GetModifiers returned {modifiers.Length} values; expected 10.");
        }

        ret = api.GetShellDesign(name, out var shellDesign);
        RequireSuccess("cPropArea.GetShellDesign", ret);

        return new InspectWallPropertyData(
            name,
            wall.WallPropType.ToString(),
            wall.ShellType.ToString(),
            wall.MaterialProperty,
            wall.Thickness,
            wall.Color,
            wall.Notes,
            wall.GlobalId,
            modifiers,
            new WallShellDesignData(
                shellDesign.MaterialProperty,
                shellDesign.SteelLayoutOption,
                shellDesign.DesignCoverTopDir1,
                shellDesign.DesignCoverTopDir2,
                shellDesign.DesignCoverBotDir1,
                shellDesign.DesignCoverBotDir2),
            units.Original,
            units.Execution);
    });

    public Result<ResolveAreaTargetsData> ResolveAreaTargets(
        IEtabsInspectionApi api,
        string sourceProperty) => Execute(api, units =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProperty);

        var ret = api.GetAreaNames(out var areaNames);
        RequireSuccess("cAreaObj.GetNameList", ret);
        var targets = new List<ResolvedAreaTargetData>();

        foreach (var name in areaNames)
        {
            ret = api.GetAreaProperty(name, out var propertyName);
            RequireSuccess($"cAreaObj.GetProperty('{name}')", ret);
            if (!string.Equals(propertyName, sourceProperty, StringComparison.Ordinal))
            {
                continue;
            }

            ret = api.GetAreaLabelAndStory(name, out _, out var story);
            RequireSuccess($"cAreaObj.GetLabelFromName('{name}')", ret);
            ret = api.GetAreaPier(name, out var pier);
            RequireSuccess($"cAreaObj.GetPier('{name}')", ret);
            ret = api.GetAreaGuid(name, out var globalId);
            RequireSuccess($"cAreaObj.GetGUID('{name}')", ret);
            ret = api.GetAreaDesignOrientation(name, out var orientation);
            RequireSuccess($"cAreaObj.GetDesignOrientation('{name}')", ret);

            targets.Add(new ResolvedAreaTargetData(
                name,
                story,
                pier,
                globalId,
                orientation.ToString()));
        }

        return new ResolveAreaTargetsData(
            sourceProperty,
            targets,
            units.Original,
            units.Execution);
    });

    private static Result<T> Execute<T>(
        IEtabsInspectionApi api,
        Func<UnitAudit, T> read)
    {
        try
        {
            return Result.Ok(WithPinnedUnits(api, read));
        }
        catch (Exception ex)
        {
            return Result.Fail<T>(ex.Message);
        }
    }

    private static T WithPinnedUnits<T>(IEtabsInspectionApi api, Func<UnitAudit, T> read)
    {
        var original = api.GetPresentUnits();
        if ((int)original == 0)
        {
            throw new InvalidOperationException("cSapModel.GetPresentUnits failed (returned 0).");
        }

        try
        {
            RequireSuccess(
                $"cSapModel.SetPresentUnits({ExecutionUnits})",
                api.SetPresentUnits(ExecutionUnits));
            return read(new UnitAudit(ToUnitData(original), ToUnitData(ExecutionUnits)));
        }
        finally
        {
            RequireSuccess(
                $"cSapModel.SetPresentUnits({original}) restore",
                api.SetPresentUnits(original));
        }
    }

    private static InspectionUnitData ToUnitData(eUnits units) => new(units.ToString(), (int)units);

    private static void RequireSuccess(string member, int returnCode)
    {
        if (returnCode != 0)
        {
            throw new InvalidOperationException($"{member} failed (ret={returnCode}).");
        }
    }

    private static string? FingerprintSavedFile(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return null;
        }

        using var stream = File.OpenRead(modelPath);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}";
    }

    private sealed record UnitAudit(InspectionUnitData Original, InspectionUnitData Execution);
}
