using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Goldfish.Harness;

/// <summary>
/// Harness-owned, per-session execution queue. A session is FIFO and single-run;
/// different sessions are scheduled independently.
/// </summary>
public sealed class GoldfishSessionQueue
{
    private readonly Func<GoldfishHarnessRequest, CancellationToken, IAsyncEnumerable<GoldfishHarnessEvent>>? _defaultExecute;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public GoldfishSessionQueue() { }

    public GoldfishSessionQueue(GoldfishHarnessRunner runner)
        : this((request, ct) => runner.StreamAsync(request, ct)) { }

    public GoldfishSessionQueue(
        Func<GoldfishHarnessRequest, CancellationToken, IAsyncEnumerable<GoldfishHarnessEvent>> execute)
    {
        _defaultExecute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public GoldfishQueueSubmission Enqueue(GoldfishHarnessRequest request, string? messageId = null)
    {
        if (_defaultExecute is null)
            throw new InvalidOperationException("No default executor was configured. Use the Enqueue overload that supplies an executor.");
        return Enqueue(request, _defaultExecute, messageId);
    }

    /// <summary>
    /// Enqueues a request with its own executor. This allows a singleton queue to
    /// coordinate requests whose runner/model/tool registry is created per request.
    /// </summary>
    public GoldfishQueueSubmission Enqueue(
        GoldfishHarnessRequest request,
        Func<GoldfishHarnessRequest, CancellationToken, IAsyncEnumerable<GoldfishHarnessEvent>> execute,
        string? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execute);
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new ArgumentException("SessionId is required.", nameof(request));

        var state = _sessions.GetOrAdd(request.SessionId, static id => new SessionState(id));
        var item = new QueueItem(
            string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("n") : messageId,
            request, execute,
            Summarize(request.UserMessageText));
        int position;
        lock (state.Gate)
        {
            state.Waiting.Enqueue(item);
            position = state.Waiting.Count + (state.Active is null ? 0 : 1);
            if (!state.WorkerRunning)
            {
                state.WorkerRunning = true;
                _ = Task.Run(() => ConsumeAsync(state));
            }
        }

        return new GoldfishQueueSubmission(item.Id, request.SessionId, position, ReadEvents(item));
    }

    /// <summary>
    /// Enqueues a non-streaming Harness run in the same per-session FIFO used by
    /// streaming submissions. The caller token cancels this queued/running item,
    /// not the session or any neighboring item.
    /// </summary>
    public Task<GoldfishHarnessRunResult> EnqueueRunAsync(
        GoldfishHarnessRequest request,
        Func<GoldfishHarnessRequest, CancellationToken, Task<GoldfishHarnessRunResult>> execute,
        string? messageId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execute);
        var id = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("n") : messageId;
        var completion = new TaskCompletionSource<GoldfishHarnessRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        GoldfishHarnessRunResult? completedResult = null;

        var submission = Enqueue(request, ExecuteAsStream, id);
        var registration = ct.CanBeCanceled
            ? ct.Register(() => Cancel(request.SessionId, id))
            : default;
        _ = ObserveCompletionAsync(submission.Events, completion, () => completedResult, registration);
        return completion.Task;

