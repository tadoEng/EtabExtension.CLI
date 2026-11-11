using System.Collections.Concurrent;

namespace EtabExtension.CLI.Features.Serve.Operations;

public interface IStaExecutionWorker : IDisposable
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> action);
}

public sealed class StaExecutionWorker : IStaExecutionWorker
{
    /// <summary>
    /// How long disposal waits for the STA thread. A work item stuck inside a CSI call that
    /// a modal ETABS dialog holds never returns on its own (review finding P2-1); disposal
    /// must not hang the daemon's exit on it.
    /// </summary>
    public static readonly TimeSpan DefaultDisposeJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<IWorkItem> _work = new();
    private readonly Thread _thread;
    private readonly TimeSpan _disposeJoinTimeout;
    private bool _disposed;

    public StaExecutionWorker()
        : this(DefaultDisposeJoinTimeout)
    {
    }

    internal StaExecutionWorker(TimeSpan disposeJoinTimeout)
    {
        _disposeJoinTimeout = disposeJoinTimeout;
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
        if (Thread.CurrentThread == _thread)
        {
            _work.Dispose();
            return;
        }

        if (_thread.Join(_disposeJoinTimeout))
        {
            _work.Dispose();
            return;
        }

        // Still inside a work item. The thread is a background thread and goes with the
        // process; the queue is left undisposed so that, if the stuck call does return
        // later, its consuming loop ends on CompleteAdding instead of throwing
        // ObjectDisposedException on a thread nobody is watching.
        Console.Error.WriteLine(
            "⚠ The ETABS STA worker did not stop within " +
            $"{_disposeJoinTimeout.TotalSeconds:0.###}s (a call is still blocked); abandoning it.");
    }

    /// <summary>True once the STA thread has finished. For tests and diagnostics.</summary>
    internal bool HasStopped => !_thread.IsAlive;

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
