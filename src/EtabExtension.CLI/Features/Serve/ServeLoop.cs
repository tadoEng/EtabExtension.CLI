using System.Text.Json;
using System.Text.Json.Nodes;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// The persistent stdin/stdout request loop for <c>etab-cli serve</c>.
///
/// <para><b>Concurrent control intake (card C8).</b> The reader NEVER awaits a command before
/// reading the next frame. Ordinary commands still run one at a time in arrival order (ETABS
/// COM is single-threaded): each is chained behind the previous one, and starts inline on the
/// reader when nothing is ahead of it. The control commands - <c>shutdown</c>, <c>detach</c>,
/// <c>cancel-operation</c>, <c>get-status</c> and the operation lookups - bypass that chain, so
/// they are accepted and answered while a synchronous command is still running. Every reply
/// is ONE whole frame carrying its request id, written under the write lock.</para>
///
/// <para>Transport only: all ETABS work happens in the injected <see cref="IServeDispatcher"/>
/// against a single shared session. Testable in isolation with any
/// <see cref="TextReader"/>/<see cref="TextWriter"/> and a fake dispatcher.</para>
/// </summary>
public sealed class ServeLoop
{
    internal const string ShutdownCommand = "shutdown";
    internal const string DetachCommand = "detach";

    /// <summary>
    /// Commands answered without waiting for the ordinary lane. None of them needs the STA
    /// while a synchronous command holds it: they read the daemon's own state.
    /// </summary>
    internal static readonly IReadOnlySet<string> ControlCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "cancel-operation",
        "get-operation-events",
        "get-operation-status",
        "get-status",