        async IAsyncEnumerable<GoldfishHarnessEvent> ExecuteAsStream(
            GoldfishHarnessRequest queuedRequest,
            [EnumeratorCancellation] CancellationToken runCt)
        {
            var result = await execute(queuedRequest, runCt).ConfigureAwait(false);
            completedResult = result;
            foreach (var ev in result.Events) yield return ev;
        }
    }

    public Task<GoldfishHarnessRunResult> EnqueueRunAsync(
        GoldfishHarnessRequest request,
        GoldfishHarnessRunner runner,
        string? messageId = null,
        CancellationToken ct = default) =>
        EnqueueRunAsync(request, runner.RunAsync, messageId, ct);

    public GoldfishSessionQueueSnapshot GetStatus(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return new(sessionId, null, []);

        lock (state.Gate)
        {
            return new GoldfishSessionQueueSnapshot(
                sessionId,
                state.Active is null ? null : Describe(state.Active),
                state.Waiting.Select(Describe).ToArray());
        }
    }

    public bool Cancel(string sessionId, string messageId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state)) return false;
        lock (state.Gate)
        {
            if (state.Active?.Id == messageId)
            {
                state.Active.Cancellation.Cancel();
                return true;
            }

            QueueItem? removed = null;
            var retained = new Queue<QueueItem>();
            while (state.Waiting.TryDequeue(out var item))
            {
                if (removed is null && item.Id == messageId) removed = item;
                else retained.Enqueue(item);
            }
            while (retained.TryDequeue(out var item)) state.Waiting.Enqueue(item);
            if (removed is null) return false;
            removed.Events.Writer.TryComplete(new OperationCanceledException("Queued run was cancelled."));
            return true;
        }
    }

    private async Task ConsumeAsync(SessionState state)
    {
        while (true)
        {
            QueueItem item;
            lock (state.Gate)
            {
                if (!state.Waiting.TryDequeue(out item!))
                {
                    state.WorkerRunning = false;
                    state.Active = null;
                    _sessions.TryRemove(new KeyValuePair<string, SessionState>(state.SessionId, state));
                    return;
                }
                state.Active = item;
            }

            try
            {
                await foreach (var ev in item.Execute(item.Request, item.Cancellation.Token)
                    .WithCancellation(item.Cancellation.Token).ConfigureAwait(false))
                    await item.Events.Writer.WriteAsync(ev, CancellationToken.None).ConfigureAwait(false);
                item.Events.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (item.Cancellation.IsCancellationRequested)
            {
                item.Events.Writer.TryComplete(new OperationCanceledException("Run was cancelled."));
            }
            catch (Exception ex)
            {
                item.Events.Writer.TryComplete(ex);
            }
            finally
            {
                item.Cancellation.Dispose();
                lock (state.Gate)
                    if (ReferenceEquals(state.Active, item)) state.Active = null;
            }
        }
    }

    private static async IAsyncEnumerable<GoldfishHarnessEvent> ReadEvents(
        QueueItem item, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in item.Events.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return ev;
    }

    private static async Task ObserveCompletionAsync(
        IAsyncEnumerable<GoldfishHarnessEvent> events,
        TaskCompletionSource<GoldfishHarnessRunResult> completion,
        Func<GoldfishHarnessRunResult?> getResult,
        CancellationTokenRegistration registration)
    {
        try
        {
            await foreach (var _ in events.ConfigureAwait(false)) { }
            var result = getResult();
            if (result is not null) completion.TrySetResult(result);
            else
                completion.TrySetException(new InvalidOperationException("Queued run completed without a result."));
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private static GoldfishQueuedMessage Describe(QueueItem item) =>
        new(item.Id, item.Summary, item.EnqueuedAt);

    private static string Summarize(string text)
    {
        var normalized = string.Join(' ', (text ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..117] + "...";
    }

    private sealed class SessionState(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public object Gate { get; } = new();
        public Queue<QueueItem> Waiting { get; } = new();
        public QueueItem? Active { get; set; }
        public bool WorkerRunning { get; set; }
    }

    private sealed class QueueItem(
        string id,
        GoldfishHarnessRequest request,
        Func<GoldfishHarnessRequest, CancellationToken, IAsyncEnumerable<GoldfishHarnessEvent>> execute,
        string summary)
    {
        public string Id { get; } = id;
        public GoldfishHarnessRequest Request { get; } = request;
        public Func<GoldfishHarnessRequest, CancellationToken, IAsyncEnumerable<GoldfishHarnessEvent>> Execute { get; } = execute;
        public string Summary { get; } = summary;
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        public Channel<GoldfishHarnessEvent> Events { get; } = Channel.CreateUnbounded<GoldfishHarnessEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    }
}

public sealed record GoldfishQueueSubmission(
    string MessageId,
    string SessionId,
    int Position,
    IAsyncEnumerable<GoldfishHarnessEvent> Events);

public sealed record GoldfishSessionQueueSnapshot(
    string SessionId,
    GoldfishQueuedMessage? Running,
    IReadOnlyList<GoldfishQueuedMessage> Waiting)
{
    public int WaitingCount => Waiting.Count;
}

public sealed record GoldfishQueuedMessage(string MessageId, string Summary, DateTimeOffset EnqueuedAt);
