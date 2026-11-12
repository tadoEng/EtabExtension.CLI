// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.SaveModel.Models;

/// <summary>
/// What the daemon observed in the SAME call as the save (card v2.3 §2, v2.4 §1): the saved
/// file's identity and stamp, captured right after <c>SaveFile</c> returned 0, and ETABS's own
/// "unsaved changes" state read in that call. <c>shutdown-if-unchanged</c> compares against it.
/// <c>ChangeSignal</c> is <c>clean | dirty | unsupported | incomplete</c>. <c>Verified</c> is
/// true only when a qualifying, complete signal read CLEAN right after the save; otherwise the
/// explicit discard fallback applies and no "unchanged" assurance is ever given.
/// </summary>
public sealed record SavedStamp(
    [property: JsonPropertyName("fileId")] string? FileId,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtime")] DateTimeOffset Mtime,
    [property: JsonPropertyName("changeSignal")] string ChangeSignal,
    [property: JsonPropertyName("verified")] bool Verified);

public record SaveModelData
{
    /// <summary>The full path the model was saved to: exactly the requested file.</summary>
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    /// <summary>What ETABS reported as its current model before the save.</summary>
    [JsonPropertyName("reportedBefore")]
    public string? ReportedBefore { get; init; }

    /// <summary>What ETABS reported as its current model after the save.</summary>
    [JsonPropertyName("reportedAfter")]
    public string? ReportedAfter { get; init; }

    /// <summary>Observed in the same call as the save.</summary>
    [JsonPropertyName("savedStamp")]
    public SavedStamp? SavedStamp { get; init; }
}
