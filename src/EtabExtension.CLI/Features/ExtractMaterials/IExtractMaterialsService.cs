// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.ExtractMaterials;

public interface IExtractMaterialsService
{
    /// <summary>
    /// Extracts one ETABS database table to a .parquet file.
    /// Uses a hidden ETABS instance (Mode B).
    ///
    /// Output file: {request.OutputDir}/{tableSlug}.parquet
    ///
    /// Returns Result.Ok with RowCount=0 and OutputFile=null when the table
    /// is empty — this is not an error, the parquet file is simply not written.
    ///
    /// Returns Result.Fail only for ETABS startup / file-open failures or
    /// an unrecognised unit preset.
    /// </summary>
    Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(ExtractMaterialsRequest request);
}
