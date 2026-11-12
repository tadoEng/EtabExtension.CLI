// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.GetStatus.Models;

public enum EtabsInstanceOwnership
{
    None,
    Managed,
    External,
    Ambiguous
}

/// <summary>Who holds the managed instance, as reported by the daemon (card C5).</summary>
public enum EtabsDisposition
{
    /// <summary>This daemon owns a started or starting instance.</summary>
    Owned,

    /// <summary>A detach is in flight, or its outcome is unresolved. Nothing is destroyed.</summary>
    Detaching,

    /// <summary>The daemon handed the instance to the user and no longer owns it.</summary>
    Detached,

    /// <summary>This daemon owns no instance.</summary>
    None
}

public enum AbandonedRequestState
{
    /// <summary>The abandoned work has not returned yet.</summary>
    Running,

    /// <summary>The abandoned work has returned.</summary>
    Finished
}

/// <summary>A request the daemon answered but whose ETABS work it could not stop (card C5).</summary>
public sealed record AbandonedRequestInfo(
    [property: JsonPropertyName("requestId")] long RequestId,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("state")] AbandonedRequestState State);

/// <summary>The live identity a recovery pass detached instead of terminating (card C5).</summary>
public sealed record OrphanDispositionInfo(
    [property: JsonPropertyName("detached")] DetachedIdentity? Detached);

public sealed record DetachedIdentity(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startTimeUtc")] DateTimeOffset StartTimeUtc,
    [property: JsonPropertyName("exePath")] string ExePath);

public record GetStatusData
{
    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; init; }

    [JsonPropertyName("pid")]
    public int? Pid { get; init; }

    [JsonPropertyName("ownership")]
    public EtabsInstanceOwnership Ownership { get; init; }

    [JsonPropertyName("observedPids")]
    public IReadOnlyList<int> ObservedPids { get; init; } = [];

    /// <summary>
    /// The observed pids that exactly match the sibling daemon's record
    /// (<c>serve --sibling-record</c>). Never classified Managed or External.
    /// </summary>
    [JsonPropertyName("siblingPids")]
    public IReadOnlyList<int> SiblingPids { get; init; } = [];

    /// <summary>The launch generation of the instance this daemon holds, or null (card C5).</summary>
    [JsonPropertyName("launchRecordId")]
    public Guid? LaunchRecordId { get; init; }

    /// <summary>
    /// <c>owned | detaching | detached | none</c>. A reader that finds this missing must
    /// treat the daemon's outstanding work as UNKNOWN, never as none.
    /// </summary>
    [JsonPropertyName("disposition")]
    public EtabsDisposition? Disposition { get; init; }

    /// <summary>Requests abandoned to an unanswered ETABS dialog and whether they returned.</summary>
    [JsonPropertyName("abandoned")]
    public IReadOnlyList<AbandonedRequestInfo>? Abandoned { get; init; }

    /// <summary>What the startup recovery pass did to a live identity, or null.</summary>
    [JsonPropertyName("orphanDisposition")]
    public OrphanDispositionInfo? OrphanDisposition { get; init; }

    [JsonPropertyName("etabsVersion")]
    public string? EtabsVersion { get; init; }

    [JsonPropertyName("openFilePath")]
    public string? OpenFilePath { get; init; }

    [JsonPropertyName("isModelOpen")]
    public bool IsModelOpen { get; init; }

    [JsonPropertyName("isLocked")]
    public bool? IsLocked { get; init; }

    [JsonPropertyName("isAnalyzed")]
    public bool? IsAnalyzed { get; init; }

    /// <summary>
    /// Present unit system. Tells Rust what units all extracted table values are in.
    /// Null when ETABS is not running or no model is open.
    /// </summary>
    [JsonPropertyName("unitSystem")]
    public UnitSystemInfo? UnitSystem { get; init; }
}
