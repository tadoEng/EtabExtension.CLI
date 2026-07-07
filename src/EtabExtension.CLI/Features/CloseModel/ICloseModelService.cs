// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.CloseModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.CloseModel;

public interface ICloseModelService
{
    /// <summary>
    /// Clears the ETABS workspace. ETABS remains running with a blank model.
    /// Rust decides save/no-save — the flag is passed in, never inferred here.
    /// </summary>
    Task<Result<CloseModelData>> CloseModelAsync(bool save);

    /// <summary>
    /// Daemon variant of <see cref="CloseModelAsync"/>: clears the workspace on a
    /// caller-owned shared session app — no <c>Connect</c>, no <c>Dispose</c> (the
    /// serve session owns the ETABS lifecycle).
    /// </summary>
    Task<Result<CloseModelData>> CloseModelOnAppAsync(ETABSApplication app, bool save);
}
