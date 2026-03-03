// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.RunAnalysis;

public interface IRunAnalysisService
{
    /// <summary>
    /// Runs complete analysis on a snapshot .edb using a hidden ETABS instance (Mode B).
    /// Saves results back into the .edb before exiting so they persist.
    /// Never attaches to the user's running ETABS.
    /// </summary>
    Task<Result<RunAnalysisData>> RunAnalysisAsync(string filePath);
}
