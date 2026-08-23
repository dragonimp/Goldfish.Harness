using System.Collections.Concurrent;
using System.Text.Json;

namespace Goldfish.Harness;

/// <summary>
/// A durable, host-neutral description of one agent turn.  Transport adapters
/// (ACP today, other transports later) consume events; they do not own turn
/// state or decide whether a run has finished.
/// </summary>
public enum GoldfishTurnStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record GoldfishHarnessTurn
{
    public string TurnId { get; init; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; init; } = string.Empty;
    public GoldfishTurnStatus Status { get; init; } = GoldfishTurnStatus.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? TerminalReason { get; init; }
}

public sealed record GoldfishHarnessTurnEvent(
    string TurnId,
    string SessionId,
    GoldfishHarnessEvent Event);

/// <summary>
/// The append-only audit boundary for a turn.  Implementations must preserve
/// terminal state instead of inferring completion from a closed stream.
/// </summary>
public interface IHarnessTurnEventStore
{
    Task StartAsync(GoldfishHarnessTurn turn, CancellationToken ct = default);
    Task AppendAsync(GoldfishHarnessTurnEvent turnEvent, CancellationToken ct = default);
    Task CompleteAsync(
        string turnId,
        GoldfishTurnStatus status,
        string? terminalReason = null,
        CancellationToken ct = default);
    Task<GoldfishHarnessTurn?> GetAsync(string turnId, CancellationToken ct = default);
    Task<IReadOnlyList<GoldfishHarnessTurn>> ListAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<GoldfishHarnessTurnEvent>> ReadEventsAsync(string turnId, CancellationToken ct = default);
}

public sealed class InMemoryHarnessTurnEventStore : IHarnessTurnEventStore
{
    private readonly ConcurrentDictionary<string, GoldfishHarnessTurn> _turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<GoldfishHarnessTurnEvent>> _events = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task StartAsync(GoldfishHarnessTurn turn, CancellationToken ct = default)
    {
        _turns[turn.TurnId] = turn with { Status = GoldfishTurnStatus.Running, StartedAt = DateTimeOffset.UtcNow };
        _events.TryAdd(turn.TurnId, []);
        return Task.CompletedTask;
    }

    public Task AppendAsync(GoldfishHarnessTurnEvent turnEvent, CancellationToken ct = default)
    {
        var events = _events.GetOrAdd(turnEvent.TurnId, _ => []);
        lock (_gate)
        {
            events.Add(turnEvent);
        }
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string turnId, GoldfishTurnStatus status, string? terminalReason = null, CancellationToken ct = default)
    {
        _turns.AddOrUpdate(
            turnId,
            _ => new GoldfishHarnessTurn
            {
                TurnId = turnId,
                Status = status,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                TerminalReason = terminalReason
            },
            (_, current) => current with
            {
                Status = status,
                CompletedAt = DateTimeOffset.UtcNow,
                TerminalReason = terminalReason
            });
        return Task.CompletedTask;
    }

    public Task<GoldfishHarnessTurn?> GetAsync(string turnId, CancellationToken ct = default)
        => Task.FromResult(_turns.TryGetValue(turnId, out var turn) ? turn : null);

