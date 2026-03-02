// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.CloseModel.Models;

public record CloseModelData
{
    [JsonPropertyName("closedFilePath")]
    public string? ClosedFilePath { get; init; }

    [JsonPropertyName("wasSaved")]
    public bool WasSaved { get; init; }
}
