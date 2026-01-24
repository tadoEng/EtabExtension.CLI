using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Common;

/// <summary>
/// Represents the result of an operation with a return value
/// </summary>
public record Result<T>: Result
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    public Result(bool success, T? data, string? error) : base(success, error)
    {
        Data = data;
    }

}
