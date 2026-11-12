// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

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

    /// <summary>
    /// The identity the daemon recorded durably for the model it opened (card v2.5a A1): the
    /// <c>expectedDocument</c> a later <c>save-and-exit</c> must present. Null for the Mode A
    /// paths, which record nothing.
    /// </summary>
    [JsonPropertyName("document")]
    public EtabsDocumentRecord? Document { get; init; }
}
