// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.OpenModel;

public interface IOpenModelService
{
    /// <summary>
    /// Opens an .edb file in the user's running ETABS.
    /// If save=true, saves the currently open file first.
    /// If save=false, discards unsaved changes without prompting.
    /// Hard error if ETABS is not running.
    /// </summary>
    Task<Result<OpenModelData>> OpenModelAsync(string filePath, bool save);
}
