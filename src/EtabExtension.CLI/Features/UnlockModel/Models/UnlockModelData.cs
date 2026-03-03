// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.UnlockModel.Models;

public record UnlockModelData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("wasLocked")]
    public bool WasLocked { get; init; }
}