    public Task<IReadOnlyList<GoldfishHarnessTurn>> ListAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GoldfishHarnessTurn>>(_turns.Values
            .Where(turn => string.Equals(turn.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(turn => turn.CreatedAt)
            .ToList());

    public Task<IReadOnlyList<GoldfishHarnessTurnEvent>> ReadEventsAsync(string turnId, CancellationToken ct = default)
    {
        if (!_events.TryGetValue(turnId, out var events)) return Task.FromResult<IReadOnlyList<GoldfishHarnessTurnEvent>>([]);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GoldfishHarnessTurnEvent>>(events.ToList());
        }
    }
}

/// <summary>
/// JSONL event ledger for a single Goldfish state root.  It deliberately keeps
/// event payloads append-only so hosts can replay ACP projections after a
/// process restart without treating EOF as a successful turn.
/// </summary>
public sealed class JsonlHarnessTurnEventStore : IHarnessTurnEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly InMemoryHarnessTurnEventStore _inner = new();
    private readonly string _ledgerPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlHarnessTurnEventStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        Directory.CreateDirectory(stateRoot);
        _ledgerPath = Path.Combine(stateRoot, "turn-events.jsonl");
    }

    public async Task StartAsync(GoldfishHarnessTurn turn, CancellationToken ct = default)
    {
        await _inner.StartAsync(turn, ct);
        await AppendLineAsync(new { type = "turn.started", turn }, ct);
    }

    public async Task AppendAsync(GoldfishHarnessTurnEvent turnEvent, CancellationToken ct = default)
    {
        await _inner.AppendAsync(turnEvent, ct);
        await AppendLineAsync(new { type = "turn.event", turnEvent }, ct);
    }

    public async Task CompleteAsync(string turnId, GoldfishTurnStatus status, string? terminalReason = null, CancellationToken ct = default)
    {
        await _inner.CompleteAsync(turnId, status, terminalReason, ct);
        await AppendLineAsync(new { type = "turn.completed", turnId, status, terminalReason, timestamp = DateTimeOffset.UtcNow }, ct);
    }

    public Task<GoldfishHarnessTurn?> GetAsync(string turnId, CancellationToken ct = default) => _inner.GetAsync(turnId, ct);
    public Task<IReadOnlyList<GoldfishHarnessTurn>> ListAsync(string sessionId, CancellationToken ct = default) => _inner.ListAsync(sessionId, ct);
    public Task<IReadOnlyList<GoldfishHarnessTurnEvent>> ReadEventsAsync(string turnId, CancellationToken ct = default) => _inner.ReadEventsAsync(turnId, ct);

    private async Task AppendLineAsync<T>(T entry, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_ledgerPath, line, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>
/// Owns the turn lifecycle around an execution strategy.  The current strategy
/// is GoldfishHarnessRunner; future planners or execution engines implement at
/// this boundary without leaking their protocol into ACP.
/// </summary>
public sealed class GoldfishHarnessKernel
{
    private readonly GoldfishHarnessRunner _runner;
    private readonly IHarnessTurnEventStore _eventStore;

    public GoldfishHarnessKernel(GoldfishHarnessRunner runner, IHarnessTurnEventStore? eventStore = null)
    {
        _runner = runner;
        _eventStore = eventStore ?? new InMemoryHarnessTurnEventStore();
    }

    public async IAsyncEnumerable<GoldfishHarnessEvent> StreamAsync(
        GoldfishHarnessRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var turn = new GoldfishHarnessTurn { SessionId = request.SessionId };
        await _eventStore.StartAsync(turn, ct);
        var terminalStatus = GoldfishTurnStatus.Failed;
        string? terminalReason = "Runner ended without a terminal event.";
        try
        {
            await foreach (var ev in _runner.StreamAsync(request, ct).WithCancellation(ct))
            {
                await _eventStore.AppendAsync(new GoldfishHarnessTurnEvent(turn.TurnId, request.SessionId, ev), ct);
                if (ev.Kind == GoldfishEventKind.Completed)
                {
                    terminalStatus = GoldfishTurnStatus.Completed;
                    terminalReason = ev.Delta;
                }
                else if (ev.Kind == GoldfishEventKind.Failed)
                {
                    terminalStatus = GoldfishTurnStatus.Failed;
                    terminalReason = ev.Delta;
                }
                yield return ev;
            }
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                terminalStatus = GoldfishTurnStatus.Canceled;
                terminalReason = "Turn canceled.";
            }
            await _eventStore.CompleteAsync(turn.TurnId, terminalStatus, terminalReason, CancellationToken.None);
        }
    }

    public async Task<GoldfishHarnessRunResult> RunAsync(GoldfishHarnessRequest request, CancellationToken ct = default)
    {
        var turn = new GoldfishHarnessTurn { SessionId = request.SessionId };
        await _eventStore.StartAsync(turn, ct);
        try
        {
            var result = await _runner.RunAsync(request, ct);
            foreach (var ev in result.Events)
            {
                await _eventStore.AppendAsync(new GoldfishHarnessTurnEvent(turn.TurnId, request.SessionId, ev), ct);
            }
            await _eventStore.CompleteAsync(turn.TurnId, GoldfishTurnStatus.Completed, result.Answer, ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _eventStore.CompleteAsync(turn.TurnId, GoldfishTurnStatus.Canceled, "Turn canceled.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _eventStore.CompleteAsync(turn.TurnId, GoldfishTurnStatus.Failed, ex.Message, CancellationToken.None);
            throw;
        }
    }
}
