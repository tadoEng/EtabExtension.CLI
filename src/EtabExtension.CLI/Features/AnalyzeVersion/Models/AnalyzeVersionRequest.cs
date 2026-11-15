using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.AnalyzeVersion.Models;

/// <summary>The document the host expects ETABS to hold open (same shape as save-model's).</summary>
public sealed record AnalyzeVersionExpectedDocument
{
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("fileId")] public string? FileId { get; init; }
}

/// <summary>The version text's units, sent by the host as the cross-check (card M2, M3).</summary>
public sealed record AnalyzeVersionExpectedUnits
{
    [JsonPropertyName("force")] public string? Force { get; init; }
    [JsonPropertyName("length")] public string? Length { get; init; }
    [JsonPropertyName("temperature")] public string? Temperature { get; init; }
}

/// <summary>
/// The <c>analyze-version</c> request (analyze-version card M3): ten top-level members. There are
/// NO defaults: a missing units block, policy or document is refused before any COM call.
/// </summary>
public sealed record AnalyzeVersionRequest
{
    public const string PolicyCheckOnly = "checkOnly";
    public const string PolicySetAndRestore = "setAndRestore";

    [JsonPropertyName("filePath")] public string? FilePath { get; init; }
    [JsonPropertyName("outputDir")] public string? OutputDir { get; init; }
    [JsonPropertyName("expectedDocument")] public AnalyzeVersionExpectedDocument? ExpectedDocument { get; init; }
    [JsonPropertyName("expectedUnits")] public AnalyzeVersionExpectedUnits? ExpectedUnits { get; init; }
    [JsonPropertyName("unitPolicy")] public string? UnitPolicy { get; init; }
    [JsonPropertyName("cases")] public List<string>? Cases { get; init; }
    [JsonPropertyName("tables")] public TableSelections? Tables { get; init; }
    [JsonPropertyName("extractionProfile")] public string? ExtractionProfile { get; init; }
    [JsonPropertyName("metadataOutputPath")] public string? MetadataOutputPath { get; init; }
    [JsonPropertyName("metricsOutputPath")] public string? MetricsOutputPath { get; init; }
}

/// <summary>A request that passed validation: nothing in it is a default.</summary>
public sealed record ValidAnalyzeVersionRequest(
    AnalyzeVersionRequest Request,
    string FilePath,
    string OutputDir,
    string ExpectedPath,
    string ExpectedFileId,
    UnitBlock ExpectedUnits,
    bool SetAndRestore);

public static class AnalyzeVersionRequestValidator
{
    /// <summary>
    /// Validates a request without touching ETABS or the disk. Returns null and a plain
    /// reason on refusal (not one of the typed tokens: the request never reached the daemon's
    /// body).
    /// </summary>
    public static ValidAnalyzeVersionRequest? Validate(AnalyzeVersionRequest? request, out string? error)
    {
        error = null;
        if (request is null)
        {
            error = Invalid("the request is empty");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            error = Invalid("filePath is required");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.OutputDir))
        {
            error = Invalid("outputDir is required");
            return null;
        }

        if (request.ExpectedDocument is null || string.IsNullOrWhiteSpace(request.ExpectedDocument.Path))
        {
            error = Invalid("expectedDocument.path is required");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedDocument.FileId))
        {
            error = Invalid("expectedDocument.fileId is required");
            return null;
        }

        if (request.ExpectedUnits is null)
        {
            error = Invalid("expectedUnits is required");
            return null;
        }

        var units = CheckedUnits.FromTokens(
            request.ExpectedUnits.Force,
            request.ExpectedUnits.Length,
            request.ExpectedUnits.Temperature,
            out var unitsError);
        if (units is null)
        {
            error = Invalid($"expectedUnits: {unitsError}");
            return null;
        }

        // The label must describe the file that is analysed: an unverified path is refused.
        string fullFilePath;
        string fullExpectedPath;
        try
        {
            fullFilePath = System.IO.Path.GetFullPath(request.FilePath);
            fullExpectedPath = System.IO.Path.GetFullPath(request.ExpectedDocument.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            error = Invalid($"filePath or expectedDocument.path is not a usable path ({exception.Message})");
            return null;
        }

        if (!string.Equals(fullFilePath, fullExpectedPath, StringComparison.OrdinalIgnoreCase))
        {
            error = Invalid("filePath must be the expectedDocument.path (the analysed file is the expected document)");
            return null;
        }

        bool setAndRestore;
        switch (request.UnitPolicy)
        {
            case AnalyzeVersionRequest.PolicyCheckOnly:
                setAndRestore = false;
                break;
            case AnalyzeVersionRequest.PolicySetAndRestore:
                setAndRestore = true;
                break;
            case null:
                error = Invalid("unitPolicy is required");
                return null;
            default:
                error = Invalid($"unknown unitPolicy '{request.UnitPolicy}'");
                return null;
        }

        return new ValidAnalyzeVersionRequest(
            request,
            request.FilePath,
            request.OutputDir,
            request.ExpectedDocument.Path,
            request.ExpectedDocument.FileId,
            units,
            setAndRestore);
    }

    private static string Invalid(string why) => $"Invalid analyze-version request: {why}.";
}
