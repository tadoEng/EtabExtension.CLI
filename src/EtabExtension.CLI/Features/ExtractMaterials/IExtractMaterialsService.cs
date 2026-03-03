// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public interface IExtractMaterialsService
{
    /// <summary>
    /// Extracts material takeoff from an .edb and writes takeoff.parquet.
    /// Uses a hidden ETABS instance (Mode B). Requires analyzed model.
    /// </summary>
    Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(string filePath, string outputPath);
}
