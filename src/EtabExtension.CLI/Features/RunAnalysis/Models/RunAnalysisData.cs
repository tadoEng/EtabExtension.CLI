// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.RunAnalysis.Models;

public record RunAnalysisData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("casesRequested")]
    public List<string>? CasesRequested { get; init; }

    [JsonPropertyName("caseCount")]
    public int CaseCount { get; init; }

    [JsonPropertyName("finishedCaseCount")]
    public int FinishedCaseCount { get; init; }

    [JsonPropertyName("analysisTimeMs")]
    public long AnalysisTimeMs { get; init; }

    /// <summary>
    /// Units that were active when analysis ran and results were saved into the .edb.
    /// Downstream extract-results commands should normalise to the same unit system.
    /// </summary>
    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }
}
