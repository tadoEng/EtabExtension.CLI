// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.CloseModel.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.CloseModel;

public interface ICloseModelService
{
    /// <summary>
    /// Clears the ETABS workspace. ETABS remains running with a blank model.
    /// Rust decides save/no-save — the flag is passed in, never inferred here.
    /// </summary>
    Task<Result<CloseModelData>> CloseModelAsync(bool save);
}
