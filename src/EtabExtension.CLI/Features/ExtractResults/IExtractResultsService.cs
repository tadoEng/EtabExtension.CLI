// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.ExtractResults;

public interface IExtractResultsService
{
    /// <summary>
    /// Opens the .edb in a hidden ETABS instance, normalises units to kip/ft,
    /// then extracts every table declared non-null in the request.
    ///
    /// Each table is written to its own {slug}.parquet file in outputDir.
    /// The result JSON contains a per-table outcome so Rust can detect partial failures.
    ///
    /// Always returns Result.Ok — partial failures are reported inside
    /// <see cref="ExtractResultsData.Tables"/> rather than as a top-level error.
    /// A top-level Result.Fail is only returned for ETABS startup / file-open failures.
    /// </summary>
    Task<Result<ExtractResultsData>> ExtractAsync(ExtractResultsRequest request);
}
