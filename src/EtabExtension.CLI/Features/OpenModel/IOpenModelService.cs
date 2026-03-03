// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.OpenModel;

public interface IOpenModelService
{
    /// <summary>
    /// Opens an .edb file in ETABS.
    /// </summary>
    /// <param name="filePath">Path to the .edb file.</param>
    /// <param name="save">Save the currently open file before switching (Mode A only).</param>
    /// <param name="newInstance">
    /// When true: start a new visible ETABS instance and open the file in it (Mode B variant).
    /// When false (default): open in the user's already-running ETABS (Mode A).
    /// </param>
    Task<Result<OpenModelData>> OpenModelAsync(string filePath, bool save, bool newInstance);
}
