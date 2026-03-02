// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.OpenModel.Models;

public record OpenModelData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("previousFilePath")]
    public string? PreviousFilePath { get; init; }

    [JsonPropertyName("pid")]
    public int? Pid { get; init; }
}
