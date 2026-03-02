// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.GetStatus;

public interface IGetStatusService
{
    /// <summary>
    /// Returns ETABS running state, PID, open file path, lock and analysis status.
    /// Returns Result.Ok with IsRunning=false when ETABS is not running — not an error.
    /// </summary>
    Task<Result<GetStatusData>> GetStatusAsync();
}
