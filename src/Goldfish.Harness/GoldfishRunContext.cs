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

    /// <summary>
    /// 网关来源信息。仅当请求确实来自网关通道时非空。
    /// 注意：网关路由说明已由 GatewayServer 注入系统提示词（Instructions），
    /// 此处保留结构化字段供 trace / 工具 / 后续逻辑使用，不再二次渲染进 prompt。
    /// </summary>
    public GoldfishGatewayContext? Gateway { get; init; }

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

        var gateway = extra.Keys.Any(k => k.StartsWith("Gateway", StringComparison.OrdinalIgnoreCase))
            ? new GoldfishGatewayContext
            {
                ChannelId = Get("GatewayChannelId"),
                ChannelName = Get("GatewayChannelName"),
                Type = Get("GatewayType"),
                BotId = Get("GatewayBotId"),
                AppId = Get("GatewayAppId"),
                Sender = Get("GatewaySender"),
                ConversationId = Get("GatewayConversationId")
            }
            : null;

        return new GoldfishRunContext
        {
            SessionId = sessionId,
            DisableConfigCache = disableConfigCache,
            User = new GoldfishUserContext
            {
                Name = Get("SenderName") ?? "未知用户",
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
            },
            Gateway = gateway
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

public sealed record GoldfishGatewayContext
{
    public string? ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public string? Type { get; init; }
    public string? BotId { get; init; }
    public string? AppId { get; init; }
    public string? Sender { get; init; }
    public string? ConversationId { get; init; }
}
