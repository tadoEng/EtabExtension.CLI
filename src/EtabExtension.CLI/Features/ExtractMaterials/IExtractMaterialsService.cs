// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public interface IExtractMaterialsService
{
    /// <summary>
    /// Extracts material takeoff via the ETABS DatabaseTables API.
    /// Writes one parquet file per table. Uses a hidden ETABS instance (Mode B).
    /// </summary>
    /// <param name="filePath">Path to the .edb file.</param>
    /// <param name="outputPath">Destination .parquet file path.</param>
    /// <param name="tableKey">
    /// ETABS database table key. Defaults to "Material List by Story" 
    /// </param>
    Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(
        string filePath,
        string outputPath,
        string tableKey = "Material List by Story");
}
