using System.Diagnostics;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;

public record RunMetrics
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; init; } = string.Empty;

    [JsonPropertyName("totalElapsedMs")]
    public long TotalElapsedMs { get; init; }

    [JsonPropertyName("phases")]
    public List<RunPhaseMetric> Phases { get; init; } = [];

    [JsonPropertyName("collectedAt")]
    public DateTimeOffset CollectedAt { get; init; }
}

public record RunPhaseMetric(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message);

internal sealed class RunMetricsBuilder(string command, string filePath, string outputDir)
{
    private readonly List<RunPhaseMetric> _phases = [];

    public async Task<T> MeasureAsync<T>(string name, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var value = await action();
            sw.Stop();
            _phases.Add(new RunPhaseMetric(name, sw.ElapsedMilliseconds, true, null));
            return value;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _phases.Add(new RunPhaseMetric(name, sw.ElapsedMilliseconds, false, ex.Message));
            throw;
        }
    }

    public async Task MeasureAsync(string name, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await action();
            sw.Stop();
            _phases.Add(new RunPhaseMetric(name, sw.ElapsedMilliseconds, true, null));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _phases.Add(new RunPhaseMetric(name, sw.ElapsedMilliseconds, false, ex.Message));
            throw;
        }
    }

    public T Measure<T>(string name, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var value = action();
            sw.Stop();
            _phases.Add(new RunPhaseMetric(name, sw.ElapsedMilliseconds, true, null));
            return value;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _phases.Add(new RunPhaseMetric(name, sw.ElapsedMilliseconds, false, ex.Message));
            throw;
        }
    }

    public void Add(string name, long elapsedMs, bool success, string? message = null)
    {
        _phases.Add(new RunPhaseMetric(name, elapsedMs, success, message));
    }

    public RunMetrics Build(long totalElapsedMs) => new()
    {
        Command = command,
        FilePath = filePath,
        OutputDir = outputDir,
        TotalElapsedMs = totalElapsedMs,
        Phases = [.. _phases],
        CollectedAt = DateTimeOffset.UtcNow
    };
}
