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

    /// <summary>True when the file was opened in a new ETABS instance launched by the CLI.</summary>
    [JsonPropertyName("openedInNewInstance")]
    public bool OpenedInNewInstance { get; init; }
}
