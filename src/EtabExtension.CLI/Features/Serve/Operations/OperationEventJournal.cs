using System.Text.Json;

namespace EtabExtension.CLI.Features.Serve.Operations;

public interface IOperationEventJournal
{
    string FilePath { get; }
    long LastSequence { get; }
    OperationEvent Append(OperationEvent item);
    IReadOnlyList<OperationEvent> ReadSince(long sinceSequence);
}

public sealed class OperationEventJournal : IOperationEventJournal
{
    private readonly object _gate = new();
    private readonly int _memoryCapacity;
    private readonly Queue<OperationEvent> _tail = new();
    private long _lastSequence;

    public OperationEventJournal(string filePath, int memoryCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryCapacity, 1);
        FilePath = filePath;
        _memoryCapacity = memoryCapacity;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("Event spill path must have a directory", nameof(filePath)));
    }

    public string FilePath { get; }
    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                return _lastSequence;
            }
        }
    }

    public OperationEvent Append(OperationEvent item)
    {
        lock (_gate)
        {
            var sequenced = item with { Seq = ++_lastSequence };
            File.AppendAllText(FilePath, JsonSerializer.Serialize(sequenced, ServeJson.Options) + "\n");
            _tail.Enqueue(sequenced);
            while (_tail.Count > _memoryCapacity)
            {
                _tail.Dequeue();
            }
            return sequenced;
        }
    }

    public IReadOnlyList<OperationEvent> ReadSince(long sinceSequence)
    {
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sinceSequence);
            if (_tail.Count == 0 || sinceSequence >= _tail.Peek().Seq - 1)
            {
                return _tail.Where(item => item.Seq > sinceSequence).ToArray();
            }
            if (!File.Exists(FilePath))
            {
                return [];
            }

            return File.ReadLines(FilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<OperationEvent>(line, ServeJson.Options)
                    ?? throw new InvalidDataException($"Invalid operation event in '{FilePath}'"))
                .Where(item => item.Seq > sinceSequence)
                .ToArray();
        }
    }
}

public interface IOperationEventJournalFactory
{
    IOperationEventJournal Create(string operationId);
}

public sealed class OperationEventJournalFactory : IOperationEventJournalFactory
{
    private readonly string _operationsDirectory;
    private readonly int _memoryCapacity;

    public OperationEventJournalFactory(string? operationsDirectory = null, int memoryCapacity = 256)
    {
        _operationsDirectory = operationsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EtabExtension", "sidecar", "operations");
        _memoryCapacity = memoryCapacity;
    }

    public IOperationEventJournal Create(string operationId) => new OperationEventJournal(
        Path.Combine(_operationsDirectory, operationId, "events.jsonl"), _memoryCapacity);
}
