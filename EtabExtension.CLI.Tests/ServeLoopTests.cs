using System.Text.Json;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class ServeLoopTests
{
    private sealed record Echo(string Value);

    private sealed class FakeDispatcher : IServeDispatcher
    {
        public List<string> Commands { get; } = [];

        public Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
        {
            Commands.Add(command);
            object result = command == "boom"
                ? Result.Fail<Echo>("kaboom")
                : Result.Ok(new Echo(command));
            return Task.FromResult(result);
        }
    }

    private static async Task<List<JsonElement>> RunAsync(string input, FakeDispatcher dispatcher)
    {
        using var reader = new StringReader(input);
        await using var writer = new StringWriter();
        await new ServeLoop(dispatcher).RunAsync(reader, writer);
        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();
    }

    [Fact]
    public async Task Dispatches_each_request_serially_and_correlates_the_id()
    {
        var dispatcher = new FakeDispatcher();
        var responses = await RunAsync(
            "{\"id\":1,\"command\":\"get-status\"}\n{\"id\":2,\"command\":\"open-model\",\"request\":{}}\n",
            dispatcher);

        Assert.Equal(new[] { "get-status", "open-model" }, dispatcher.Commands);
        Assert.Equal(2, responses.Count);
        Assert.Equal(1, responses[0].GetProperty("id").GetInt64());
        Assert.True(responses[0].GetProperty("success").GetBoolean());
        Assert.Equal("get-status", responses[0].GetProperty("data").GetProperty("value").GetString());
        Assert.Equal(2, responses[1].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Failure_result_is_surfaced_with_id_and_omits_null_data()
    {
        var responses = await RunAsync("{\"id\":7,\"command\":\"boom\"}\n", new FakeDispatcher());

        Assert.Single(responses);
        Assert.Equal(7, responses[0].GetProperty("id").GetInt64());
        Assert.False(responses[0].GetProperty("success").GetBoolean());
        Assert.Equal("kaboom", responses[0].GetProperty("error").GetString());
        Assert.False(responses[0].TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Malformed_line_gets_an_error_but_the_loop_keeps_serving()
    {
        var dispatcher = new FakeDispatcher();
        var responses = await RunAsync("not json\n{\"id\":3,\"command\":\"get-status\"}\n", dispatcher);

        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].GetProperty("success").GetBoolean());
        Assert.True(responses[1].GetProperty("success").GetBoolean());
        Assert.Equal(new[] { "get-status" }, dispatcher.Commands);
    }

    [Fact]
    public async Task Shutdown_command_stops_the_loop_without_dispatching()
    {
        var dispatcher = new FakeDispatcher();
        var responses = await RunAsync(
            "{\"id\":1,\"command\":\"shutdown\"}\n{\"id\":2,\"command\":\"get-status\"}\n",
            dispatcher);

        Assert.Single(responses);
        Assert.Equal(1, responses[0].GetProperty("id").GetInt64());
        Assert.True(responses[0].GetProperty("success").GetBoolean());
        Assert.Empty(dispatcher.Commands);
    }
}
