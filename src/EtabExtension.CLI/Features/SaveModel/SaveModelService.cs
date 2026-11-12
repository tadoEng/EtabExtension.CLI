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

    /// <summary>
    /// The save did not change the file on disk, so it cannot be reported as a save (card
    /// v2.3 §2): the stamp would describe a file this call did not write.
    /// </summary>
    public const string SaveNotObservedCode = "ETABS_SAVE_NOT_OBSERVED";

    /// <summary>
    /// The requested file is not the document the caller expected (<c>expectedDocument</c>),
    /// so nothing was saved.
    /// </summary>
    public const string DocumentMismatchCode = "ETABS_DOCUMENT_MISMATCH";

    private readonly IEtabsChangeSignalSource _changeSignal;
    private readonly ISavedFileStampReader _stamps;

    public SaveModelService(
        IEtabsChangeSignalSource? changeSignal = null,
        ISavedFileStampReader? stamps = null)
    {
        _changeSignal = changeSignal ?? new UnsupportedChangeSignalSource();
        _stamps = stamps ?? new DiskSavedFileStampReader();
    }

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

            return SaveOnApp(app, filePath, null);
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

    public Task<Result<SaveModelData>> SaveModelOnAppAsync(ETABSApplication app, string filePath) =>
        SaveModelOnAppAsync(app, filePath, expectedFileId: null);

    public Task<Result<SaveModelData>> SaveModelOnAppAsync(
        ETABSApplication app,
        string filePath,
        string? expectedFileId)
    {
        try { return Task.FromResult(SaveOnApp(app, filePath, expectedFileId)); }
        catch (Exception ex) { return Task.FromResult(Result.Fail<SaveModelData>($"ETABS COM error: {ex.Message}")); }
    }

    private Result<SaveModelData> SaveOnApp(ETABSApplication app, string filePath, string? expectedFileId)
    {
        ArgumentNullException.ThrowIfNull(app);

        var validation = EtabsModelOpen.ValidateModelPath(filePath);
        return validation is not null
            ? Result.Fail<SaveModelData>(validation)
            : SaveOnApi(
                new EtabsModelFileApi(app, _changeSignal),
                filePath,
                compareIdentity: null,
                _stamps,
                expectedFileId);
    }

    /// <summary>
    /// The save sequence over the named CSI seam, provable without ETABS.
    ///
    /// <para><b>The stamp.</b> The change signal is read in the call RIGHT AFTER
    /// <c>SaveFile</c> returned 0 (card v2.4 §1): COM calls run on the ETABS UI thread, so no
    /// user edit is processed between the two, and a flag that reads dirty there means the
    /// baseline cannot be trusted. <c>verified</c> is true only for a clean read of a qualifying
    /// signal; a dirty, unsupported or incomplete signal leaves it false.</para>
    /// </summary>
    internal static Result<SaveModelData> SaveOnApi(
        IEtabsModelFileApi api,
        string filePath,
        Func<string, string, FileIdentityResult>? compareIdentity = null,
        ISavedFileStampReader? stamps = null,
        string? expectedFileId = null)
    {
        stamps ??= new DiskSavedFileStampReader();
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

            var fileBefore = stamps.Read(requested);
            if (expectedFileId is not null
                && !string.Equals(fileBefore?.FileId, expectedFileId, StringComparison.Ordinal))
            {
                return Result.Fail<SaveModelData>(EtabsApiDiagnosticFormatter.Bounded(
                    $"[{DocumentMismatchCode}] The requested file is not the expected document " +
                    $"(expected fileId '{expectedFileId}', found '{fileBefore?.FileId ?? "none"}'); nothing was saved."));
            }

            Console.Error.WriteLine($"ℹ Saving to: {requested}");
            activeOperation = SaveOperation;
            var saveReturnCode = api.SaveFile(requested);
            if (saveReturnCode != 0)
            {
                return Result.Fail<SaveModelData>(
                    EtabsApiDiagnosticFormatter.ApiReturn(activeOperation, saveReturnCode));
            }

            // IMMEDIATELY after SaveFile returned 0, in this same call: the flag is read
            // before anything else runs (card v2.4 §1).
            var signal = ReadSignal(api);
            var fileAfter = stamps.Read(requested);
            if (fileAfter is null
                || (fileBefore is not null
                    && fileBefore.Size == fileAfter.Size
                    && fileBefore.MtimeUtc == fileAfter.MtimeUtc))
            {
                return Result.Fail<SaveModelData>(EtabsApiDiagnosticFormatter.Bounded(
                    $"[{SaveNotObservedCode}] cFile.Save returned 0 but the file on disk did not change, " +
                    "so the save is not reported as done."));
            }

            activeOperation = EtabsModelOpen.ReadCurrentPathOperation;
            var after = api.GetModelFilename();
            Console.Error.WriteLine($"✓ Saved: {Path.GetFileName(requested)}");
            return Result.Ok(new SaveModelData
            {
                FilePath = requested,
                ReportedBefore = Blank(before),
                ReportedAfter = Blank(after),
                SavedStamp = new SavedStamp(
                    fileAfter.FileId,
                    fileAfter.Size,
                    fileAfter.MtimeUtc,
                    ChangeSignalName(signal.State),
                    signal.IsVerifiedClean),
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

    internal static bool IsLockSiblingOf(string currentFull, string requestedFullPath) =>
        string.Equals(Path.GetExtension(currentFull), ".$et", StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            Path.ChangeExtension(currentFull, null),
            Path.ChangeExtension(requestedFullPath, null),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A signal that throws is not "clean": it is an incomplete signal, so the stamp is
    /// unverified. Nothing about a failed read may be spent as an assurance.
    /// </summary>
    private static ChangeSignalReading ReadSignal(IEtabsModelFileApi api)
    {
        try
        {
            return api.ReadChangeSignal();
        }
#pragma warning disable CA1031 // an unreadable signal is an unverified stamp, never a failed save
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine(
                $"⚠ The change signal could not be read: {EtabsApiDiagnosticFormatter.Bounded(exception.Message)}");
            return new ChangeSignalReading(ChangeSignalState.Incomplete, "read failed");
        }
    }

    internal static string ChangeSignalName(ChangeSignalState state) => state switch
    {
        ChangeSignalState.Clean => "clean",
        ChangeSignalState.Dirty => "dirty",
        ChangeSignalState.Incomplete => "incomplete",
        _ => "unsupported"
    };

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
