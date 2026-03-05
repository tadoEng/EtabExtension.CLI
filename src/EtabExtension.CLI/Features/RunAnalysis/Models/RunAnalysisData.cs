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
    /// Units that were active when analysis ran.
    ///
    /// NOTE: We do NOT call SaveFile() after analysis — ETABS writes results
    /// directly into sidecar files (.Y*, .K_*, .msh) during the run.
    /// Calling SaveFile() would delete those sidecar files.
    ///
    /// The .EDB unit system is whatever the model was saved with originally.
    /// Downstream extract-results commands normalise to kip/ft regardless.
    /// </summary>
    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }
}
