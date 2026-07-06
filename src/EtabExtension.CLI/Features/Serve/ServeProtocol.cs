using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// A single request line on the serve daemon's stdin:
/// <c>{"id":123,"command":"analyze-and-extract","request":{...}}</c>.
/// The <c>request</c> payload is the existing per-command request JSON, unchanged.
/// </summary>
public sealed class ServeRequest
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("command")] public string Command { get; init; } = string.Empty;
    [JsonPropertyName("request")] public JsonElement? Request { get; init; }
}

/// <summary>
/// JSON options for the line-delimited serve protocol. Mirrors
/// <c>JsonExtensions.DefaultOptions</c> (camelCase, null-ignored, enum-as-string)
/// but <b>compact</b> (<c>WriteIndented = false</c>) so every response is exactly
/// one line — pretty-printing would break line framing.
/// </summary>
internal static class ServeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>
/// Routes one serve request to the matching feature, executed against the single
/// shared ETABS session. Returns the feature's existing <c>Result</c>/<c>Result&lt;T&gt;</c>
/// (the loop serializes it by runtime type and injects the correlation id).
/// </summary>
public interface IServeDispatcher
{
    Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct);
}
