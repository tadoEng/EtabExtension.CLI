// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.ExtractMaterials.Models;

/// <summary>
/// Request for the extract-materials command.
///
/// JSON EXAMPLE (passed from Rust via --request):
/// {
///   "filePath":  "C:\\models\\building.edb",
///   "outputDir": "C:\\output\\materials",
///   "tableKey":  "Material List by Story",
///   "units":     "US_Kip_Ft"
/// }
///
/// UNIT PRESETS (case-insensitive) — same set as extract-results:
///   US_Kip_Ft (default)  US_Kip_In  US_Lb_Ft  US_Lb_In
///   SI_kN_m   SI_kN_mm   SI_N_m     SI_N_mm   SI_kgf_m  SI_tonf_m
///
/// OUTPUT FILE: {outputDir}/{tableSlug}.parquet
///   e.g. tableKey "Material List by Story" → material_list_by_story.parquet
/// </summary>
public record ExtractMaterialsRequest
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    /// <summary>
    /// ETABS database table key.
    /// null / omitted → defaults to "Material List by Story".
    /// </summary>
    [JsonPropertyName("tableKey")]
    public string? TableKey { get; init; }

    /// <summary>
    /// Unit preset to normalise to before extraction.
    /// null / omitted → defaults to "US_Kip_Ft".
    /// </summary>
    [JsonPropertyName("units")]
    public string? Units { get; init; }

    /// <summary>Specific columns to include. null = all columns.</summary>
    [JsonPropertyName("fieldKeys")]
    public string[]? FieldKeys { get; init; }
}
