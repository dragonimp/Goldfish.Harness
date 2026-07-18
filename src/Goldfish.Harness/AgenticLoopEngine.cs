using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Goldfish.Harness;

/// <summary>
/// Agentic Loop 引擎 — 实现 LLM 调用 → 工具执行 → 反馈循环
/// </summary>
public class AgenticLoopEngine : IAgent
{
    private readonly IChatClient _chatClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly IMemoryManager _memoryManager;
    private readonly IPromptBuilder _promptBuilder;
    private readonly ILogger<AgenticLoopEngine> _logger;
    private readonly AgentInfo _agentInfo;
    private readonly MemoryOptions _memoryOptions;
    private readonly int _maxLoops;

    public AgenticLoopEngine(
        AgentInfo agentInfo,
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        IMemoryManager memoryManager,
        IPromptBuilder promptBuilder,
        ILogger<AgenticLoopEngine>? logger = null,
        MemoryOptions? memoryOptions = null,
        int maxLoops = 10)
    {
        _agentInfo = agentInfo;
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _memoryManager = memoryManager;
        _promptBuilder = promptBuilder;
        _logger = logger ?? NullLogger<AgenticLoopEngine>.Instance;
        _memoryOptions = memoryOptions ?? MemoryOptions.Default;
        _maxLoops = maxLoops;
    }

    public AgentInfo Identity => _agentInfo;

    public async Task<AgentResponse> ExecuteAsync(
        string prompt,
        AgentContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = context?.SessionId ?? Guid.NewGuid().ToString("N");
        var memoryPartition = new MemoryPartition
        {
            TenantId = context?.TenantId ?? string.Empty,
            UserId = context?.UserId ?? string.Empty,
            AgentId = context?.AgentId ?? _agentInfo.Id,
            WorkspaceId = context?.WorkspaceId ?? string.Empty,
            SessionId = sessionId
        };
        var response = new AgentResponse
        {
            Content = string.Empty
        };
        var loopCount = 0;
        var memoryContext = await _memoryManager.BuildContextAsync(memoryPartition, prompt, _memoryOptions);
        IList<ChatMessage> localMessages = _promptBuilder.BuildMessages(
            _agentInfo,
            prompt,
            memoryContext,
            _toolRegistry.GetAll());

        while (loopCount < _maxLoops)
        {
            loopCount++;
            _logger.LogInformation(
                "Agentic loop iteration {Loop} for agent {AgentId}",
                loopCount, _agentInfo.Id);

            // Convert local ChatMessage[] to MS.AI ChatMessage[]
            var msMessages = localMessages.Select(m =>
                new Microsoft.Extensions.AI.ChatMessage(ParseChatRole(m.Role), m.Content)
            ).ToList();

            // Build ChatOptions
            var chatOptions = new ChatOptions
            {
                MaxOutputTokens = 2048,
                Temperature = 0.7f
            };

            // Register tools as AITools (AIFunction)
            var tools = _toolRegistry.GetAll();
            if (tools.Any())
            {
                var aiTools = tools
                    .Select(CreateAITool)
                    .OfType<AITool>()
                    .ToList();
                chatOptions.Tools = aiTools;
                chatOptions.ToolMode = ChatToolMode.Auto;
            }

            var responseChat = await _chatClient.GetResponseAsync(
                msMessages,
                chatOptions,
                cancellationToken);

            var assistantText = responseChat.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                assistantText = "[No response from model]";
            }

            _logger.LogDebug(
                "Agentic loop response: {Text}",
                assistantText[..Math.Min(200, assistantText.Length)]);

            // 检查响应中是否有工具调用（标记格式: {{TOOL_CALL:toolId:arguments}}）
            var toolCalls = ParseToolCalls(assistantText);
            if (!toolCalls.Any())
            {
                // 没有工具调用，完成响应
                response.Content = assistantText;
                localMessages.Add(new Goldfish.Harness.ChatMessage
                {
                    Role = "assistant",
                    Content = assistantText
                });
                break;
            }

            // 执行工具调用
            foreach (var toolCall in toolCalls)
            {
                var toolId = toolCall.ToolId;
                var toolArgs = toolCall.Arguments;

                _logger.LogInformation(
                    "Executing tool {ToolId} with args: {Args}",
                    toolId, toolArgs);

                var toolResult = await _toolRegistry.ExecuteAsync(toolId, toolArgs);

                var record = new ToolCallRecord
                {
                    ToolId = toolId,
                    Arguments = toolArgs,
                    Result = toolResult.Success
                        ? (toolResult.Data?.ToString() ?? "Success")
                        : (toolResult.Error ?? "Unknown error"),
                    Success = toolResult.Success
                };

                response.ToolCalls.Add(record);
                _promptBuilder.AddToolResult(localMessages, record);

                if (!toolResult.Success)
                {
                    _logger.LogWarning(
                        "Tool execution failed for {ToolId}: {Error}",
                        toolId, toolResult.Error);
                }
            }

            // 将助手消息添加到 messages
            localMessages.Add(new Goldfish.Harness.ChatMessage
            {
                Role = "assistant",
                Content = assistantText
            });
        }

