// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;

/// <summary>
/// One ETABS unit triple as the daemon reports it: the readable tokens AND the raw enum
/// values they came from. No conversion happens anywhere in this type (card I-8).
/// </summary>
public sealed record UnitBlock
{
    [JsonPropertyName("force")] public required string Force { get; init; }
    [JsonPropertyName("length")] public required string Length { get; init; }

    /// <summary>"F" or "C"; null only for a block built from a request that named none.</summary>
    [JsonPropertyName("temperature")] public string? Temperature { get; init; }

    [JsonPropertyName("rawForce")] public required int RawForce { get; init; }
    [JsonPropertyName("rawLength")] public required int RawLength { get; init; }
    [JsonPropertyName("rawTemperature")] public int? RawTemperature { get; init; }

    /// <summary>Force and length name the same units (temperature is never part of compatibility).</summary>
    public bool SameForceAndLength(UnitBlock other) =>
        RawForce == other.RawForce && RawLength == other.RawLength;

    /// <summary>Force, length AND temperature are identical (used to verify a restore).</summary>
    public bool SameAll(UnitBlock other) =>
        SameForceAndLength(other) && RawTemperature == other.RawTemperature;
}

/// <summary>
/// The checked reading and mapping of ETABS present/database units (analyze-version card
/// M1). The raw <c>cSapModel</c> calls return a code AND three enum values; this type
/// refuses a nonzero code, <c>NotApplicable</c> (0), <c>micron</c>, and any value outside the
/// documented enums, so an unreadable answer can never be spent as a unit system.
/// </summary>
public static class CheckedUnits
{
    /// <summary>The raw call shape of <c>Get*Units_2</c>: a return code and three enum values.</summary>
    public delegate int RawUnitsReader(out int force, out int length, out int temperature);

    // ETABSv1 enum values (Cardex pages for eForce / eLength / eTemperature). A test pins
    // these against the interop enums, so a drift in the assembly cannot go unnoticed.
    private static readonly (string Token, int Raw)[] Forces =
    [
        ("lb", 1), ("kip", 2), ("N", 3), ("kN", 4), ("kgf", 5), ("tonf", 6)
    ];

    private static readonly (string Token, int Raw)[] Lengths =
    [
        ("in", 1), ("ft", 2), ("mm", 4), ("cm", 5), ("m", 6)
    ];

    private static readonly (string Token, int Raw)[] Temperatures =
    [
        ("F", 1), ("C", 2)
    ];

    /// <summary>The force tokens the contract accepts, in raw-value order.</summary>
    public static IReadOnlyList<string> ForceTokens { get; } = Forces.Select(item => item.Token).ToArray();

    /// <summary>The length tokens the contract accepts, in raw-value order.</summary>
    public static IReadOnlyList<string> LengthTokens { get; } = Lengths.Select(item => item.Token).ToArray();

    /// <summary>The temperature tokens the contract accepts, in raw-value order.</summary>
    public static IReadOnlyList<string> TemperatureTokens { get; } = Temperatures.Select(item => item.Token).ToArray();

    public static bool TryForceToRaw(string? token, out int raw) => TryToRaw(Forces, token, out raw);
    public static bool TryLengthToRaw(string? token, out int raw) => TryToRaw(Lengths, token, out raw);
    public static bool TryTemperatureToRaw(string? token, out int raw) => TryToRaw(Temperatures, token, out raw);

    public static string? ForceToken(int raw) => ToToken(Forces, raw);
    public static string? LengthToken(int raw) => ToToken(Lengths, raw);
    public static string? TemperatureToken(int raw) => ToToken(Temperatures, raw);

    /// <summary>
    /// Builds a block from tokens. Force and length are required and exact (case-sensitive);
    /// temperature is optional. Null with the reason when any given token is unknown.
    /// </summary>
    public static UnitBlock? FromTokens(string? force, string? length, string? temperature, out string? error)
    {
        error = null;
        if (!TryForceToRaw(force, out var rawForce))
        {
            error = $"unknown force unit '{force}'";
            return null;
        }

        if (!TryLengthToRaw(length, out var rawLength))
        {
            error = $"unknown length unit '{length}'";
            return null;
        }

        if (temperature is null)
        {
            return new UnitBlock { Force = force!, Length = length!, RawForce = rawForce, RawLength = rawLength };
        }

        if (!TryTemperatureToRaw(temperature, out var rawTemperature))
        {
            error = $"unknown temperature unit '{temperature}'";
            return null;
        }

        return new UnitBlock
        {
            Force = force!,
            Length = length!,
            Temperature = temperature,
            RawForce = rawForce,
            RawLength = rawLength,
            RawTemperature = rawTemperature
        };
    }

    /// <summary>
    /// Maps three raw enum values to a block, or null with the reason. 0, micron and any
    /// out-of-range value are an unreadable answer.
    /// </summary>
    public static UnitBlock? FromRaw(int rawForce, int rawLength, int rawTemperature, out string? error)
    {
        error = null;
        var force = ForceToken(rawForce);
        var length = LengthToken(rawLength);
        var temperature = TemperatureToken(rawTemperature);
        if (force is null || length is null || temperature is null)
        {
            error = $"unreadable units answer (force={rawForce}, length={rawLength}, temperature={rawTemperature})";
            return null;
        }

        return new UnitBlock
        {
            Force = force,
            Length = length,
            Temperature = temperature,
            RawForce = rawForce,
            RawLength = rawLength,
            RawTemperature = rawTemperature
        };
    }

    /// <summary>
    /// One checked read: the call must return 0 and every value must be known. A throw is
    /// an unreadable answer too. Never returns a half-checked block.
    /// </summary>
    public static Result<UnitBlock> Read(RawUnitsReader read, string operation)
    {
        try
        {
            var code = read(out var force, out var length, out var temperature);
            if (code != 0)
            {
                return Result.Fail<UnitBlock>($"{operation} returned {code}");
            }

            var block = FromRaw(force, length, temperature, out var error);
            return block is null
                ? Result.Fail<UnitBlock>($"{operation}: {error}")
                : Result.Ok(block);
        }
#pragma warning disable CA1031 // any COM fault is an unreadable answer, never a unit system
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Result.Fail<UnitBlock>($"{operation} threw: {exception.Message}");
        }
    }

    private static bool TryToRaw((string Token, int Raw)[] table, string? token, out int raw)
    {
        foreach (var item in table)
        {
            if (string.Equals(item.Token, token, StringComparison.Ordinal))
            {
                raw = item.Raw;
                return true;
            }
        }

        raw = 0;
        return false;
    }

    private static string? ToToken((string Token, int Raw)[] table, int raw)
    {
        foreach (var item in table)
        {
            if (item.Raw == raw)
            {
                return item.Token;
            }
        }

        return null;
    }
}