        // Registered and timed at INTAKE (review P1-3). Its body queues on the STA like any
        // synchronous command, but its cancellation entry and its Checking deadline exist from
        // the moment it is read, so a request stuck behind a blocked command can still be
        // cancelled or expire, and can never later exit on an old decision.
        "shutdown-if-unchanged"
    };

    private readonly IServeDispatcher _dispatcher;
    private readonly IServeShutdownCoordinator _shutdown;
    private readonly IManagedEtabsStartIntentScope _startIntent;
    private readonly ServeHandshake _handshake;
    private readonly TextWriter _diagnostics;
    private readonly ServeEofPolicy _eofPolicy;

    public ServeLoop(
        IServeDispatcher dispatcher,
        IServeShutdownCoordinator shutdown,
        IManagedEtabsStartIntentScope startIntent) : this(
            dispatcher,
            shutdown,
            startIntent,
            ServeHandshake.Current,
            Console.Error)
    {
    }

    internal ServeLoop(
        IServeDispatcher dispatcher,
        IServeShutdownCoordinator shutdown,
        IManagedEtabsStartIntentScope startIntent,
        Func<IReadOnlyList<string>, ServeHandshake> handshakeFactory,
        TextWriter diagnostics,
        ServeEofPolicy eofPolicy = ServeEofPolicy.Exit)
    {
        _dispatcher = dispatcher;
        _shutdown = shutdown;
        _startIntent = startIntent;
        _diagnostics = diagnostics;
        _eofPolicy = eofPolicy;
        var capabilities = dispatcher.Capabilities
            .Append(ShutdownCommand)
            .Append(DetachCommand)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        _handshake = handshakeFactory(capabilities);
    }

    /// <summary>
    /// Runs until stdin EOF, a <c>shutdown</c> command, or cancellation. Never
    /// throws for a bad request — malformed lines and failing handlers both get a
    /// correlated error response and the loop keeps serving. Only transport and
    /// cancellation failures end the run.
    ///
    /// <para>A handler may ACCEPT a request and answer it later by returning a
    /// <see cref="DeferredServeResponse"/>. The ordinary lane then moves straight on instead
    /// of waiting, which is what keeps <c>get-operation-status</c>,
    /// <c>get-operation-events</c>, <c>cancel-operation</c> and <c>shutdown</c> reachable
    /// while a long operation runs. It introduces no concurrent ETABS work: exactly one
    /// ordinary dispatch executes at a time, and every COM call still goes through the single
    /// STA worker.</para>
    ///
    /// <para><b>No drain on the way out (card C7a).</b> A shutdown or EOF never waits for an
    /// accepted request to finish: the instance is ended within the bounded exit, and a
    /// request still running is answered, if at all, by whatever its work returns.</para>
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var explicitShutdown = false;

        // The ordinary lane: the last command accepted onto it. Each new one runs after it.
        Task lane = Task.CompletedTask;

        // Accepted-but-unanswered requests and in-flight control replies. Pruned as they land.
        var inFlight = new List<Task>();

        // One whole frame at a time on stdout: a deferred or control answer is written from
        // its own continuation, and two responses interleaved mid-line would corrupt the
        // protocol for every request after them. Deliberately never disposed: a late answer
        // may still land after the loop returned, and must not find a disposed gate.
        var writes = new SemaphoreSlim(1, 1);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                _handshake, ServeJson.Options));
            await output.FlushAsync(ct);
            Console.Error.WriteLine("ℹ etab-cli serve: ready (line-delimited JSON on stdin/stdout)");

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // A fatal fault (a cancellation, a transport error) of an earlier request
                // ends the run here, exactly as it did when dispatch was awaited inline.
                if (lane.IsFaulted || lane.IsCanceled)
                {
                    await lane;
                }

                string? line = await input.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ServeRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<ServeRequest>(line, ServeJson.Options);
                }
                catch (JsonException exception)
                {
                    await WriteAsync(
                        writes,
                        output,
                        id: 0,
                        Result.Fail($"Invalid request JSON: {exception.Message}"));
                    continue;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Command))
                {
                    await WriteAsync(
                        writes,
                        output,
                        request?.Id ?? 0,
                        Result.Fail("Malformed request: missing command"));
                    continue;
                }

                if (string.Equals(
                    request.Command,
                    ShutdownCommand,
                    StringComparison.OrdinalIgnoreCase))
                {
                    explicitShutdown = true;
                    var terminal = await _shutdown.ShutdownAsync();

                    // The answer is written the moment the instance is ended. An accepted
                    // request still running is NOT awaited (card C7a): it cannot delay it.
                    await WriteAsync(writes, output, request.Id, terminal);
                    return;
                }

                if (string.Equals(request.Command, DetachCommand, StringComparison.Ordinal))
                {
                    await StartAsync(inFlight, AnswerDetachAsync(writes, output, request.Id));
                    continue;
                }

                if (ControlCommands.Contains(request.Command))
                {
                    await StartAsync(inFlight, RunRequestAsync(writes, output, request, inFlight, ct));
                    continue;
                }

                // Ordinary: runs now when nothing is ahead of it, otherwise right behind the
                // previous one. Either way the reader goes straight back to stdin.
                lane = lane.IsCompleted
                    ? RunRequestAsync(writes, output, request, inFlight, ct)
                    : ChainAfter(lane, () => RunRequestAsync(writes, output, request, inFlight, ct));
                if (lane.IsCompleted)
                {
                    await lane;
                }
            }
        }
        finally
        {
            // The release of the session. Never preceded by a drain of accepted work (card
            // C7a): a stuck answer must not be the reason ETABS is left running, and a failing
            // cleanup must not be the reason a caller waits forever.
            try
            {
                var cleanup = explicitShutdown
                    ? await _shutdown.ShutdownAsync()
                    : await _shutdown.EndOfInputAsync(_eofPolicy);
                if (!cleanup.Success && !explicitShutdown)
                {
                    await WriteCleanupFailureAsync(cleanup);
                }
            }
            catch (Exception cleanupException)
            {
                await WriteCleanupExceptionAsync(cleanupException);
            }

            // Only AFTER the instance was dealt with, and only on EOF (an explicit shutdown
            // has already answered and must not linger): give accepted answers one short,
            // bounded chance to land. It can delay nothing the exit needed.
            if (!explicitShutdown)
            {
                await SettleAcceptedAnswersAsync(inFlight);
            }
        }
    }

    /// <summary>How long EOF waits, after the exit, for accepted answers to be written.</summary>
    internal static readonly TimeSpan PostCleanupSettle = TimeSpan.FromSeconds(2);

    private static async Task SettleAcceptedAnswersAsync(List<Task> inFlight)
    {
        Task[] outstanding;
        lock (inFlight)
        {
            outstanding = inFlight.ToArray();
        }

        if (outstanding.Length == 0)
        {
            return;
        }

        try
        {
            _ = await Task.WhenAny(Task.WhenAll(outstanding), Task.Delay(PostCleanupSettle));
        }
#pragma warning disable CA1031 // a failing late answer must not fail the exit that already happened
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Awaits an already-finished task inline (so its fault propagates immediately) and
    /// otherwise lets it run on, observing any later fault.
    /// </summary>
    private static async Task StartAsync(List<Task> inFlight, Task started)
    {
        if (started.IsCompleted)
        {
            await started;
            return;
        }

        Track(inFlight, started);
        _ = started.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void Track(List<Task> inFlight, Task task)
    {
        lock (inFlight)
        {
            inFlight.RemoveAll(item => item.IsCompleted);
            inFlight.Add(task);
        }
    }

    private static Task ChainAfter(Task previous, Func<Task> next) =>
        previous.ContinueWith(
            _ => next(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();

    /// <summary>One request against the dispatcher, answered on its own id.</summary>
    private async Task RunRequestAsync(
        SemaphoreSlim writes,
        TextWriter output,
        ServeRequest request,
        List<Task> inFlight,
        CancellationToken ct)
    {
        var response = await DispatchIsolatedAsync(request, ct);
        if (response is DeferredServeResponse pending)
        {
            // Accepted, not answered. The lane moves on so control commands stay reachable
            // for the whole run.
            var answer = AnswerWhenReadyAsync(writes, output, request.Id, request.Command, pending);
            Track(inFlight, answer);
            _ = answer.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return;
        }

        await WriteAsync(writes, output, request.Id, response);
    }

    /// <summary><c>detach</c> (card C1): bounded, answered on its own id.</summary>
    private async Task AnswerDetachAsync(SemaphoreSlim writes, TextWriter output, long id)
    {
        object result;
        try
        {
            result = await _shutdown.DetachAsync();
        }
#pragma warning disable CA1031 // every accepted request is answered, whatever happened behind it
        catch (Exception exception)
#pragma warning restore CA1031
        {
            result = Result.Fail(EtabsApiDiagnosticFormatter.InfrastructureException(
                "IServeShutdownCoordinator.DetachAsync",
                exception));
        }

        await WriteAsync(writes, output, id, result);
    }

    /// <summary>
    /// Runs one request against the dispatcher with its failures contained to that
    /// request. A bad payload — a missing <c>request</c> object, a wrong field type,
    /// an unexpected handler or COM escape — becomes one bounded correlated failure
    /// response, and the daemon keeps serving. Without this the offending request
    /// would get no response at all and would take the managed ETABS session down
    /// with the loop.
    ///
    /// <para>Cancellation is deliberately not contained: when the serve token is
    /// cancelled the daemon really is stopping, so it propagates and terminates
    /// through the same shutdown coordinator as every other exit path.</para>
    ///
    /// <para>A typed managed-session failure keeps its own code. Wrapping an
    /// <see cref="EtabsLaunchException"/> in the generic infrastructure envelope
    /// would bury the one token a consumer branches on — the live proof surfaced
    /// <c>ETABS_MODEL_INITIALIZATION_FAILED</c> only inside a generic message.</para>
    ///
    /// <para>The declared intent and the request id are scoped to the synchronous part of the
    /// dispatch only: that is where a handler captures them (card C8 - two requests are now
    /// in flight at once, and a scope held across the whole command would be clobbered by the
    /// next one).</para>
    /// </summary>
    private async Task<object> DispatchIsolatedAsync(ServeRequest request, CancellationToken ct)
    {
        try
        {
            Task<object> dispatch;
            using (ServeRequestContext.Enter(new ServeRequestInfo(request.Id, request.Command)))
            using (_startIntent.Publish(ManagedEtabsStartIntents.Parse(request.StartIntent)))
            {
                dispatch = _dispatcher.DispatchAsync(request.Command, request.Request, ct);
            }

            return await dispatch;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EtabsLaunchException launch)
        {
            return Result.Fail(
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.Bounded(launch.Message),
                    $"command={request.Command}"));
        }
        catch (Exception exception)
        {
            return Result.Fail(
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IServeDispatcher.DispatchAsync",
                        exception),
                    $"command={request.Command}"));
        }
    }

    private async Task WriteCleanupFailureAsync(
        Result<ManagedEtabsShutdownData> cleanup)
    {
        var diagnostic = cleanup.Error
            ?? "Serve cleanup failed without a diagnostic.";
        var bounded = EtabsApiDiagnosticFormatter.AppendTerminalFacts(
            diagnostic,
            $"state={cleanup.Data?.State.ToString() ?? "unknown"}; " +
            $"processExitConfirmed={cleanup.Data?.ProcessExitConfirmed ?? false}; " +
            $"recordRetained={cleanup.Data?.RecordRetained ?? true}");
        await _diagnostics.WriteLineAsync(bounded);
        await _diagnostics.FlushAsync();
    }

    private async Task WriteCleanupExceptionAsync(Exception exception)
    {
        var bounded = EtabsApiDiagnosticFormatter.InfrastructureException(
            "IServeShutdownCoordinator.ShutdownAsync",
            exception);
        await _diagnostics.WriteLineAsync(bounded);
        await _diagnostics.FlushAsync();
    }

    /// <summary>
    /// Writes the answer to a request that was ACCEPTED earlier, once its work finishes.
    ///
    /// <para>Every failure becomes a response, cancellation included. A caller blocked on
    /// an id that never gets answered is a worse outcome than any diagnostic: the accepted
    /// request must be settled exactly once, whatever happened to the work behind it.</para>
    /// </summary>
    private static async Task AnswerWhenReadyAsync(
        SemaphoreSlim writes,
        TextWriter output,
        long id,
        string command,
        DeferredServeResponse pending)
    {
        object result;
        try
        {
            result = await pending.Completion;
        }
        catch (Exception exception)
        {
            result = Result.Fail(
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IServeDispatcher deferred completion",
                        exception),
                    $"command={command}"));
        }

        await WriteAsync(writes, output, id, result);
    }

    /// <summary>
    /// Serialize the feature result by its <b>runtime</b> type (so the concrete
    /// <c>Result&lt;T&gt;.data</c> is emitted, not an empty <c>object</c>), inject
    /// the correlation <c>id</c>, and write one compact line.
    ///
    /// <para>Serialized on the run's write gate: a deferred or control answer is written from
    /// its own continuation, and two responses interleaved mid-line would corrupt stdout for
    /// every request after them, not just those two. The whole frame is built BEFORE the gate
    /// is taken, so the lock is held for exactly one write.</para>
    /// </summary>
    private static async Task WriteAsync(
        SemaphoreSlim writes,
        TextWriter output,
        long id,
        object result)
    {
        var node = JsonSerializer.SerializeToNode(result, result.GetType(), ServeJson.Options)!.AsObject();
        node["id"] = id;
        var line = node.ToJsonString(ServeJson.Options);

        await writes.WaitAsync();
        try
        {
            await output.WriteLineAsync(line);
            await output.FlushAsync();
        }
        finally
        {
            writes.Release();
        }
    }
}