        await SaveTurnAsync(memoryPartition, prompt, response.Content);

        response.Messages = localMessages.ToList();
        return response;
    }

    public Task<IList<ChatMessage>> GetHistoryAsync(string sessionId)
    {
        return _memoryManager.GetHistoryAsync(sessionId);
    }

    public Task ResetSessionAsync(string sessionId)
    {
        return _memoryManager.DeleteSessionAsync(sessionId);
    }

    private async Task SaveTurnAsync(
        MemoryPartition partition,
        string userPrompt,
        string assistantContent)
    {
        if (!_memoryOptions.ShortTerm.Enabled && !_memoryOptions.MediumTerm.Enabled)
        {
            return;
        }

        await _memoryManager.AddMessageAsync(partition, new Goldfish.Harness.ChatMessage
        {
            Role = "user",
            Content = userPrompt
        });

        if (!string.IsNullOrWhiteSpace(assistantContent))
        {
            await _memoryManager.AddMessageAsync(partition, new Goldfish.Harness.ChatMessage
            {
                Role = "assistant",
                Content = assistantContent
            });
        }

        // 压缩在下一轮 BuildContextAsync 时按阈值触发，避免刚保存少量消息就过早摘要化。
    }

    /// <summary>
    /// 将字符串角色名转换为 ChatRole
    /// </summary>
    private static ChatRole ParseChatRole(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "system" => ChatRole.System,
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User
        };
    }

    /// <summary>
    /// 从响应文本中解析工具调用
    /// 简单的标记格式: {{TOOL_CALL:toolId:arguments}}
    /// </summary>
    private static IEnumerable<(string ToolId, string Arguments)> ParseToolCalls(string text)
    {
        var results = new List<(string, string)>();
        var pattern = @"\{\{TOOL_CALL:([^:]+):(.+?)\}\}";
        var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var toolId = match.Groups[1].Value;
            var args = match.Groups[2].Value;
            results.Add((toolId, args));
        }

        return results;
    }

    /// <summary>
    /// 将 ITool 转换为 AITool (AIFunction)
    /// </summary>
    private AITool? CreateAITool(ITool tool)
    {
        try
        {
            var func = Microsoft.Extensions.AI.AIFunctionFactory.Create(
                (string args, CancellationToken ct) => ExecuteToolAsync(tool, args, ct),
                tool.Name,
                tool.Description,
                null);
            return func;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create AITool for tool: {ToolName}", tool.Name);
            return null;
        }
    }

    private async Task<object?> ExecuteToolAsync(ITool tool, string arguments, System.Threading.CancellationToken ct = default)
    {
        var result = await tool.ExecuteAsync(arguments);
        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "Tool execution failed");
        return result.Data;
    }
}
