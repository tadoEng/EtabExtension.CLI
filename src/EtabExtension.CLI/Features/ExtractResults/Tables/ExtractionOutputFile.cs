// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

namespace EtabExtension.CLI.Features.ExtractResults.Tables;

/// <summary>
/// The two file operations behind CLI #33, in one place so that every writer of a
/// fixed-name extraction parquet applies them identically.
///
/// <para>Extraction reuses the caller's output directory, and readers resolve tables by
/// fixed filename. So any path the CLI is about to write must first lose whatever an
/// earlier run left there (<see cref="RemovePrevious"/>). A write that then fails must
/// not leave its own truncated bytes behind either (<see cref="RemovePartial"/>).
/// Neither operation ever touches anything but the one exact path it is given.</para>
/// </summary>
internal static class ExtractionOutputFile
{
    /// <summary>
    /// Removes an earlier run's file at <paramref name="path"/>. Returns <c>null</c> when
    /// the path is now clear (including when nothing was there), or a failure message
    /// naming the file when it could not be removed. The caller must then not attempt
    /// the table: the old bytes are still at a path readers treat as current.
    /// </summary>
    public static string? RemovePrevious(string path, string label)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            // No directory, so no earlier output either.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return
                $"A previous run's output for '{label}' could not be removed ({path}): {ex.Message}. " +
                "The table was not extracted, and that file must NOT be read as this run's result.";
        }
    }

    /// <summary>
    /// Removes whatever a failed write left at <paramref name="path"/>. Returns an empty
    /// string when the path is clean, or a sentence to append to the failure when it
    /// could not be cleaned, so a failure never claims a cleanliness it did not get.
    /// </summary>
    public static string RemovePartial(string path)
    {
        try
        {
            File.Delete(path);
            return string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $" A partially written file could not be removed ({path}): {ex.Message}. " +
                   "It must NOT be read as this run's result.";
        }
    }

    /// <summary>
    /// Resolves <c>&lt;outputDir&gt;\&lt;slug&gt;.parquet</c> and proves it is a plain file
    /// name DIRECTLY inside <paramref name="outputDir"/>, or explains why not.
    ///
    /// <para>Needed wherever the slug comes from caller input (extract-materials
    /// <c>--table-key</c>). This file is about to be DELETED, so a key such as
    /// <c>..\..\x</c> or <c>C:x</c> must be refused before anything is touched. Registry
    /// slugs are constants and always pass.</para>
    /// </summary>
    public static bool TryResolve(string outputDir, string slug, out string path, out string? error)
    {
        path = string.Empty;
        var fileName = $"{slug}.parquet";
        if (string.IsNullOrWhiteSpace(slug)
            || slug is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar)
            || fileName.Contains(Path.VolumeSeparatorChar))
        {
            error = $"Table slug '{slug}' is not a plain file name, so no output path inside the " +
                    "output directory can be derived from it. Nothing was written or removed.";
            return false;
        }

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
        var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Table slug '{slug}' resolves outside the output directory ({candidate}). " +
                    "Nothing was written or removed.";
            return false;
        }

        path = candidate;
        error = null;
        return true;
    }
}
