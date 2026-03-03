// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.RunAnalysis;

public interface IRunAnalysisService
{
    /// <summary>
    /// Runs analysis on a snapshot .edb using a hidden ETABS instance (Mode B).
    /// Saves results back into the .edb before exit so they persist.
    /// </summary>
    /// <param name="filePath">Path to the .edb file.</param>
    /// <param name="cases">
    /// Specific load case names to run. When null or empty, all cases are run (default).
    /// Internally: SetRunCaseFlag(all=true, run=false) first, then per-case SetRunCaseFlag(run=true).
    /// </param>
    Task<Result<RunAnalysisData>> RunAnalysisAsync(string filePath, List<string>? cases);
}
