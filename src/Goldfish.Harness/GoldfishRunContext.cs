namespace Goldfish.Harness;

/// <summary>
/// 单次 Harness 运行的结构化上下文。集中替代散落在 AgentInfo.ExtraData 里的字符串读取，
/// 让 prompt 组装、工具加载、trace 都基于强类型字段，而不是到处 GetValueOrDefault。
/// </summary>
public sealed record GoldfishRunContext
{
    public string SessionId { get; init; } = string.Empty;
    public bool DisableConfigCache { get; init; } = true;
    public GoldfishUserContext User { get; init; } = new();
    public GoldfishAgentContext Agent { get; init; } = new();

    public static GoldfishRunContext FromAgentInfo(AgentInfo agentInfo, string sessionId, bool disableConfigCache = true)
    {
        var extra = agentInfo.ExtraData;

        string? Get(string key) => extra.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        string? First(params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = Get(key);
                if (value != null) return value;
            }
            return null;
        }

        return new GoldfishRunContext
        {
            SessionId = sessionId,
            DisableConfigCache = disableConfigCache,
            User = new GoldfishUserContext
            {
                // caller.username is the canonical User Center identity passed
                // through AgentNode.  It intentionally takes precedence over a
                // transport display name and never derives from an internal ID.
                Name = First("caller.username", "CallerUsername", "SenderName") ?? "未知用户",
                Id = Get("UserId") ?? "未知",
                Role = Get("UserRole") ?? "未知"
            },
            Agent = new GoldfishAgentContext
            {
                Name = string.IsNullOrWhiteSpace(agentInfo.Name) ? "Goldfish" : agentInfo.Name,
                Id = string.IsNullOrWhiteSpace(agentInfo.Id) ? null : agentInfo.Id,
                Type = agentInfo.AgentType,
                SystemPrompt = agentInfo.SystemPrompt ?? string.Empty,
                ProjectDirectory = First("ProjectDirectory", "ProjectPath", "ModelAgent")
            }
        };
    }
}

public sealed record GoldfishUserContext
{
    public string Name { get; init; } = "未知用户";
    public string Id { get; init; } = "未知";
    public string Role { get; init; } = "未知";
}

public sealed record GoldfishAgentContext
{
    public string Name { get; init; } = "Goldfish";
    public string? Id { get; init; }
    public string? Type { get; init; }
    public string? ProjectDirectory { get; init; }
    public string SystemPrompt { get; init; } = string.Empty;
}
