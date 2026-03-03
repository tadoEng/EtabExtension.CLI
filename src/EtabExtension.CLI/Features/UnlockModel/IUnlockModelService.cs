// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.UnlockModel.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.UnlockModel;

public interface IUnlockModelService
{
    /// <summary>
    /// Clears the post-analysis lock on the specified file.
    /// The file must already be open in ETABS.
    /// </summary>
    Task<Result<UnlockModelData>> UnlockModelAsync(string filePath);
}
