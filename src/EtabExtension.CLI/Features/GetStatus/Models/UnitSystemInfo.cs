// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.
namespace EtabExtension.CLI.Features.GetStatus.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Human-readable unit breakdown for the active ETABS model.
/// Rust uses isUS / isMetric to decide how to label extracted table columns.
/// </summary>
public record UnitSystemInfo
{
    /// <summary>e.g. "kip", "kN", "N", "lb", "kgf", "tonf"</summary>
    [JsonPropertyName("force")]
    public string Force { get; init; } = string.Empty;

    /// <summary>e.g. "ft", "in", "m", "cm", "mm"</summary>
    [JsonPropertyName("length")]
    public string Length { get; init; } = string.Empty;

    /// <summary>"F" or "C"</summary>
    [JsonPropertyName("temperature")]
    public string Temperature { get; init; } = string.Empty;

    /// <summary>True when units are a standard US preset (kip/ft/F, lb/in/F, kip/in/F …)</summary>
    [JsonPropertyName("isUs")]
    public bool IsUs { get; init; }

    /// <summary>True when units are a standard Metric preset (kN/m/C, N/mm/C, kgf/m/C …)</summary>
    [JsonPropertyName("isMetric")]
    public bool IsMetric { get; init; }
}
