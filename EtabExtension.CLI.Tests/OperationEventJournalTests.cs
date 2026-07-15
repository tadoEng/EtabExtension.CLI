using System.Text.Json;
using EtabExtension.CLI.Features.Serve.Operations;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class OperationEventJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-operation-journal-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Assigns_monotonic_sequences_and_since_sequence_is_exclusive()
    {
        var journal = CreateJournal(memoryCapacity: 8);
        var first = journal.Append(Event("started"));
        var second = journal.Append(Event("step-started"));
        var third = journal.Append(Event("step-completed"));

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(3, third.Seq);
        Assert.Equal([2L, 3L], journal.ReadSince(1).Select(item => item.Seq));
        Assert.Empty(journal.ReadSince(3));
    }

    [Fact]
    public void Replays_evicted_events_from_the_durable_spill_without_duplicates()
    {
        var journal = CreateJournal(memoryCapacity: 2);
        for (var index = 0; index < 5; index++) journal.Append(Event($"event-{index}"));

        var replay = journal.ReadSince(1);

        Assert.Equal([2L, 3L, 4L, 5L], replay.Select(item => item.Seq));
        Assert.Equal(5, File.ReadLines(journal.FilePath).Count());
        foreach (var line in File.ReadLines(journal.FilePath))
        {
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.GetProperty("seq").GetInt64() > 0);
        }
    }

    private OperationEventJournal CreateJournal(int memoryCapacity) => new(
        Path.Combine(_directory, "operation", "events.jsonl"), memoryCapacity);

    private static OperationEvent Event(string type) => new()
    {
        Type = type,
        Phase = OperationPhase.Running,
        Timestamp = DateTimeOffset.UtcNow
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
