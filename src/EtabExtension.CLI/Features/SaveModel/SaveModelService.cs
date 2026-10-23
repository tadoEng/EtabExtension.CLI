// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.SaveModel;

/// <summary>
/// <c>save-model</c> — saves the open model to an EXPLICIT path (EtabExtension #443).
///
/// <para>Why explicit. Cardex <c>cFile.Save</c>: with no file name the model "is saved
/// using its current name", and with one it is saved to that path. ETABS's current name
/// is not a reliable model path: on ETABS 23.3 it read <c>vN\model.e2k</c> after
/// <c>cFile.ExportFile</c> and <c>vN\model.$et</c> after a UI Ctrl+S. Saving under the
/// current name is how a committed version got overwritten, so this command never does:
/// the caller names the file, and the save goes to exactly that file.</para>
///
/// <para>Why it must be the open model. Saving one model's content over a different
/// <c>.edb</c> would be a silent overwrite of its own. The requested file must be the
/// model ETABS has open, by path or by file identity. The <c>.$et</c> sibling ETABS
/// reports after a UI save of that same model counts as the model.</para>
/// </summary>
public class SaveModelService : ISaveModelService
{
    public const string SaveOperation = "cFile.Save";

    public async Task<Result<SaveModelData>> SaveModelAsync(string filePath)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
            {
                return Result.Fail<SaveModelData>("ETABS is not running.");
            }

            return SaveOnApp(app, filePath);
        }
        catch (Exception ex)
        {
            return Result.Fail<SaveModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }

    public Task<Result<SaveModelData>> SaveModelOnAppAsync(ETABSApplication app, string filePath)
    {
        try { return Task.FromResult(SaveOnApp(app, filePath)); }
        catch (Exception ex) { return Task.FromResult(Result.Fail<SaveModelData>($"ETABS COM error: {ex.Message}")); }
    }

    private static Result<SaveModelData> SaveOnApp(ETABSApplication app, string filePath)
    {
        ArgumentNullException.ThrowIfNull(app);

        var validation = EtabsModelOpen.ValidateModelPath(filePath);
        return validation is not null
            ? Result.Fail<SaveModelData>(validation)
            : SaveOnApi(new EtabsModelFileApi(app), filePath);
    }

    /// <summary>The save sequence over the named CSI seam, provable without ETABS.</summary>
    internal static Result<SaveModelData> SaveOnApi(
        IEtabsModelFileApi api,
        string filePath,
        Func<string, string, FileIdentityResult>? compareIdentity = null)
    {
        string requested;
        try
        {
            requested = Path.GetFullPath(filePath);
        }
        catch (Exception exception)
        {
            return Result.Fail<SaveModelData>(EtabsApiDiagnosticFormatter.Bounded(
                $"Model file path could not be resolved to a full path: {exception.Message}"));
        }

        var activeOperation = EtabsModelOpen.ReadCurrentPathOperation;
        try
        {
            var before = api.GetModelFilename();
            var refusal = ValidateOpenModelIs(
                before,
                requested,
                compareIdentity ?? WindowsFileIdentity.Compare);
            if (refusal is not null)
            {
                return Result.Fail<SaveModelData>(refusal);
            }

            Console.Error.WriteLine($"ℹ Saving to: {requested}");
            activeOperation = SaveOperation;
            var saveReturnCode = api.SaveFile(requested);
            if (saveReturnCode != 0)
            {
                return Result.Fail<SaveModelData>(
                    EtabsApiDiagnosticFormatter.ApiReturn(activeOperation, saveReturnCode));
            }

            activeOperation = EtabsModelOpen.ReadCurrentPathOperation;
            var after = api.GetModelFilename();
            Console.Error.WriteLine($"✓ Saved: {Path.GetFileName(requested)}");
            return Result.Ok(new SaveModelData
            {
                FilePath = requested,
                ReportedBefore = Blank(before),
                ReportedAfter = Blank(after),
            });
        }
        catch (Exception exception)
        {
            return Result.Fail<SaveModelData>(
                EtabsApiDiagnosticFormatter.Exception(activeOperation, exception));
        }
    }

    /// <summary>
    /// Null when ETABS's current model is the requested file, otherwise the refusal.
    ///
    /// <para>Accepted: the same full path (case-insensitive), a different spelling of the
    /// same file by identity, or the <c>.$et</c> sibling of the requested file (same
    /// folder, same name). Nothing else — in particular not a blank answer, a folder, or
    /// an <c>.e2k</c> that an export left as the current name.</para>
    /// </summary>
    internal static string? ValidateOpenModelIs(
        string? reportedPath,
        string requestedFullPath,
        Func<string, string, FileIdentityResult> compareIdentity)
    {
        var current = EtabsCurrentModelPath.ResolveOpenFile(reportedPath);
        if (current is not null)
        {
            var currentFull = Path.GetFullPath(current);
            if (string.Equals(currentFull, requestedFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (IsLockSiblingOf(currentFull, requestedFullPath))
            {
                return null;
            }

            if (File.Exists(currentFull)
                && compareIdentity(requestedFullPath, currentFull).Match == FileIdentityMatch.Same)
            {
                return null;
            }
        }

        return EtabsApiDiagnosticFormatter.Bounded(
            $"Refusing to save: ETABS has '{EtabsCurrentModelPath.Describe(reportedPath)}' open, " +
            $"not '{requestedFullPath}'. Saving would write one model over another.");
    }

    private static bool IsLockSiblingOf(string currentFull, string requestedFullPath) =>
        string.Equals(Path.GetExtension(currentFull), ".$et", StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            Path.ChangeExtension(currentFull, null),
            Path.ChangeExtension(requestedFullPath, null),
            StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
