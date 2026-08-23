using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Goldfish.Harness;

public sealed record GoldfishHarnessTurnRequest(
    GoldfishHarnessSessionRequest Session,
    string? TurnId = null,
    string? RequestId = null,
    string? RetryOfTurnId = null,
    string Source = "local");

public sealed record HarnessContextContribution(string Provider, int Characters, int EstimatedTokens, TimeSpan Elapsed);

public sealed record HarnessContextBuildResult(
    GoldfishHarnessRequest Request,
    IReadOnlyList<HarnessContextContribution> Contributions);

public interface IHarnessContextProvider
{
    string Name { get; }
    int Order { get; }
    Task<HarnessContextBuildResult> BuildAsync(GoldfishHarnessSessionRequest request, CancellationToken ct);
}

public interface IHarnessExecutionStrategy
{
    IAsyncEnumerable<GoldfishHarnessEvent> ExecuteAsync(GoldfishHarnessRequest request, CancellationToken ct);
}

public interface IHarnessToolPolicy
{
    Task AuthorizeAsync(string turnId, string toolId, string arguments, CancellationToken ct);
}

public sealed class GoldfishRunnerExecutionStrategy(GoldfishHarnessRunner runner) : IHarnessExecutionStrategy
{
    public IAsyncEnumerable<GoldfishHarnessEvent> ExecuteAsync(GoldfishHarnessRequest request, CancellationToken ct)
        => runner.StreamAsync(request, ct);
}

public sealed class DefaultHarnessContextProvider(GoldfishHarnessContextAssembler assembler) : IHarnessContextProvider
{
    public string Name => "goldfish.default-context";
    public int Order => 100;

    public async Task<HarnessContextBuildResult> BuildAsync(GoldfishHarnessSessionRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var assembled = await assembler.AssembleAsync(request, ct);
        var characters = assembled.History.Sum(message => message.Content?.Length ?? 0)
            + assembled.UserMessageText.Length;
        return new HarnessContextBuildResult(assembled,
        [
            new HarnessContextContribution(Name, characters, Math.Max(1, characters / 4), Stopwatch.GetElapsedTime(started))
        ]);
    }
}

