using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Common;

public static class JsonExtensions
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Writes the result as JSON to REAL stdout (not the redirected stderr).
    /// This is the only place that should write to stdout.
    /// </summary>
    public static void WriteJsonToStdout<T>(this Result<T> result)
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        stdout.WriteLine(JsonSerializer.Serialize(result, DefaultOptions));
    }

    public static void WriteJsonToStdout(this Result result)
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        stdout.WriteLine(JsonSerializer.Serialize(result, DefaultOptions));
    }

    /// <summary>
    /// Writes JSON to stdout and returns the appropriate exit code (0=success, 1=failure).
    /// The only place Environment.Exit() should be called.
    /// </summary>
    public static int ExitWithResult<T>(this Result<T> result)
    {
        result.WriteJsonToStdout();
        return result.Success ? 0 : 1;
    }

    public static int ExitWithResult(this Result result)
    {
        result.WriteJsonToStdout();
        return result.Success ? 0 : 1;
    }
}
