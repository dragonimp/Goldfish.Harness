using System.Collections.Concurrent;

namespace Goldfish.Harness;

public static class HarnessRuntimeStoreFactory
{
    public static IHarnessRuntimeStore Create(string stateRoot, HarnessStateOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        Directory.CreateDirectory(stateRoot);
        return options.Mode switch
        {
            HarnessStateMode.Jsonl => new JsonlHarnessRuntimeStore(stateRoot),
            HarnessStateMode.Dual => new DualHarnessRuntimeStore(
                new SqliteHarnessStateStore(Path.Combine(stateRoot, "harness-state.db"), options),
                new JsonlHarnessTurnEventStore(stateRoot)),
            _ => new SqliteHarnessStateStore(Path.Combine(stateRoot, "harness-state.db"), options)
        };
    }
}

public sealed class DualHarnessRuntimeStore(
    IHarnessRuntimeStore primary,
    IHarnessTurnEventStore legacy) : IHarnessRuntimeStore, IDisposable
{
    public int SchemaVersion => primary.SchemaVersion;

    public async Task<HarnessTurnCreateResult> GetOrCreateTurnAsync(GoldfishHarnessTurn turn, string userMessage, CancellationToken ct = default)
    {
        var result = await primary.GetOrCreateTurnAsync(turn, userMessage, ct);
        if (result.Created) await legacy.StartAsync(turn, ct);
        return result;
    }

    public Task<bool> TryStartAsync(string turnId, string leaseOwner, DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
        => primary.TryStartAsync(turnId, leaseOwner, leaseExpiresAt, ct);

    public async Task AppendEventsAsync(string turnId, string sessionId, IReadOnlyList<GoldfishHarnessEvent> events, CancellationToken ct = default)
    {
        await primary.AppendEventsAsync(turnId, sessionId, events, ct);
        foreach (var ev in events) await legacy.AppendAsync(new GoldfishHarnessTurnEvent(turnId, sessionId, ev), ct);
    }

    public async Task<bool> TryCompleteAsync(string turnId, GoldfishTurnStatus status, string? terminalReasonCode,
        string? terminalReason, string? assistantMessage, CancellationToken ct = default)
    {
        var changed = await primary.TryCompleteAsync(turnId, status, terminalReasonCode, terminalReason, assistantMessage, ct);
        if (changed) await legacy.CompleteAsync(turnId, status, terminalReason, ct);
        return changed;
    }

    public async Task<bool> TryCompleteWithEventAsync(string turnId, string sessionId, GoldfishHarnessEvent terminalEvent,
        GoldfishTurnStatus status, string? terminalReasonCode, string? terminalReason, string? assistantMessage,
        CancellationToken ct = default)
    {
        var changed = await primary.TryCompleteWithEventAsync(turnId, sessionId, terminalEvent, status,
            terminalReasonCode, terminalReason, assistantMessage, ct);
        if (changed)
        {
            await legacy.AppendAsync(new GoldfishHarnessTurnEvent(turnId, sessionId, terminalEvent), ct);
            await legacy.CompleteAsync(turnId, status, terminalReason, ct);
        }
        return changed;
    }

    public Task HeartbeatAsync(string turnId, string leaseOwner, DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
        => primary.HeartbeatAsync(turnId, leaseOwner, leaseExpiresAt, ct);
    public Task<GoldfishHarnessTurn?> GetTurnAsync(string turnId, CancellationToken ct = default) => primary.GetTurnAsync(turnId, ct);
    public Task<GoldfishHarnessTurn?> GetByRequestAsync(GoldfishTurnPartition partition, string requestId, CancellationToken ct = default)
        => primary.GetByRequestAsync(partition, requestId, ct);
    public Task<IReadOnlyList<GoldfishHarnessTurnEvent>> ReadEventsAsync(string turnId, CancellationToken ct = default)
        => primary.ReadEventsAsync(turnId, ct);
    public Task<int> RecoverOrphanedAsync(DateTimeOffset now, CancellationToken ct = default) => primary.RecoverOrphanedAsync(now, ct);
    public Task ResetSessionAsync(GoldfishTurnPartition partition, CancellationToken ct = default) => primary.ResetSessionAsync(partition, ct);
    public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken ct = default) => primary.CleanupAsync(cutoff, ct);

    public void Dispose()
    {
        if (primary is IDisposable disposable) disposable.Dispose();
    }
}

public sealed class JsonlHarnessRuntimeStore : IHarnessRuntimeStore
{
    private readonly JsonlHarnessTurnEventStore _events;
    private readonly ConcurrentDictionary<string, GoldfishHarnessTurn> _turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _requestIndex = new(StringComparer.Ordinal);

    public JsonlHarnessRuntimeStore(string stateRoot) => _events = new JsonlHarnessTurnEventStore(stateRoot);
    public int SchemaVersion => 0;

    public async Task<HarnessTurnCreateResult> GetOrCreateTurnAsync(GoldfishHarnessTurn turn, string userMessage, CancellationToken ct = default)
    {
        var key = $"{turn.Partition.QueueKey}\u001f{turn.RequestId}";
        if (_requestIndex.TryGetValue(key, out var existingId) && _turns.TryGetValue(existingId, out var existing))
            return new HarnessTurnCreateResult(existing, false);
        if (!_requestIndex.TryAdd(key, turn.TurnId))
            return new HarnessTurnCreateResult(_turns[_requestIndex[key]], false);
        _turns[turn.TurnId] = turn;
        await _events.StartAsync(turn, ct);
        return new HarnessTurnCreateResult(turn, true);
    }

    public Task<bool> TryStartAsync(string turnId, string leaseOwner, DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
    {
        if (!_turns.TryGetValue(turnId, out var turn) || turn.Status != GoldfishTurnStatus.Queued) return Task.FromResult(false);
        _turns[turnId] = turn with { Status = GoldfishTurnStatus.Running, LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt, StartedAt = DateTimeOffset.UtcNow, Version = turn.Version + 1 };
        return Task.FromResult(true);
    }

    public async Task AppendEventsAsync(string turnId, string sessionId, IReadOnlyList<GoldfishHarnessEvent> events, CancellationToken ct = default)
    {
        foreach (var ev in events) await _events.AppendAsync(new GoldfishHarnessTurnEvent(turnId, sessionId, ev), ct);
    }

    public async Task<bool> TryCompleteAsync(string turnId, GoldfishTurnStatus status, string? terminalReasonCode,
        string? terminalReason, string? assistantMessage, CancellationToken ct = default)
    {
        if (!_turns.TryGetValue(turnId, out var turn) || turn.IsTerminal) return false;
        _turns[turnId] = turn with { Status = status, TerminalReasonCode = terminalReasonCode,
            TerminalReason = terminalReason, CompletedAt = DateTimeOffset.UtcNow, Version = turn.Version + 1 };
        await _events.CompleteAsync(turnId, status, terminalReason, ct);
        return true;
    }

    public async Task<bool> TryCompleteWithEventAsync(string turnId, string sessionId, GoldfishHarnessEvent terminalEvent,
        GoldfishTurnStatus status, string? terminalReasonCode, string? terminalReason, string? assistantMessage,
        CancellationToken ct = default)
    {
        if (!_turns.TryGetValue(turnId, out var turn) || turn.IsTerminal) return false;
        await _events.AppendAsync(new GoldfishHarnessTurnEvent(turnId, sessionId, terminalEvent), ct);
        return await TryCompleteAsync(turnId, status, terminalReasonCode, terminalReason, assistantMessage, ct);
    }

    public Task HeartbeatAsync(string turnId, string leaseOwner, DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
    {
        if (_turns.TryGetValue(turnId, out var turn) && turn.Status == GoldfishTurnStatus.Running)
            _turns[turnId] = turn with { HeartbeatAt = DateTimeOffset.UtcNow, LeaseExpiresAt = leaseExpiresAt };
        return Task.CompletedTask;
    }

    public Task<GoldfishHarnessTurn?> GetTurnAsync(string turnId, CancellationToken ct = default)
        => Task.FromResult(_turns.TryGetValue(turnId, out var turn) ? turn : null);
    public Task<GoldfishHarnessTurn?> GetByRequestAsync(GoldfishTurnPartition partition, string requestId, CancellationToken ct = default)
    {
        var key = $"{partition.QueueKey}\u001f{requestId}";
        return Task.FromResult(_requestIndex.TryGetValue(key, out var id) && _turns.TryGetValue(id, out var turn) ? turn : null);
    }
    public Task<IReadOnlyList<GoldfishHarnessTurnEvent>> ReadEventsAsync(string turnId, CancellationToken ct = default)
        => _events.ReadEventsAsync(turnId, ct);
    public Task<int> RecoverOrphanedAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);
    public Task ResetSessionAsync(GoldfishTurnPartition partition, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.FromResult(0);
}
