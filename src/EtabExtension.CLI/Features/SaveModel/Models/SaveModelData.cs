// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.SaveModel.Models;

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
}
