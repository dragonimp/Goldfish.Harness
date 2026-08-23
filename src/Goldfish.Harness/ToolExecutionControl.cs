using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Goldfish.Harness;

public enum ToolAuthorizationDecision
{
    Allow,
    Deny,
    RequireApproval
}

public sealed record ToolAuthorizationRequest
{
    public string RunId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? TenantId { get; init; }
    public string? UserId { get; init; }
    public string? AgentId { get; init; }
    public string? WorkspaceId { get; init; }
    public string ToolId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Arguments { get; init; } = "{}";
}

public sealed record ToolAuthorizationResult
{
    public ToolAuthorizationDecision Decision { get; init; } = ToolAuthorizationDecision.Allow;
    public string? Reason { get; init; }
    public string? ApprovalRequestId { get; init; }

    public static ToolAuthorizationResult Allow() => new()
    {
        Decision = ToolAuthorizationDecision.Allow
    };

    public static ToolAuthorizationResult Deny(string reason) => new()
    {
        Decision = ToolAuthorizationDecision.Deny,
        Reason = reason
    };

    public static ToolAuthorizationResult RequireApproval(string approvalRequestId, string? reason = null) => new()
    {
        Decision = ToolAuthorizationDecision.RequireApproval,
        ApprovalRequestId = approvalRequestId,
        Reason = reason
    };
}

public interface IToolAuthorizationHook
{
    ValueTask<ToolAuthorizationResult> AuthorizeAsync(ToolAuthorizationRequest request, CancellationToken ct = default);
}

public sealed class AllowAllToolAuthorizationHook : IToolAuthorizationHook
{
    public static AllowAllToolAuthorizationHook Instance { get; } = new();

    private AllowAllToolAuthorizationHook()
    {
    }

    public ValueTask<ToolAuthorizationResult> AuthorizeAsync(
        ToolAuthorizationRequest request,
        CancellationToken ct = default)
        => ValueTask.FromResult(ToolAuthorizationResult.Allow());
}

public sealed record ToolExecutionRecord
{
    public string? ExecutionId { get; init; }
    public string? TurnId { get; init; }
    public string RunId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? TenantId { get; init; }
    public string? UserId { get; init; }
    public string? AgentId { get; init; }
    public string? WorkspaceId { get; init; }
    public int Step { get; init; }
    public string? ToolCallId { get; init; }
    public string ToolId { get; init; } = string.Empty;
    public string ArgumentsHash { get; init; } = string.Empty;
    public string? ResultHash { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? ResultJson { get; init; }
    public string? StructuredContentJson { get; init; }
    public bool? IsError { get; init; }
    public string Status { get; init; } = "Completed";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string AuthorizationDecision { get; init; } = ToolAuthorizationDecision.Allow.ToString();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static partial class HarnessSensitiveData
{
    [GeneratedRegex("(?i)(\\\"(?:api[_-]?key|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|authorization[_-]?code)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")")]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+\\-/]+=*")]
    private static partial Regex BearerRegex();

    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = JsonSecretRegex().Replace(value, "$1[REDACTED]$2");
        return BearerRegex().Replace(redacted, "Bearer [REDACTED]");
    }
}

public interface IToolExecutionStore
{
    Task RecordAsync(ToolExecutionRecord record, CancellationToken ct = default);
}

public sealed class NullToolExecutionStore : IToolExecutionStore
{
    public static NullToolExecutionStore Instance { get; } = new();

    private NullToolExecutionStore()
    {
    }

    public Task RecordAsync(ToolExecutionRecord record, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class InMemoryToolExecutionStore : IToolExecutionStore
{
    private readonly List<ToolExecutionRecord> _records = [];
    private readonly object _lock = new();

    public IReadOnlyList<ToolExecutionRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return _records.ToList();
            }
        }
    }

    public Task RecordAsync(ToolExecutionRecord record, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var existing = string.IsNullOrWhiteSpace(record.ExecutionId)
                ? -1
                : _records.FindIndex(item => string.Equals(item.ExecutionId, record.ExecutionId, StringComparison.Ordinal));
            if (existing >= 0) _records[existing] = record;
            else _records.Add(record);
        }

        return Task.CompletedTask;
    }
}

public static class ToolExecutionHash
{
    public static string Sha256(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
