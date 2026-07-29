namespace Goldfish.Harness;

/// <summary>
/// 智能体核心接口 — 封装完整的 agentic loop
/// </summary>
public interface IAgent
{
    /// <summary>
    /// 智能体标识
    /// </summary>
    AgentInfo Identity { get; }

    /// <summary>
    /// 执行智能体任务
    /// </summary>
    /// <param name="prompt">用户输入/任务描述</param>
    /// <param name="context">上下文信息（可选）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>智能体的完整响应（含工具调用历史）</returns>
    Task<AgentResponse> ExecuteAsync(
        string prompt,
        AgentContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取会话历史
    /// </summary>
    Task<IList<ChatMessage>> GetHistoryAsync(string sessionId);

    /// <summary>
    /// 重置会话
    /// </summary>
    Task ResetSessionAsync(string sessionId);
}

/// <summary>
/// 智能体信息
/// </summary>
public class AgentInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AgentType { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public ICollection<string> ToolIds { get; set; } = new List<string>();
    public Dictionary<string, string> ExtraData { get; set; } = new();
}

/// <summary>
/// 智能体上下文
/// </summary>
public class AgentContext
{
    public string? SessionId { get; set; }
    public string? TenantId { get; set; }
    public string? UserId { get; set; }
    public string? AgentId { get; set; }
    public string? WorkspaceId { get; set; }
    public Dictionary<string, object> Variables { get; set; } = new();
    public DateTime? Timestamp { get; set; }
}

/// <summary>
/// 智能体响应
/// </summary>
public class AgentResponse
{
    public string Content { get; set; } = string.Empty;
    public List<ToolCallRecord> ToolCalls { get; set; } = new();
    public List<ChatMessage> Messages { get; set; } = new();
}

/// <summary>
/// 工具调用记录
/// </summary>
public class ToolCallRecord
{
    /// <summary>模型/协议层的工具调用 ID，用于把开始、参数、结果事件串成同一次调用。</summary>
    public string? ToolCallId { get; set; }
    public string ToolId { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public bool Success { get; set; }

    /// <summary>工具自带的可读展示文本（envelope.displayText）。为空时由 Result 反向格式化。</summary>
    public string? DisplayText { get; set; }

    /// <summary>工具返回的附件对象，由宿主转换为对应的附件事件。</summary>
    public IReadOnlyList<object>? Attachments { get; set; }
}

/// <summary>
/// 聊天消息（标准格式）
/// </summary>
public class ChatMessage
{
    public string Role { get; set; } = string.Empty; // system/user/assistant/tool
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
