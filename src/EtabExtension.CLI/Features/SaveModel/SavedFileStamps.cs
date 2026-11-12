// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs;

namespace EtabExtension.CLI.Features.SaveModel;

/// <summary>A saved file's identity, size and last-write time, as observed on disk.</summary>
public sealed record SavedFileStamp(string? FileId, long Size, DateTimeOffset MtimeUtc);

/// <summary>
/// The file-system boundary of the save stamp, so the save sequence and
/// <c>shutdown-if-unchanged</c> are provable without a disk.
/// </summary>
public interface ISavedFileStampReader
{
    /// <summary>The stamp of <paramref name="path"/>, or null when the file does not exist.</summary>
    SavedFileStamp? Read(string path);
}

/// <summary>Reads the stamp from the real file system.</summary>
public sealed class DiskSavedFileStampReader : ISavedFileStampReader
{
    public SavedFileStamp? Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        return new SavedFileStamp(
            WindowsFileIdentity.ReadFileId(path),
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }
}