/// <summary>
/// Process-scoped Harness runtime. It owns local serialization, durable turn
/// lifecycle, idempotency, event backpressure and crash-safe terminal state.
/// Gateway remains the only ingress queue owner.
/// </summary>
public sealed class GoldfishHarnessRuntime : IAsyncDisposable
{
    private readonly IHarnessRuntimeStore _store;
    private readonly GoldfishSessionQueue _queue;
    private readonly HarnessStateOptions _options;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():n}";
    private readonly ConcurrentDictionary<string, ActiveTurn> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestGates = new(StringComparer.Ordinal);
    private bool _disposed;

    public GoldfishHarnessRuntime(
        IHarnessRuntimeStore store,
        GoldfishSessionQueue? queue = null,
        HarnessStateOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queue = queue ?? new GoldfishSessionQueue();
        _options = options ?? new HarnessStateOptions();
        _store.RecoverOrphanedAsync(DateTimeOffset.UtcNow).GetAwaiter().GetResult();
        _store.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays))).GetAwaiter().GetResult();
    }

    public int SchemaVersion => _store.SchemaVersion;

    public async IAsyncEnumerable<GoldfishHarnessEvent> ExecuteAsync(
        GoldfishHarnessTurnRequest request,
        IHarnessContextProvider contextProvider,
        IHarnessExecutionStrategy strategy,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(contextProvider);
        ArgumentNullException.ThrowIfNull(strategy);
        var prepared = await PrepareAsync(request, contextProvider, strategy, ct);
        await foreach (var ev in prepared.Subscription.ReadAllAsync(ct)) yield return ev;
    }

    public bool Cancel(string turnId)
    {
        if (!_active.TryGetValue(turnId, out var active)) return false;
        active.Cancellation.Cancel();
        _queue.Cancel(active.QueueKey, turnId);
        return true;
    }

    public Task ResetSessionAsync(MemoryPartition partition, CancellationToken ct = default)
        => _store.ResetSessionAsync(GoldfishTurnPartition.From(partition), ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var active in _active.Values) active.Cancellation.Cancel();
        await Task.WhenAll(_active.Values.Select(active => active.Completion.Task)).ConfigureAwait(false);
        foreach (var gate in _requestGates.Values) gate.Dispose();
    }

    private async Task<PreparedTurn> PrepareAsync(
        GoldfishHarnessTurnRequest request,
        IHarnessContextProvider contextProvider,
        IHarnessExecutionStrategy strategy,
        CancellationToken ct)
    {
        var partition = GoldfishTurnPartition.From(request.Session.MemoryPartition with
        {
            SessionId = request.Session.SessionId
        });
        var requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("n") : request.RequestId;
        var identityKey = $"{partition.QueueKey}\u001f{requestId}";
        var gate = _requestGates.GetOrAdd(identityKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var selected = request.Session.ReasoningOptions?.Strategy ?? ReasoningStrategyKind.ReAct;
            var proposed = new GoldfishHarnessTurn
            {
                TurnId = string.IsNullOrWhiteSpace(request.TurnId) ? $"harness-{Guid.NewGuid():n}" : request.TurnId,
                RequestId = requestId,
                RunId = Guid.NewGuid().ToString("n"),
                TenantId = partition.TenantId,
                UserId = partition.UserId,
                AgentId = partition.AgentId,
                WorkspaceId = partition.WorkspaceId,
                SessionId = partition.SessionId,
                Strategy = selected.ToString(),
                RetryOfTurnId = request.RetryOfTurnId
            };
            var create = await _store.GetOrCreateTurnAsync(proposed, request.Session.Prompt, ct);
            var turn = create.Turn;

            if (!create.Created && turn.IsTerminal)
            {
                var replay = Channel.CreateBounded<GoldfishHarnessEvent>(256);
                foreach (var ev in await _store.ReadEventsAsync(turn.TurnId, ct))
                    await replay.Writer.WriteAsync(ev.Event, ct);
                replay.Writer.TryComplete();
                return new PreparedTurn(replay.Reader);
            }

            if (_active.TryGetValue(turn.TurnId, out var existing))
                return new PreparedTurn(existing.Subscribe());

            if (!create.Created && turn.Status == GoldfishTurnStatus.Running)
            {
                var unavailable = Channel.CreateBounded<GoldfishHarnessEvent>(1);
                await unavailable.Writer.WriteAsync(GoldfishHarnessEvent.Failed(0,
                    "The matching turn is already running in another Harness process."), ct);
                unavailable.Writer.TryComplete();
                return new PreparedTurn(unavailable.Reader);
            }

            var active = new ActiveTurn(partition.QueueKey);
            if (!_active.TryAdd(turn.TurnId, active))
                return new PreparedTurn(_active[turn.TurnId].Subscribe());
            var reader = active.Subscribe();
            _ = RunTurnAsync(turn, request.Session, contextProvider, strategy, active);
            return new PreparedTurn(reader);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunTurnAsync(
        GoldfishHarnessTurn turn,
        GoldfishHarnessSessionRequest session,
        IHarnessContextProvider contextProvider,
        IHarnessExecutionStrategy strategy,
        ActiveTurn active)
    {
        try
        {
            var context = await contextProvider.BuildAsync(session, active.Cancellation.Token);
            var queuedRequest = context.Request with { QueueKey = turn.Partition.QueueKey, TurnId = turn.TurnId };
            var submission = _queue.Enqueue(queuedRequest, ExecuteDurably, turn.TurnId);
            await foreach (var ev in submission.Events.WithCancellation(active.Cancellation.Token))
                await active.PublishAsync(ev, active.Cancellation.Token);
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            var canceled = GoldfishHarnessEvent.Failed(0, "Turn canceled.");
            await _store.TryCompleteWithEventAsync(turn.TurnId, turn.SessionId, canceled,
                GoldfishTurnStatus.Canceled, "cancelled", "Turn canceled.", null, CancellationToken.None);
            await active.PublishAsync(canceled, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var failed = GoldfishHarnessEvent.Failed(0, ex.Message);
            try
            {
                await _store.TryCompleteWithEventAsync(turn.TurnId, turn.SessionId, failed,
                    GoldfishTurnStatus.Failed, "runtime_error", ex.Message, null, CancellationToken.None);
            }
            catch
            {
                // The original persistence failure is surfaced through the event.
            }
            await active.PublishAsync(failed, CancellationToken.None);
        }
        finally
        {
            active.Complete();
            _active.TryRemove(new KeyValuePair<string, ActiveTurn>(turn.TurnId, active));
        }

        async IAsyncEnumerable<GoldfishHarnessEvent> ExecuteDurably(
            GoldfishHarnessRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (!await _store.TryStartAsync(turn.TurnId, _leaseOwner,
                DateTimeOffset.UtcNow.AddSeconds(_options.LeaseSeconds), ct))
                throw new InvalidOperationException("Harness turn could not enter Running state.");

            using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatAsync(turn.TurnId, heartbeatStop.Token);
            GoldfishTurnStatus? terminal = null;
            string? terminalReason = null;
            string? answer = null;
            try
            {
                await foreach (var batch in BatchAsync(strategy.ExecuteAsync(request, ct), ct))
                {
                    var terminalIndex = batch.ToList().FindIndex(ev => ev.Kind is GoldfishEventKind.Completed or GoldfishEventKind.Failed);
                    if (terminalIndex >= 0 && terminalIndex != batch.Count - 1)
                        throw new InvalidOperationException("Strategy emitted events after its terminal event.");
                    var durableCount = terminalIndex < 0 ? batch.Count : terminalIndex;
                    if (durableCount > 0)
                        await _store.AppendEventsAsync(turn.TurnId, turn.SessionId, batch.Take(durableCount).ToArray(), ct);
                    for (var index = 0; index < batch.Count; index++)
                    {
                        var ev = batch[index];
                        if (ev.Kind == GoldfishEventKind.Completed)
                        {
                            terminal = GoldfishTurnStatus.Completed;
                            terminalReason = ev.Delta;
                            answer = ev.Delta;
                        }
                        else if (ev.Kind == GoldfishEventKind.Failed)
                        {
                            terminal = GoldfishTurnStatus.Failed;
                            terminalReason = ev.Delta;
                        }
                        if (index == terminalIndex)
                        {
                            await _store.TryCompleteWithEventAsync(turn.TurnId, turn.SessionId, ev,
                                terminal!.Value,
                                terminal == GoldfishTurnStatus.Completed ? "end_turn" : "strategy_failed",
                                terminalReason, answer, CancellationToken.None);
                        }
                        yield return ev;
                    }
                }

                if (terminal is null)
                {
                    var failed = GoldfishHarnessEvent.Failed(0, "Strategy ended without a terminal event.");
                    terminal = GoldfishTurnStatus.Failed;
                    terminalReason = failed.Delta;
                    await _store.TryCompleteWithEventAsync(turn.TurnId, turn.SessionId, failed,
                        terminal.Value, "strategy_failed", terminalReason, null, CancellationToken.None);
                    yield return failed;
                }
            }
            finally
            {
                heartbeatStop.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task HeartbeatAsync(string turnId, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.LeaseSeconds / 3));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
            await _store.HeartbeatAsync(turnId, _leaseOwner,
                DateTimeOffset.UtcNow.AddSeconds(_options.LeaseSeconds), ct);
    }

    private async IAsyncEnumerable<IReadOnlyList<GoldfishHarnessEvent>> BatchAsync(
        IAsyncEnumerable<GoldfishHarnessEvent> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<GoldfishHarnessEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in source.WithCancellation(ct)) await channel.Writer.WriteAsync(ev, ct);
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
        }, CancellationToken.None);

        var batch = new List<GoldfishHarnessEvent>();
        var bytes = 0;
        while (await channel.Reader.WaitToReadAsync(ct))
        {
            var first = await channel.Reader.ReadAsync(ct);
            batch.Add(first);
            bytes += EventSize(first);
            var immediate = first.Kind is not (GoldfishEventKind.TextDelta or GoldfishEventKind.ThinkingDelta);
            if (!immediate)
            {
                using var flush = CancellationTokenSource.CreateLinkedTokenSource(ct);
                flush.CancelAfter(Math.Max(1, _options.DeltaBatchMilliseconds));
                try
                {
                    while (bytes < _options.DeltaBatchBytes && await channel.Reader.WaitToReadAsync(flush.Token))
                    {
                        if (!channel.Reader.TryRead(out var next)) continue;
                        batch.Add(next);
                        bytes += EventSize(next);
                        if (next.Kind is not (GoldfishEventKind.TextDelta or GoldfishEventKind.ThinkingDelta)) break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            yield return batch.ToArray();
            batch.Clear();
            bytes = 0;
        }
        await producer;
    }

    private static int EventSize(GoldfishHarnessEvent ev) => Encoding.UTF8.GetByteCount(ev.Delta)
        + Encoding.UTF8.GetByteCount(ev.Arguments ?? string.Empty)
        + Encoding.UTF8.GetByteCount(ev.Result ?? string.Empty);

    private sealed record PreparedTurn(ChannelReader<GoldfishHarnessEvent> Subscription);

    private sealed class ActiveTurn(string queueKey)
    {
        private readonly object _gate = new();
        private readonly List<Channel<GoldfishHarnessEvent>> _subscribers = [];
        public string QueueKey { get; } = queueKey;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelReader<GoldfishHarnessEvent> Subscribe()
        {
            var channel = Channel.CreateBounded<GoldfishHarnessEvent>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            lock (_gate) _subscribers.Add(channel);
            return channel.Reader;
        }

        public async Task PublishAsync(GoldfishHarnessEvent ev, CancellationToken ct)
        {
            Channel<GoldfishHarnessEvent>[] subscribers;
            lock (_gate) subscribers = _subscribers.ToArray();
            foreach (var subscriber in subscribers)
                await subscriber.Writer.WriteAsync(ev, ct);
        }

        public void Complete()
        {
            lock (_gate)
            {
                foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
                _subscribers.Clear();
            }
            Completion.TrySetResult();
            Cancellation.Dispose();
        }
    }
}
