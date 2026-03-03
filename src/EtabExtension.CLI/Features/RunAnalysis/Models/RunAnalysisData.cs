// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.RunAnalysis.Models;

public record RunAnalysisData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("caseCount")]
    public int CaseCount { get; init; }

    [JsonPropertyName("finishedCaseCount")]
    public int FinishedCaseCount { get; init; }

    [JsonPropertyName("analysisTimeMs")]
    public long AnalysisTimeMs { get; init; }
}
