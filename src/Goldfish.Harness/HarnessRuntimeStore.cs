using System.Text.Json;

namespace Goldfish.Harness;

public enum HarnessStateMode
{
    Jsonl,
    Dual,
    Sqlite
}

public sealed record HarnessStateOptions
{
    public HarnessStateMode Mode { get; init; } = HarnessStateMode.Sqlite;
    public int RetentionDays { get; init; } = 30;
    public int DeltaBatchMilliseconds { get; init; } = 50;
    public int DeltaBatchBytes { get; init; } = 4096;
    public int LeaseSeconds { get; init; } = 30;
    public int InlinePayloadBytes { get; init; } = 256 * 1024;
}

public sealed record GoldfishTurnPartition(
    string TenantId,
    string UserId,
    string AgentId,
    string WorkspaceId,
    string SessionId)
{
    public static GoldfishTurnPartition From(MemoryPartition partition) => new(
        partition.TenantId,
        partition.UserId,
        partition.AgentId,
        partition.WorkspaceId,
        partition.SessionId ?? string.Empty);

    public string QueueKey => string.Join("\u001f", TenantId, UserId, AgentId, WorkspaceId, SessionId);
}

public sealed record HarnessTurnCreateResult(GoldfishHarnessTurn Turn, bool Created);

public interface IHarnessRuntimeStore
{
    int SchemaVersion { get; }

    Task<HarnessTurnCreateResult> GetOrCreateTurnAsync(
        GoldfishHarnessTurn turn,
        string userMessage,
        CancellationToken ct = default);

    Task<bool> TryStartAsync(
        string turnId,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default);

    Task AppendEventsAsync(
        string turnId,
        string sessionId,
        IReadOnlyList<GoldfishHarnessEvent> events,
        CancellationToken ct = default);

    Task<bool> TryCompleteAsync(
        string turnId,
        GoldfishTurnStatus status,
        string? terminalReasonCode,
        string? terminalReason,
        string? assistantMessage,
        CancellationToken ct = default);

    Task<bool> TryCompleteWithEventAsync(
        string turnId,
        string sessionId,
        GoldfishHarnessEvent terminalEvent,
        GoldfishTurnStatus status,
        string? terminalReasonCode,
        string? terminalReason,
        string? assistantMessage,
        CancellationToken ct = default);

    Task HeartbeatAsync(
        string turnId,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default);

    Task<GoldfishHarnessTurn?> GetTurnAsync(string turnId, CancellationToken ct = default);
    Task<GoldfishHarnessTurn?> GetByRequestAsync(GoldfishTurnPartition partition, string requestId, CancellationToken ct = default);
    Task<IReadOnlyList<GoldfishHarnessTurnEvent>> ReadEventsAsync(string turnId, CancellationToken ct = default);
    Task<int> RecoverOrphanedAsync(DateTimeOffset now, CancellationToken ct = default);
    Task ResetSessionAsync(GoldfishTurnPartition partition, CancellationToken ct = default);
    Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

internal static class HarnessRuntimeJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
