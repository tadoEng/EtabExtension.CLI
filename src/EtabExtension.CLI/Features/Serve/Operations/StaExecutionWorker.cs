using System.Collections.Concurrent;

namespace EtabExtension.CLI.Features.Serve.Operations;

public interface IStaExecutionWorker : IDisposable
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> action);
}

public sealed class StaExecutionWorker : IStaExecutionWorker
{
    private readonly BlockingCollection<IWorkItem> _work = new();
    private readonly Thread _thread;
    private bool _disposed;

    public StaExecutionWorker()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The ETABS STA worker requires Windows");
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "etab-cli-serve-sta"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = new WorkItem<T>(action);
        _work.Add(item);
        return item.Completion.Task;
    }

    private void Run()
    {
        using var context = new PumpSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);

        foreach (var item in _work.GetConsumingEnumerable())
        {
            item.Run(context);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _work.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }
        _work.Dispose();
    }

    private interface IWorkItem
    {
        void Run(PumpSynchronizationContext context);
    }

    private sealed class WorkItem<T>(Func<Task<T>> action) : IWorkItem
    {
        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run(PumpSynchronizationContext context)
        {
            try
            {
                var task = action();
                context.PumpUntil(task);
                Completion.TrySetResult(task.GetAwaiter().GetResult());
            }
            catch (OperationCanceledException ex)
            {
                Completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }

    private sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback d, object? state) => _callbacks.Add((d, state));

        public void PumpUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (_callbacks.TryTake(out var callback, millisecondsTimeout: 50))
                {
                    callback.Callback(callback.State);
                }
            }

            while (_callbacks.TryTake(out var callback))
            {
                callback.Callback(callback.State);
            }
        }

        public void Dispose() => _callbacks.Dispose();
    }
}
