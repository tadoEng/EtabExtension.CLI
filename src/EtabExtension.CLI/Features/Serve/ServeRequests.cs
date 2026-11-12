using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.Serve;

// Serve request payloads. These MUST match what the Rust ext-sidecar client's
// `request_from_args` produces (crates/ext-sidecar/src/client.rs): for commands
// whose one-shot form used CLI flags, the flags move into the request JSON, and
// the `--request` JSON is *flattened* into the top level (not nested). So an
// analyze request arrives as {filePath, outputDir, units, cases, tables, ...}.

/// <summary>
/// filePath/outputDir locator for the flattened <c>analyze-and-extract</c> and
/// <c>snapshot-export</c> requests. The per-command request fields (units,
/// tables, …) are deserialised separately from the SAME element (unknown fields
/// are ignored on each pass).
/// </summary>
public sealed class ServeFileLocator
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("outputDir")] public string OutputDir { get; init; } = string.Empty;
}

/// <summary>
/// Payload for the <c>open-model</c> serve command. The Rust client sends
/// <c>saveOnClose</c> (and <c>newInstance</c>, which is irrelevant in daemon
/// mode — there is exactly one shared instance).
/// </summary>
public sealed class ServeOpenModelRequest
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("saveOnClose")] public bool SaveOnClose { get; init; }

    /// <summary>
    /// The analysis daemon's open: the model is opened and ETABS is NEVER revealed
    /// (core-loop §2.4). Absent means the explicit user-visible open.
    /// </summary>
    [JsonPropertyName("background")] public bool Background { get; init; }

    /// <summary>
    /// The ETABS version that saved the model (x.y.z). When present and newer (major.minor)
    /// than the running ETABS, the open is refused with ETABS_VERSION_MISMATCH before
    /// OpenFile. Absent means no pre-check; the modal-dialog watchdog still protects.
    /// </summary>
    [JsonPropertyName("expectedModelVersion")] public string? ExpectedModelVersion { get; init; }
}

public sealed class ServeCloseModelRequest
{
    [JsonPropertyName("save")] public bool Save { get; init; }
}

public sealed class ServeFileRequest
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
}

public sealed class ServeGenerateE2KRequest
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("outputFile")] public string OutputFile { get; init; } = string.Empty;
    [JsonPropertyName("overwrite")] public bool Overwrite { get; init; }

    /// <summary>Same pre-check as <see cref="ServeOpenModelRequest.ExpectedModelVersion"/>.</summary>
    [JsonPropertyName("expectedModelVersion")] public string? ExpectedModelVersion { get; init; }
}

public sealed class ServeRunAnalysisRequest
{
    [JsonPropertyName("filePath")] public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("cases")] public List<string>? Cases { get; init; }
    [JsonPropertyName("units")] public string? Units { get; init; }
}
