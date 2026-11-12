// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.SaveModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.SaveModel;

public interface ISaveModelService
{
    /// <summary>
    /// Saves the model ETABS has open to <paramref name="filePath"/>, which must be
    /// that same model. Mode A: attaches to the running ETABS.
    /// </summary>
    Task<Result<SaveModelData>> SaveModelAsync(string filePath);

    Task<Result<SaveModelData>> SaveModelOnAppAsync(ETABSApplication app, string filePath);

    /// <summary>
    /// <see cref="SaveModelOnAppAsync(ETABSApplication, string)"/> that also refuses to save
    /// unless the requested file's identity equals <paramref name="expectedFileId"/> (card
    /// v2.3 §2, <c>expectedDocument</c>). Null states no expectation.
    /// </summary>
    Task<Result<SaveModelData>> SaveModelOnAppAsync(
        ETABSApplication app,
        string filePath,
        string? expectedFileId) => SaveModelOnAppAsync(app, filePath);
}
