namespace Goldfish.Harness;

/// <summary>
/// 提示词构建器 — 构建发送给 LLM 的完整提示词
/// </summary>
public interface IPromptBuilder
{
    /// <summary>
    /// 构建系统提示词
    /// </summary>
    string BuildSystemPrompt(AgentInfo agent);

    /// <summary>
    /// 构建完整消息列表
    /// </summary>
    IList<ChatMessage> BuildMessages(
        AgentInfo agent,
        string userPrompt,
        IList<ChatMessage> history,
        IList<ITool> tools);

    /// <summary>
    /// 基于分层记忆构建完整消息列表。
    /// </summary>
    IList<ChatMessage> BuildMessages(
        AgentInfo agent,
        string userPrompt,
        MemoryContext memoryContext,
        IList<ITool> tools);

    /// <summary>
    /// 添加工具调用结果到消息列表
    /// </summary>
    void AddToolResult(IList<ChatMessage> messages, ToolCallRecord record);
}

/// <summary>
/// 提示词构建器实现
/// </summary>
public class PromptBuilder : IPromptBuilder
{
    public string BuildSystemPrompt(AgentInfo agent)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(agent.SystemPrompt ?? "You are a helpful AI assistant.");
        sb.AppendLine();
        sb.AppendLine("If tools are available, use the model's native tools/function calling interface. Do not list or invoke tools by writing tool JSON in the prompt text.");

        return sb.ToString().TrimEnd();
    }

    public IList<ChatMessage> BuildMessages(
        AgentInfo agent,
        string userPrompt,
        IList<ChatMessage> history,
        IList<ITool> tools)
        => BuildMessages(agent, userPrompt, MemoryContext.FromHistory(history), tools);

    public IList<ChatMessage> BuildMessages(
        AgentInfo agent,
        string userPrompt,
        MemoryContext memoryContext,
        IList<ITool> tools)
    {
        var messages = new List<ChatMessage>();

        var systemPrompt = BuildSystemPrompt(agent);
        var memoryPrompt = BuildMemoryPrompt(memoryContext);
        if (!string.IsNullOrWhiteSpace(memoryPrompt))
        {
            systemPrompt = $"{systemPrompt.TrimEnd()}\n\n{memoryPrompt}";
        }

        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = systemPrompt
        });

        foreach (var msg in memoryContext.ShortTermMessages)
        {
            messages.Add(new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ToolCallId = msg.ToolCallId
            });
        }

        // 添加用户消息
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = userPrompt
        });

        return messages;
    }

    private static string BuildMemoryPrompt(MemoryContext memoryContext)
    {
        var sb = new System.Text.StringBuilder();

        if (memoryContext.LongTermMemories.Count > 0)
        {
            sb.AppendLine("## 长期记忆");
            sb.AppendLine("以下是跨会话保留的用户偏好、事实或稳定背景。只在相关时使用，不要把记忆内容原样复述给用户。");
            foreach (var memory in memoryContext.LongTermMemories)
            {
                sb.AppendLine($"- [{memory.Type}{FormatCategory(memory.Category)}] {memory.Content}");
            }
            sb.AppendLine();
        }

        if (memoryContext.MediumTermMemories.Count > 0)
        {
            sb.AppendLine("## 中期记忆");
            sb.AppendLine("以下是当前会话较早内容的压缩摘要，用于延续上下文；最近对话会以普通消息形式附加。");
            foreach (var memory in memoryContext.MediumTermMemories.OrderBy(m => m.CreatedAt))
            {
                sb.AppendLine($"- {memory.Content}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? string.Empty : $"/{category}";

    public void AddToolResult(IList<ChatMessage> messages, ToolCallRecord record)
    {
        messages.Add(new ChatMessage
        {
            Role = "tool",
            Content = record.Success ? record.Result : $"Error: {record.Result}",
            ToolCallId = string.IsNullOrWhiteSpace(record.ToolCallId) ? record.ToolId : record.ToolCallId
        });
    }
}
