using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MsChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Goldfish.Harness;

public sealed class GoldfishHarnessRunner
{
    private const int DefaultMaxHistoryMessages = 24;
    private const int DefaultMaxReactSteps = 5;
    private const int MaxToolResultChars = 20000;
    private static readonly JsonSerializerOptions ToolArgumentJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IChatClient _chatClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly IReasoningStrategyDecider _reasoningStrategyDecider;
    private readonly ILogger<GoldfishHarnessRunner> _logger;
    private readonly int _maxHistoryMessages;
    private readonly int _maxReactSteps;

    public GoldfishHarnessRunner(
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        ILogger<GoldfishHarnessRunner>? logger = null,
        int maxHistoryMessages = DefaultMaxHistoryMessages,
        int maxReactSteps = DefaultMaxReactSteps)
        : this(chatClient, toolRegistry, logger, maxHistoryMessages, maxReactSteps, skillRegistry: null)
    {
    }

    public GoldfishHarnessRunner(
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        ILogger<GoldfishHarnessRunner>? logger = null,
        int maxHistoryMessages = DefaultMaxHistoryMessages,
        int maxReactSteps = DefaultMaxReactSteps,
        ISkillRegistry? skillRegistry = null,
        IReasoningStrategyDecider? reasoningStrategyDecider = null)
    {
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _skillRegistry = skillRegistry;
        _logger = logger ?? NullLogger<GoldfishHarnessRunner>.Instance;
        _reasoningStrategyDecider = reasoningStrategyDecider ?? new DefaultReasoningStrategyDecider(_chatClient, _logger);
        _maxHistoryMessages = Math.Max(1, maxHistoryMessages);
        _maxReactSteps = Math.Max(1, maxReactSteps);
    }

    public async Task<GoldfishHarnessRunResult> RunAsync(
        GoldfishHarnessRequest request,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("n");
        var skillOptions = request.SkillOptions ?? SkillOptions.Default;
        var reasoningSelection = await SelectReasoningStrategyAsync(request, ct);
        var skillState = new SkillRuntimeState(skillOptions);
        await RestoreSessionSkillsAsync(request, skillState, skillOptions, ct);
        var internalTools = BuildInternalTools(request, skillState, skillOptions);
        var toolFunctions = BuildToolFunctions(BuildEffectiveTools(_toolRegistry.GetAll(), internalTools, skillState, skillOptions));
        var messages = BuildMessages(request, skillState, reasoningSelection);
        var result = new GoldfishHarnessRunResult();
        result.Events.Add(GoldfishHarnessEvent.ReasoningStrategySelected(reasoningSelection));
        var plan = await CreatePlanIfNeededAsync(request, reasoningSelection, messages, ct);
        if (plan is not null)
        {
            result.Events.Add(GoldfishHarnessEvent.PlanCreated(plan));
            AppendPlanToLeadingSystemMessage(messages, plan);
        }
        var reWooGraph = await CreateReWooGraphIfNeededAsync(request, reasoningSelection, messages, toolFunctions, ct);
        if (reWooGraph is not null)
        {
            result.Events.Add(GoldfishHarnessEvent.ReWooGraphCreated(reWooGraph));
            AppendReWooGraphToLeadingSystemMessage(messages, reWooGraph);
            var graphRecords = await ExecuteReWooGraphAsync(request, runId, reWooGraph, toolFunctions, ct);
            foreach (var record in graphRecords)
            {
                result.ToolCalls.Add(record);
                result.Events.Add(GoldfishHarnessEvent.ToolCall(0, record));
                result.Events.Add(GoldfishHarnessEvent.ToolResult(0, record));
            }
            AppendReWooObservations(messages, graphRecords);
        }
        _logger.LogInformation("Goldfish Harness starting. tools={ToolCount}, model tools mode={ToolMode}", toolFunctions.Count, toolFunctions.Count > 0 ? "native" : "none");

        for (var step = 1; step <= _maxReactSteps; step++)
        {
            var planStep = GetPlanStep(plan, step);
            if (planStep is not null)
            {
                result.Events.Add(GoldfishHarnessEvent.PlanStepStarted(planStep));
            }

            toolFunctions = BuildToolFunctions(BuildEffectiveTools(_toolRegistry.GetAll(), internalTools, skillState, skillOptions));
            var steers = await DrainSteersAsync(request, messages, step, ct);
            if (steers.Count > 0)
            {
                result.Events.Add(GoldfishHarnessEvent.Thinking(step, $"收到 {steers.Count} 条运行期 steer，已加入下一次模型调用。"));
            }

            var response = await _chatClient.GetResponseAsync(messages, BuildChatOptions(request, toolFunctions), ct);
            if (response.Usage is not null)
            {
                result.Events.Add(GoldfishHarnessEvent.TokenUsage(step, response.Usage));
            }
            var raw = response.Text?.Trim() ?? string.Empty;
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Where(c => !c.InformationalOnly)
                .ToList();

            if (functionCalls.Count > 0)
            {
                foreach (var message in response.Messages)
                {
                    messages.Add(message);
                }

                foreach (var functionCall in functionCalls)
                {
                    var record = await ExecuteFunctionCallAsync(request, runId, step, functionCall, toolFunctions, ct);
                    result.ToolCalls.Add(record);
                    result.Events.Add(GoldfishHarnessEvent.ToolCall(step, record));
                    result.Events.Add(GoldfishHarnessEvent.ToolResult(step, record));
                    messages.Add(new MsChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(functionCall.CallId, BuildFunctionResultPayload(record))]));
                    AppendLoadedSkillPromptIfNeeded(messages, record, skillState);
                    if (TryCreateRequiredRetryCall(record, toolFunctions, step, out var retryCall))
                    {
                        var retryRecord = await ExecuteFunctionCallAsync(request, runId, step, retryCall, toolFunctions, ct);
                        result.ToolCalls.Add(retryRecord);
                        result.Events.Add(GoldfishHarnessEvent.ToolCall(step, retryRecord));
                        result.Events.Add(GoldfishHarnessEvent.ToolResult(step, retryRecord));
                        messages.Add(new MsChatMessage(
                            ChatRole.Assistant,
                            [retryCall]));
                        messages.Add(new MsChatMessage(
                            ChatRole.Tool,
                            [new FunctionResultContent(retryCall.CallId, BuildFunctionResultPayload(retryRecord))]));
                    }
                }
                if (planStep is not null)
                {
                    result.Events.Add(GoldfishHarnessEvent.PlanStepCompleted(planStep, "工具观察已加入上下文。"));
                }
                continue;
            }

            var action = ParseReactAction(raw);
            if (!string.IsNullOrWhiteSpace(action.Thought))
            {
                result.Events.Add(GoldfishHarnessEvent.Thinking(step, action.Thought!));
            }

            if (action.Kind == GoldfishActionKind.Tool && !string.IsNullOrWhiteSpace(action.Tool))
            {
                var legacyToolCallId = BuildLegacyToolCallId(step, action.Tool!);
                var record = await ExecuteLegacyToolActionAsync(request, runId, step, action, legacyToolCallId, ct);
                result.ToolCalls.Add(record);
                result.Events.Add(GoldfishHarnessEvent.ToolCall(step, record));
                result.Events.Add(GoldfishHarnessEvent.ToolResult(step, record));
                messages.Add(new MsChatMessage(ChatRole.Assistant, raw));
                messages.Add(new MsChatMessage(ChatRole.User, BuildObservationPrompt(record)));
                if (planStep is not null)
                {
                    result.Events.Add(GoldfishHarnessEvent.PlanStepCompleted(planStep, "工具观察已加入上下文。"));
                }
                continue;
            }

            result.Answer = NormalizeFinalAnswer(CleanFinalAnswer(action.Answer ?? raw));
            if (string.IsNullOrWhiteSpace(result.Answer))
            {
                result.Answer = "[Goldfish 无输出]";
            }
            if (IsFinalTextOnly(request)
                && (RequiresToolBeforeFinal(request, result.ToolCalls.Count > 0)
                    || ShouldContinueFinalTextOnly(result.Answer, toolFunctions.Count > 0)))
            {
                messages.Add(new MsChatMessage(ChatRole.Assistant, raw));
                messages.Add(new MsChatMessage(
                    ChatRole.User,
                    RequiresToolBeforeFinal(request, result.ToolCalls.Count > 0)
                        ? BuildRequiredToolCorrection()
                        : BuildFinalTextCorrection(result.Answer)));
                result.Answer = string.Empty;
                continue;
            }
            if (planStep is not null)
            {
                result.Events.Add(GoldfishHarnessEvent.PlanStepCompleted(planStep, "已生成最终回答。"));
            }
            if (plan is not null)
            {
                result.Events.AddRange(CompleteRemainingPlanSteps(plan, step));
            }
            var reflection = await ReflectFinalAnswerIfNeededAsync(request, reasoningSelection, messages, result.Answer, ct);
            if (reflection is not null)
            {
                result.Answer = reflection.Answer;
                result.Events.Add(GoldfishHarnessEvent.ReflectionCompleted(reflection));
            }
            AddReasoningTraceCompletedEvent(result);
            return result;
        }

        messages.Add(new MsChatMessage(ChatRole.User, "请停止继续调用工具，基于已有观察给出最终回答。"));
        var finalResponse = await _chatClient.GetResponseAsync(messages, BuildChatOptions(request, new Dictionary<string, ToolFunction>()), ct);
        result.Answer = NormalizeFinalAnswer(
            CleanFinalAnswer(ParseReactAction(finalResponse.Text ?? string.Empty).Answer ?? finalResponse.Text ?? string.Empty));
        if (string.IsNullOrWhiteSpace(result.Answer))
        {
            result.Answer = "已达到 Goldfish 最大推理轮次，但没有生成可用最终回答。";
        }
        var finalReflection = await ReflectFinalAnswerIfNeededAsync(request, reasoningSelection, messages, result.Answer, ct);
        if (finalReflection is not null)
        {
            result.Answer = finalReflection.Answer;
            result.Events.Add(GoldfishHarnessEvent.ReflectionCompleted(finalReflection));
        }
        if (plan is not null)
        {
            var pendingStep = plan.Steps.FirstOrDefault(step => step.Index > _maxReactSteps);
            if (pendingStep is not null)
            {
                result.Events.Add(GoldfishHarnessEvent.PlanStepFailed(
                    pendingStep,
                    "已达到最大推理轮次。"));
            }
        }
        AddReasoningTraceCompletedEvent(result);
        return result;
    }

    public async IAsyncEnumerable<GoldfishHarnessEvent> StreamAsync(
        GoldfishHarnessRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("n");
        yield return GoldfishHarnessEvent.RunStarted(runId);
        await foreach (var ev in StreamCoreAsync(request, runId, ct))
        {
            // 统一注入 RunId，保留各事件自带的 EventId/Timestamp。
            yield return ev with { RunId = runId };
        }
    }

    private async IAsyncEnumerable<GoldfishHarnessEvent> StreamCoreAsync(
        GoldfishHarnessRequest request,
        string runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var skillOptions = request.SkillOptions ?? SkillOptions.Default;
        var reasoningSelection = await SelectReasoningStrategyAsync(request, ct);
        var skillState = new SkillRuntimeState(skillOptions);
        await RestoreSessionSkillsAsync(request, skillState, skillOptions, ct);
        var internalTools = BuildInternalTools(request, skillState, skillOptions);
        var toolFunctions = BuildToolFunctions(BuildEffectiveTools(_toolRegistry.GetAll(), internalTools, skillState, skillOptions));
        var messages = BuildMessages(request, skillState, reasoningSelection);
        _logger.LogInformation("Goldfish Harness streaming. tools={ToolCount}, model tools mode={ToolMode}", toolFunctions.Count, toolFunctions.Count > 0 ? "native" : "none");
        var toolWasUsed = false;
        var finalTextOnly = IsFinalTextOnly(request);
        var traceEvents = new List<GoldfishHarnessEvent>();
        var strategySelectedEvent = GoldfishHarnessEvent.ReasoningStrategySelected(reasoningSelection);
        traceEvents.Add(strategySelectedEvent);
        yield return strategySelectedEvent;
        var plan = await CreatePlanIfNeededAsync(request, reasoningSelection, messages, ct);
        if (plan is not null)
        {
            var planCreatedEvent = GoldfishHarnessEvent.PlanCreated(plan);
            traceEvents.Add(planCreatedEvent);
            yield return planCreatedEvent;
            AppendPlanToLeadingSystemMessage(messages, plan);
        }
        var reWooGraph = await CreateReWooGraphIfNeededAsync(request, reasoningSelection, messages, toolFunctions, ct);
        if (reWooGraph is not null)
        {
            var graphCreatedEvent = GoldfishHarnessEvent.ReWooGraphCreated(reWooGraph);
            traceEvents.Add(graphCreatedEvent);
            yield return graphCreatedEvent;
            AppendReWooGraphToLeadingSystemMessage(messages, reWooGraph);
            var graphRecords = await ExecuteReWooGraphAsync(request, runId, reWooGraph, toolFunctions, ct);
            foreach (var record in graphRecords)
            {
                toolWasUsed = toolWasUsed || record.Success;
                var toolCallEvent = GoldfishHarnessEvent.ToolCall(0, record);
                var toolResultEvent = GoldfishHarnessEvent.ToolResult(0, record);
                traceEvents.Add(toolCallEvent);
                traceEvents.Add(toolResultEvent);
                yield return toolCallEvent;
                yield return toolResultEvent;
            }
            AppendReWooObservations(messages, graphRecords);
        }

        for (var step = 1; step <= _maxReactSteps; step++)
        {
            var planStep = GetPlanStep(plan, step);
            if (planStep is not null)
            {
                var planStepStartedEvent = GoldfishHarnessEvent.PlanStepStarted(planStep);
                traceEvents.Add(planStepStartedEvent);
                yield return planStepStartedEvent;
            }

            toolFunctions = BuildToolFunctions(BuildEffectiveTools(_toolRegistry.GetAll(), internalTools, skillState, skillOptions));
            var steers = await DrainSteersAsync(request, messages, step, ct);
            if (steers.Count > 0)
            {
                yield return GoldfishHarnessEvent.Thinking(step, $"收到 {steers.Count} 条运行期 steer，已加入下一次模型调用。");
            }

            yield return GoldfishHarnessEvent.Thinking(step, step == 1 ? "开始分析用户请求。" : "根据工具观察继续分析。");

            var updates = new List<ChatResponseUpdate>();
            var rawBuilder = new StringBuilder();
            var pendingTextBuilder = new StringBuilder();
            bool? streamTextDirectly = finalTextOnly || toolWasUsed ? false : null;
            await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, BuildChatOptions(request, toolFunctions), ct))
            {
                updates.Add(update);

                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    yield return GoldfishHarnessEvent.TokenUsage(step, usage.Details);
                }

                foreach (var reasoning in update.Contents.OfType<TextReasoningContent>())
                {
                    if (!string.IsNullOrWhiteSpace(reasoning.Text))
                    {
                        yield return GoldfishHarnessEvent.Thinking(step, reasoning.Text);
                    }
                }

                var textDelta = ExtractStreamingText(update);
                if (!string.IsNullOrEmpty(textDelta))
                {
                    rawBuilder.Append(textDelta);
                    if (streamTextDirectly != false)
                    {
                        pendingTextBuilder.Append(textDelta);
                        if (streamTextDirectly == true)
                        {
                            yield return GoldfishHarnessEvent.Text(step, textDelta);
                        }
                        else
                        {
                            var pending = pendingTextBuilder.ToString();
                            var trimmed = pending.TrimStart();
                            if (trimmed.Length > 0)
                            {
                                if (LooksLikeStructuredReact(trimmed))
                                {
                                    streamTextDirectly = false;
                                }
                                else
                                {
                                    streamTextDirectly = true;
                                    yield return GoldfishHarnessEvent.Text(step, pending);
                                    pendingTextBuilder.Clear();
                                }
                            }
                        }
                    }
                }
            }

            var response = updates.ToChatResponse();
            var raw = rawBuilder.Length > 0
                ? rawBuilder.ToString().Trim()
                : response.Text?.Trim() ?? string.Empty;
            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Where(c => !c.InformationalOnly)
                .ToList();

            if (functionCalls.Count > 0)
            {
                foreach (var message in response.Messages)
                {
                    messages.Add(message);
                }

                foreach (var functionCall in functionCalls)
                {
                    var toolCallStartEvent = GoldfishHarnessEvent.ToolCallStart(
                        step,
                        functionCall.Name,
                        SerializeArguments(functionCall.Arguments),
                        functionCall.CallId);
                    traceEvents.Add(toolCallStartEvent);
                    yield return toolCallStartEvent;
                    var record = await ExecuteFunctionCallAsync(request, runId, step, functionCall, toolFunctions, ct);
                    toolWasUsed = true;
                    var toolResultEvent = GoldfishHarnessEvent.ToolResult(step, record);
                    traceEvents.Add(toolResultEvent);
                    yield return toolResultEvent;
                    messages.Add(new MsChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(functionCall.CallId, BuildFunctionResultPayload(record))]));
                    AppendLoadedSkillPromptIfNeeded(messages, record, skillState);
                    if (TryCreateRequiredRetryCall(record, toolFunctions, step, out var retryCall))
                    {
                        var retryCallStartEvent = GoldfishHarnessEvent.ToolCallStart(
                            step,
                            retryCall.Name,
                            SerializeArguments(retryCall.Arguments),
                            retryCall.CallId);
                        traceEvents.Add(retryCallStartEvent);
                        yield return retryCallStartEvent;
                        var retryRecord = await ExecuteFunctionCallAsync(request, runId, step, retryCall, toolFunctions, ct);
                        var retryResultEvent = GoldfishHarnessEvent.ToolResult(step, retryRecord);
                        traceEvents.Add(retryResultEvent);
                        yield return retryResultEvent;
                        messages.Add(new MsChatMessage(
                            ChatRole.Assistant,
                            [retryCall]));
                        messages.Add(new MsChatMessage(
                            ChatRole.Tool,
                            [new FunctionResultContent(retryCall.CallId, BuildFunctionResultPayload(retryRecord))]));
                    }
                }
                if (planStep is not null)
                {
                    var planStepCompletedEvent = GoldfishHarnessEvent.PlanStepCompleted(planStep, "工具观察已加入上下文。");
                    traceEvents.Add(planStepCompletedEvent);
                    yield return planStepCompletedEvent;
                }
                continue;
            }

            var action = ParseReactAction(raw);
            if (!string.IsNullOrWhiteSpace(action.Thought))
            {
                yield return GoldfishHarnessEvent.Thinking(step, action.Thought!);
            }

            if (action.Kind == GoldfishActionKind.Tool && !string.IsNullOrWhiteSpace(action.Tool))
            {
                var legacyToolCallId = BuildLegacyToolCallId(step, action.Tool!);
                var toolCallStartEvent = GoldfishHarnessEvent.ToolCallStart(step, action.Tool!, action.Arguments ?? "{}", legacyToolCallId);
                traceEvents.Add(toolCallStartEvent);
                yield return toolCallStartEvent;
                var record = await ExecuteLegacyToolActionAsync(request, runId, step, action, legacyToolCallId, ct);
                toolWasUsed = true;
                var toolResultEvent = GoldfishHarnessEvent.ToolResult(step, record);
                traceEvents.Add(toolResultEvent);
                yield return toolResultEvent;
                messages.Add(new MsChatMessage(ChatRole.Assistant, raw));
                messages.Add(new MsChatMessage(ChatRole.User, BuildObservationPrompt(record)));
                if (planStep is not null)
                {
                    var planStepCompletedEvent = GoldfishHarnessEvent.PlanStepCompleted(planStep, "工具观察已加入上下文。");
                    traceEvents.Add(planStepCompletedEvent);
                    yield return planStepCompletedEvent;
                }
                continue;
            }

            var answer = NormalizeFinalAnswer(CleanFinalAnswer(action.Answer ?? raw));
            if (string.IsNullOrWhiteSpace(answer) && rawBuilder.Length == 0)
            {
                answer = "[Goldfish 无输出]";
            }
            if (finalTextOnly
                && (RequiresToolBeforeFinal(request, toolWasUsed)
                    || ShouldContinueFinalTextOnly(answer, toolFunctions.Count > 0)))
            {
                messages.Add(new MsChatMessage(ChatRole.Assistant, raw));
                messages.Add(new MsChatMessage(
                    ChatRole.User,
                    RequiresToolBeforeFinal(request, toolWasUsed)
                        ? BuildRequiredToolCorrection()
                        : BuildFinalTextCorrection(answer)));
                yield return GoldfishHarnessEvent.Thinking(step, "检测到过程占位话术，继续完成任务后再输出最终答复。");
                continue;
            }
            if (planStep is not null)
            {
                var planStepCompletedEvent = GoldfishHarnessEvent.PlanStepCompleted(planStep, "已生成最终回答。");
                traceEvents.Add(planStepCompletedEvent);
                yield return planStepCompletedEvent;
            }
            if (plan is not null)
            {
                foreach (var remainingEvent in CompleteRemainingPlanSteps(plan, step))
                {
                    traceEvents.Add(remainingEvent);
                    yield return remainingEvent;
                }
            }
            var reflection = await ReflectFinalAnswerIfNeededAsync(request, reasoningSelection, messages, answer, ct);
            if (reflection is not null)
            {
                answer = reflection.Answer;
                var reflectionEvent = GoldfishHarnessEvent.ReflectionCompleted(reflection);
                traceEvents.Add(reflectionEvent);
                yield return reflectionEvent;
            }
            if (!string.IsNullOrWhiteSpace(answer) && streamTextDirectly != true)
            {
                yield return GoldfishHarnessEvent.Text(step, answer);
            }
            var traceCompletedEvent = GoldfishHarnessEvent.ReasoningTraceCompleted(traceEvents);
            if (!string.IsNullOrWhiteSpace(traceCompletedEvent.Delta))
            {
                yield return traceCompletedEvent;
            }
            yield return GoldfishHarnessEvent.Completed(step, answer);
            yield break;
        }

        await DrainSteersAsync(request, messages, _maxReactSteps + 1, ct);
        messages.Add(new MsChatMessage(ChatRole.User, "请停止继续调用工具，基于已有观察给出最终回答。"));
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, BuildChatOptions(request, new Dictionary<string, ToolFunction>()), ct))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                yield return GoldfishHarnessEvent.TokenUsage(_maxReactSteps + 1, usage.Details);
            }

            foreach (var reasoning in update.Contents.OfType<TextReasoningContent>())
            {
                if (!string.IsNullOrWhiteSpace(reasoning.Text))
                {
                    yield return GoldfishHarnessEvent.Thinking(_maxReactSteps + 1, reasoning.Text);
                }
            }
            var textDelta = ExtractStreamingText(update);
            if (!string.IsNullOrEmpty(textDelta))
            {
                yield return GoldfishHarnessEvent.Text(_maxReactSteps + 1, textDelta);
            }
        }
        if (plan is not null)
        {
            var pendingStep = plan.Steps.FirstOrDefault(step => step.Index > _maxReactSteps);
            if (pendingStep is not null)
            {
                var planStepFailedEvent = GoldfishHarnessEvent.PlanStepFailed(
                    pendingStep,
                    "已达到最大推理轮次。");
                traceEvents.Add(planStepFailedEvent);
                yield return planStepFailedEvent;
            }
        }
        var finalTraceCompletedEvent = GoldfishHarnessEvent.ReasoningTraceCompleted(traceEvents);
        if (!string.IsNullOrWhiteSpace(finalTraceCompletedEvent.Delta))
        {
            yield return finalTraceCompletedEvent;
        }
        yield return GoldfishHarnessEvent.Completed(_maxReactSteps + 1, string.Empty);
    }

    private static bool IsFinalTextOnly(GoldfishHarnessRequest request)
        => request.AgentInfo.ExtraData.TryGetValue("RuntimeResponseMode", out var mode)
            && string.Equals(mode, "final_text_only", StringComparison.OrdinalIgnoreCase);

    private static void AddReasoningTraceCompletedEvent(GoldfishHarnessRunResult result)
    {
        if (result.Events.Any(ev => ev.Kind == GoldfishEventKind.ReasoningTraceCompleted))
        {
            return;
        }

        var trace = GoldfishHarnessEvent.ReasoningTraceCompleted(result.Events);
        if (!string.IsNullOrWhiteSpace(trace.Delta))
        {
            result.Events.Add(trace);
        }
    }

    private static bool RequiresToolBeforeFinal(GoldfishHarnessRequest request, bool toolWasUsed)
        => !toolWasUsed
            && request.AgentInfo.ExtraData.TryGetValue("GoldfishRequireToolBeforeFinal", out var required)
            && bool.TryParse(required, out var enabled)
            && enabled;

    private static string BuildRequiredToolCorrection()
        => """
[Skill 工具执行约束]
当前是查询型语音请求，活动 Skill 已声明结果必须来自工具，但本轮尚未调用任何工具，因此不能输出 final。现在必须调用最合适的只读查询工具；拿到结果后再输出可直接播报的最终答案。
""";

    private static bool IsProvisionalFinalAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        var compact = Regex.Replace(answer, @"\s+", string.Empty);
        if (compact.Length > 80) return false;
        return Regex.IsMatch(
            compact,
            @"(正在.*(查询|查找|查一下|处理|确认|获取|为您查)|请稍等|稍等一下|我(先|来|马上).*(查询|查找|查一下|处理|确认|获取)|马上为您.*(查询|查找|处理))",
            RegexOptions.CultureInvariant);
    }

    private static bool ShouldContinueFinalTextOnly(string? answer, bool hasTools)
        => IsProvisionalFinalAnswer(answer)
            || (hasTools && IsUnresolvedNotFoundAnswer(answer));

    private static bool IsUnresolvedNotFoundAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        var compact = Regex.Replace(answer, @"\s+", string.Empty);
        if (compact.Length > 160) return false;
        return Regex.IsMatch(
            compact,
            @"(没有找到|未找到|查不到|不存在).*(名字|名称|孩子|成员|用户|记录|信息)|请.*(添加|确认).*(名字|名称|姓名|信息)",
            RegexOptions.CultureInvariant);
    }

    private static string BuildFinalTextCorrection(string answer)
        => IsUnresolvedNotFoundAnswer(answer)
            ? """
[通道最终答复校验]
当前“未找到”只代表刚才的精确查询没有命中，不能直接作为最终答复。请继续遵守已加载 Skill：调用可用的列表、候选或查询工具获取完整候选，按标准名称、昵称、近音和错别字匹配；只有完成候选核对后，才能播报结果或请用户确认多个候选。
"""
            : """
[通道最终答复校验]
上一条只是执行过程或等待话术，不能作为最终答复。不要再次说“正在查询”“请稍等”或“我先查一下”。请继续完成必要的工具调用，并只在拿到工具结果后输出可直接播报的最终答案；如果工具失败，直接说明实际失败原因。
""";

    private static bool TryCreateRequiredRetryCall(
        ToolCallRecord record,
        IReadOnlyDictionary<string, ToolFunction> toolFunctions,
        int step,
        out FunctionCallContent retryCall)
    {
        retryCall = null!;
        if (string.IsNullOrWhiteSpace(record.Result)) return false;
        try
        {
            using var document = JsonDocument.Parse(record.Result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("structuredContent", out var structured)
                || structured.ValueKind != JsonValueKind.Object
                || !structured.TryGetProperty("requires_child_list_retry", out var required)
                || required.ValueKind != JsonValueKind.True
                || !structured.TryGetProperty("retry_tool", out var retryToolElement))
            {
                return false;
            }

            var retryTool = retryToolElement.GetString();
            if (string.IsNullOrWhiteSpace(retryTool)) return false;
            var function = toolFunctions.FirstOrDefault(item =>
                string.Equals(item.Key, retryTool, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Value.Tool.Id, retryTool, StringComparison.OrdinalIgnoreCase)
                || item.Key.EndsWith($"_{retryTool}", StringComparison.OrdinalIgnoreCase)
                || item.Value.Tool.Id.EndsWith($"_{retryTool}", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(function.Key)) return false;

            var arguments = new Dictionary<string, object?>();
            if (structured.TryGetProperty("retry_arguments", out var retryArguments)
                && retryArguments.ValueKind == JsonValueKind.Object)
            {
                arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    retryArguments.GetRawText(),
                    ToolArgumentJsonOptions) ?? new Dictionary<string, object?>();
            }
            retryCall = new FunctionCallContent(
                $"required-retry-{step}-{Guid.NewGuid():N}",
                function.Key,
                arguments);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<ReasoningStrategySelection> SelectReasoningStrategyAsync(
        GoldfishHarnessRequest request,
        CancellationToken ct)
        => await _reasoningStrategyDecider.SelectAsync(
            new ReasoningStrategyDecisionRequest(
                request.SessionId,
                request.UserMessageText,
                request.ReasoningOptions,
                request.CachedReasoningSelection),
            ct);

    private async Task<ReasoningPlan?> CreatePlanIfNeededAsync(
        GoldfishHarnessRequest request,
        ReasoningStrategySelection selection,
        IReadOnlyList<MsChatMessage> messages,
        CancellationToken ct)
    {
        if (selection.Effective != ReasoningStrategyKind.PlanAndExecute)
        {
            return null;
        }

        var options = request.ReasoningOptions ?? ReasoningOptions.Default;
        var planningMessages = messages.ToList();
        planningMessages.Add(new MsChatMessage(ChatRole.User, BuildPlanCreationPrompt(request.UserMessageText, options.MaxPlanSteps)));
        var response = await _chatClient.GetResponseAsync(
            planningMessages,
            BuildChatOptions(request, new Dictionary<string, ToolFunction>()),
            ct);
        return ReasoningPlanParser.ParseOrFallback(response.Text, request.UserMessageText, options.MaxPlanSteps);
    }

    private static string BuildPlanCreationPrompt(string userMessage, int maxPlanSteps)
        => $$"""
请为当前用户请求生成一个简洁执行计划。只输出 JSON，不要输出 Markdown。
JSON 格式：
{
  "summary": "一句话概括计划",
  "steps": [
    { "title": "步骤标题", "description": "这一步要完成的可验证目标" }
  ]
}

约束：
- 最多 {{Math.Max(1, maxPlanSteps)}} 步。
- 除非用户请求本身只能一步完成，否则至少拆成 2 步：先获取或处理必要信息，再汇总并形成最终结果。
- 不要调用工具，不要编造工具结果。
- 计划应服务于后续执行，而不是面向用户的最终回答。

用户请求：
{{userMessage}}
""";

    private static void AppendPlanToLeadingSystemMessage(IList<MsChatMessage> messages, ReasoningPlan plan)
    {
        if (messages.Count == 0 || messages[0].Role != ChatRole.System)
        {
            messages.Insert(0, new MsChatMessage(ChatRole.System, plan.ToPromptSection()));
            return;
        }

        messages[0] = new MsChatMessage(ChatRole.System, $"{messages[0].Text?.TrimEnd()}\n\n{plan.ToPromptSection()}");
    }

    private async Task<ReasoningReWooGraph?> CreateReWooGraphIfNeededAsync(
        GoldfishHarnessRequest request,
        ReasoningStrategySelection selection,
        IReadOnlyList<MsChatMessage> messages,
        IReadOnlyDictionary<string, ToolFunction> toolFunctions,
        CancellationToken ct)
    {
        if (selection.Effective != ReasoningStrategyKind.ReWOO || toolFunctions.Count == 0)
        {
            return null;
        }

        var options = request.ReasoningOptions ?? ReasoningOptions.Default;
        var graphMessages = messages.ToList();
        graphMessages.Add(new MsChatMessage(
            ChatRole.User,
            BuildReWooGraphPrompt(request.UserMessageText, toolFunctions, options.MaxReasoningSteps)));
        var response = await _chatClient.GetResponseAsync(
            graphMessages,
            BuildChatOptions(request, new Dictionary<string, ToolFunction>()),
            ct);
        return ReasoningReWooGraphParser.ParseOrFallback(
            response.Text,
            toolFunctions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            options.MaxReasoningSteps);
    }

    private static string BuildReWooGraphPrompt(
        string userMessage,
        IReadOnlyDictionary<string, ToolFunction> toolFunctions,
        int maxNodes)
    {
        var tools = string.Join("\n", toolFunctions.Select(item =>
            $"- {item.Key}: {item.Value.Description}"));
        return $$"""
请为当前用户请求生成 ReWOO 工具图。只输出 JSON，不要输出 Markdown。
JSON 格式：
{
  "summary": "一句话概括工具图",
  "nodes": [
    { "id": "N1", "tool": "必须使用下方工具名", "purpose": "该节点要收集的证据", "arguments": {} }
  ]
}

约束：
- 最多 {{Math.Max(1, maxNodes)}} 个节点。
- 只允许使用下方工具名，不要编造工具。
- arguments 必须是 JSON object。
- 不要输出最终答案，不要编造工具结果。

可用工具：
{{tools}}

用户请求：
{{userMessage}}
""";
    }

    private static void AppendReWooGraphToLeadingSystemMessage(IList<MsChatMessage> messages, ReasoningReWooGraph graph)
    {
        if (messages.Count == 0 || messages[0].Role != ChatRole.System)
        {
            messages.Insert(0, new MsChatMessage(ChatRole.System, graph.ToPromptSection()));
            return;
        }

        messages[0] = new MsChatMessage(ChatRole.System, $"{messages[0].Text?.TrimEnd()}\n\n{graph.ToPromptSection()}");
    }

    private async Task<IReadOnlyList<ToolCallRecord>> ExecuteReWooGraphAsync(
        GoldfishHarnessRequest request,
        string runId,
        ReasoningReWooGraph graph,
        IReadOnlyDictionary<string, ToolFunction> toolFunctions,
        CancellationToken ct)
    {
        var records = new List<ToolCallRecord>();
        foreach (var node in graph.Nodes)
        {
            if (!toolFunctions.TryGetValue(node.Tool, out var function))
            {
                records.Add(new ToolCallRecord
                {
                    ToolCallId = $"rewoo-{node.Id}",
                    ToolId = node.Tool,
                    Arguments = node.ArgumentsJson,
                    Result = $"Tool not found: {node.Tool}",
                    Success = false
                });
                continue;
            }

            var call = new FunctionCallContent(
                $"rewoo-{graph.GraphId}-{node.Id}",
                function.Name,
                ParseFunctionArguments(node.ArgumentsJson));
            records.Add(await ExecuteFunctionCallAsync(request, runId, 0, call, toolFunctions, ct));
        }

        return records;
    }

    private static Dictionary<string, object?> ParseFunctionArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                argumentsJson,
                ToolArgumentJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void AppendReWooObservations(
        IList<MsChatMessage> messages,
        IReadOnlyList<ToolCallRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var observations = string.Join("\n\n", records.Select((record, index) =>
            $"[{index + 1}] Tool: {record.ToolId}\nSuccess: {record.Success}\nArguments: {record.Arguments}\nResult:\n{BuildReadableToolResult(record)}"));
        messages.Add(new MsChatMessage(ChatRole.User, $"""
[ReWOO 工具图观察]
Harness 已按 ReWOO 工具图执行以下节点。请基于这些真实观察继续综合，不要重复调用已经完成且成功的同一工具节点，除非需要补充证据。

{observations}
"""));
    }

    private async Task<ReasoningReflection?> ReflectFinalAnswerIfNeededAsync(
        GoldfishHarnessRequest request,
        ReasoningStrategySelection selection,
        IReadOnlyList<MsChatMessage> messages,
        string answer,
        CancellationToken ct)
    {
        var options = request.ReasoningOptions ?? ReasoningOptions.Default;
        if (!selection.ReflexionEnabled
            || options.MaxReflectionRetries <= 0
            || !ShouldRunReflexion(request.UserMessageText, answer))
        {
            return null;
        }

        var reflectionMessages = messages.ToList();
        reflectionMessages.Add(new MsChatMessage(ChatRole.User, BuildReflectionPrompt(request.UserMessageText, answer)));
        var response = await _chatClient.GetResponseAsync(
            reflectionMessages,
            BuildChatOptions(request, new Dictionary<string, ToolFunction>()),
            ct);
        return ReasoningReflectionParser.ParseOrKeep(response.Text, answer);
    }

    private static bool ShouldRunReflexion(string? userMessage, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return true;
        var text = userMessage ?? string.Empty;
        return Regex.IsMatch(
            text,
            "(必须|不要|不得|不能|限制|约束|验收|失败|错误|修正|纠错|不超过|至少|恰好|只输出|JSON|格式)",
            RegexOptions.CultureInvariant);
    }

    private static string BuildReflectionPrompt(string userMessage, string answer)
        => $$"""
你是 Goldfish Harness 的 Reflexion 校验器。请检查当前最终答案是否满足用户请求和系统约束。
只输出 JSON，不要输出 Markdown。
JSON 格式：
{"action":"keep|revise","reason":"简短原因","answer":"当 action=revise 时输出修正后的最终答案"}

约束：
- 如果当前答案已经满足要求，输出 {"action":"keep","reason":"..."}。
- 如果存在遗漏、格式错误、违反限制、空答复或明显未完成，输出 action=revise，并给出可以直接返回用户的修正版 answer。
- 不要暴露内部推理过程。

用户请求：
{{userMessage}}

当前最终答案：
{{answer}}
""";

    private static ReasoningPlanStep? GetPlanStep(ReasoningPlan? plan, int oneBasedStep)
    {
        if (plan is null || plan.Steps.Count == 0 || oneBasedStep < 1 || oneBasedStep > plan.Steps.Count)
        {
            return null;
        }

        return plan.Steps[oneBasedStep - 1];
    }

    private static IEnumerable<GoldfishHarnessEvent> CompleteRemainingPlanSteps(
        ReasoningPlan plan,
        int completedThroughIndex)
    {
        foreach (var step in plan.Steps.Where(item => item.Index > completedThroughIndex))
        {
            yield return GoldfishHarnessEvent.PlanStepStarted(step);
            yield return GoldfishHarnessEvent.PlanStepCompleted(step, "已由最终回答覆盖完成。");
        }
    }

    private static ChatOptions BuildChatOptions(GoldfishHarnessRequest request, IReadOnlyDictionary<string, ToolFunction> toolFunctions)
    {
        var options = new ChatOptions
        {
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = request.Temperature
        };

        if (toolFunctions.Count > 0)
        {
            options.Tools = toolFunctions.Values.Cast<AITool>().ToList();
            options.ToolMode = ChatToolMode.Auto;
            options.AllowMultipleToolCalls = true;
        }

        return options;
    }

    private List<MsChatMessage> BuildMessages(GoldfishHarnessRequest request)
        => BuildMessages(request, null);

    private List<MsChatMessage> BuildMessages(GoldfishHarnessRequest request, SkillRuntimeState? skillState)
        => BuildMessages(request, skillState, ReasoningStrategySelector.Select(request.UserMessageText, request.ReasoningOptions));

    private List<MsChatMessage> BuildMessages(
        GoldfishHarnessRequest request,
        SkillRuntimeState? skillState,
        ReasoningStrategySelection reasoningSelection)
    {
        var context = GoldfishRunContext.FromAgentInfo(request.AgentInfo, request.SessionId, request.DisableConfigCache);
        var memoryOptions = request.MemoryOptions ?? MemoryOptions.Default;
        var systemPrompt = BuildSystemPrompt(context, memoryOptions, request.SkillOptions ?? SkillOptions.Default);
        systemPrompt = $"{systemPrompt.TrimEnd()}\n\n{reasoningSelection.ToPromptSection()}";
        var suppliedMemory = request.MemoryContext;
        if (suppliedMemory is not null)
        {
            var memoryPrompt = BuildMemoryPrompt(suppliedMemory);
            if (!string.IsNullOrWhiteSpace(memoryPrompt))
            {
                systemPrompt = $"{systemPrompt.TrimEnd()}\n\n{memoryPrompt}";
            }
        }
        if (skillState is not null)
        {
            var loadedSkillsPrompt = skillState.BuildLoadedSkillsPrompt();
            if (!string.IsNullOrWhiteSpace(loadedSkillsPrompt))
            {
                systemPrompt = $"{systemPrompt.TrimEnd()}\n\n{loadedSkillsPrompt}";
            }
        }

        var messages = new List<MsChatMessage>
        {
            new(ChatRole.System, systemPrompt)
        };

        var historyForPrompt = suppliedMemory?.ShortTermMessages ?? request.History;
        if (historyForPrompt.LastOrDefault() is { Role: "user" } last
            && string.Equals(last.Content, request.UserMessageText, StringComparison.Ordinal))
        {
            historyForPrompt = historyForPrompt.Take(historyForPrompt.Count - 1).ToList();
        }

        var selectedHistory = SelectShortTermHistory(
                historyForPrompt,
                memoryOptions.ShortTerm,
                _maxHistoryMessages)
            .ToList();
        selectedHistory = ApplyEstimatedPromptBudget(
            selectedHistory,
            systemPrompt,
            request.UserMessageText,
            memoryOptions.MediumTerm);

        foreach (var msg in selectedHistory)
        {
            messages.Add(new MsChatMessage(ParseRole(msg.Role), msg.Content));
        }

        messages.Add(BuildUserMessage(request));
        return messages;
    }

    private static MsChatMessage BuildUserMessage(GoldfishHarnessRequest request)
        => request.UserMessage;

    private static async Task<IReadOnlyList<string>> DrainSteersAsync(
        GoldfishHarnessRequest request,
        List<MsChatMessage> messages,
        int step,
        CancellationToken ct)
    {
        if (request.SteerSource == null) return [];
        var steers = await request.SteerSource.DrainAsync(request.SessionId, ct);
        var normalized = steers
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToList();
        if (normalized.Count == 0) return normalized;

        var steerText = string.Join("\n", normalized.Select((item, index) => $"- Steer #{index + 1}: {item}"));
        messages.Add(new MsChatMessage(ChatRole.User, $"""
[运行期间收到的 steer 指令]
这些内容是在当前 Goldfish Harness run 执行期间由用户追加的补充/修正。请在第 {step} 次模型调用前合并这些要求，调整后续计划；不要重放已经完成的工具调用，除非用户明确要求。

{steerText}
"""));
        return normalized;
    }

    private string BuildSystemPrompt(GoldfishRunContext context, MemoryOptions memoryOptions, SkillOptions skillOptions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(context.Agent.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("你是 Goldfish，自研智能体运行时中的一个可执行智能体。你需要在内部完成必要推理，再决定是否行动，最后给出清晰回答。");
        sb.AppendLine("严禁把思考过程、意图猜测、推理草稿、修正思路或 ReAct 过程写进正文；如果模型支持原生 reasoning/thinking 通道，只能通过该通道表达思考。");
        sb.AppendLine("正文只能输出面向用户的最终回答，不能先列出“可能的意图”“考虑到”“我将假设”等内部分析。");
        sb.AppendLine("需要继续行动时，必须通过模型提供的 tools/function calling 接口调用工具，不要把工具名称、参数或伪 JSON 工具调用写在正文里。");
        sb.AppendLine("可以回答时，直接给出面向用户的最终回答；不要编造工具结果。");
        sb.AppendLine("如果没有合适工具，直接 final。不要编造工具结果。");
        sb.AppendLine("当用户要求分行、列表、表格、代码块或 Markdown 格式时，必须保留必要换行，不要把多行内容压缩成一行。");
        sb.AppendLine("当用户明确要求返回 N 行、分 N 行、每行一条或多行测试内容时，必须使用真实换行符逐行输出；不要用空格、连续句号或普通段落代替换行。");
        sb.AppendLine("用户消息可能包含图片、音频、视频或文件附件；如果模型提供了可见内容，你需要结合附件作答，否则基于附件元数据说明当前可判断与不可判断的部分。");
        sb.AppendLine();
        sb.AppendLine("## 用户信息");
        sb.AppendLine($"- 用户名: {context.User.Name}");
        sb.AppendLine($"- 用户ID: {context.User.Id}");
        sb.AppendLine($"- 用户角色: {context.User.Role}");
        sb.AppendLine();
        sb.AppendLine("## 环境信息");
        sb.AppendLine($"- 当前时间(UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"- 服务机器: {Environment.MachineName}");
        sb.AppendLine($"- 操作系统: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"- 会话ID: {context.SessionId}");
        sb.AppendLine($"- 智能体名称: {context.Agent.Name}");
        sb.AppendLine($"- 配置缓存: {(context.DisableConfigCache ? "disabled" : "enabled")}");
        if (!string.IsNullOrWhiteSpace(context.Agent.ProjectDirectory))
        {
            sb.AppendLine($"- 项目目录: {context.Agent.ProjectDirectory}");
        }
        // Host/channel routing instructions belong in Agent.SystemPrompt; do not duplicate them here.
        sb.AppendLine();
        sb.AppendLine("## 对话历史");
        if (memoryOptions.ShortTerm.Enabled)
        {
            var ageText = memoryOptions.ShortTerm.MaxAge.HasValue
                ? $"，且不早于最近 {memoryOptions.ShortTerm.MaxAge.Value.TotalHours:0.#} 小时"
                : string.Empty;
            sb.AppendLine($"系统会按配置附加最多 {Math.Min(_maxHistoryMessages, memoryOptions.ShortTerm.MaxMessages)} 条短期用户/助手消息{ageText}。你需要延续上下文，不要把历史当作新请求重复回答。");
        }
        else
        {
            sb.AppendLine("短期对话历史已按配置关闭；只基于当前消息和显式注入的记忆作答。");
        }
        if (_skillRegistry != null && skillOptions.Enabled)
        {
            sb.AppendLine();
            sb.AppendLine("## Skills");
            sb.AppendLine("Goldfish 提供内部 Skill 能力。Skill 不是业务工具，而是可动态加载的任务流程和约束说明。");
            sb.AppendLine("当任务需要专门流程、领域规则或工具边界时，先调用 goldfish_skill_index 查询候选 Skill，再调用 goldfish_load_skill 加载最合适的 Skill。");
            sb.AppendLine("加载 Skill 后必须遵守其 instructions，并优先使用其 allowed-tools 中允许的工具。");
        }
        return sb.ToString().Trim();
    }

    private IReadOnlyList<ITool> BuildInternalTools(
        GoldfishHarnessRequest request,
        SkillRuntimeState skillState,
        SkillOptions skillOptions)
    {
        if (_skillRegistry == null || !skillOptions.Enabled)
        {
            return [];
        }

        var tools = new List<ITool>();
        if (skillOptions.ExposeSkillIndexTool)
        {
            tools.Add(new SkillIndexTool(_skillRegistry, skillOptions));
        }
        if (skillOptions.ExposeLoadSkillTool)
        {
            tools.Add(new LoadSkillTool(
                _skillRegistry,
                skillState,
                skillOptions,
                request.SkillSessionStore,
                BuildSkillSessionKey(request)));
        }
        return tools;
    }

    private async Task RestoreSessionSkillsAsync(
        GoldfishHarnessRequest request,
        SkillRuntimeState skillState,
        SkillOptions skillOptions,
        CancellationToken ct)
    {
        if (_skillRegistry == null
            || !skillOptions.Enabled
            || !skillOptions.PersistLoadedSkills
            || request.SkillSessionStore == null)
        {
            return;
        }

        var entries = await request.SkillSessionStore.LoadAsync(BuildSkillSessionKey(request), ct);
        foreach (var entry in entries.Take(Math.Max(0, skillOptions.MaxLoadedSkills)))
        {
            if (string.IsNullOrWhiteSpace(entry.SkillName)) continue;
            if (skillOptions.AllowedSkills.Count > 0 && !skillOptions.AllowedSkills.Contains(entry.SkillName))
            {
                continue;
            }

            var skill = _skillRegistry.Load(entry.SkillName);
            if (skill == null)
            {
                skillState.MarkRestoredMissing(entry.SkillName);
                continue;
            }

            if (skillState.CanLoad(skill.Metadata.Name))
            {
                skillState.MarkLoaded(skill);
            }
        }
    }

    private static SkillSessionKey BuildSkillSessionKey(GoldfishHarnessRequest request)
    {
        var context = GoldfishRunContext.FromAgentInfo(request.AgentInfo, request.SessionId, request.DisableConfigCache);
        return new SkillSessionKey
        {
            TenantId = GetExtra(request.AgentInfo, "TenantId") ?? string.Empty,
            UserId = context.User.Id,
            AgentId = context.Agent.Id ?? string.Empty,
            WorkspaceId = GetExtra(request.AgentInfo, "WorkspaceId") ?? string.Empty,
            SessionId = request.SessionId
        };
    }

    private static string? GetExtra(AgentInfo agentInfo, string key)
        => agentInfo.ExtraData.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static IReadOnlyList<ITool> BuildEffectiveTools(
        IList<ITool> registeredTools,
        IReadOnlyList<ITool> internalTools,
        SkillRuntimeState skillState,
        SkillOptions skillOptions)
    {
        var tools = new List<ITool>();
        tools.AddRange(internalTools);

        if (!skillOptions.Enabled || !skillOptions.RestrictToolsToLoadedSkills)
        {
            tools.AddRange(registeredTools);
            return tools;
        }

        var allowedToolIds = skillState.GetAllowedToolIds();
        if (allowedToolIds.Count == 0)
        {
            return tools;
        }

        tools.AddRange(registeredTools.Where(tool =>
            allowedToolIds.Contains(tool.Id)
            || allowedToolIds.Contains(tool.Name)));
        return tools;
    }

    private static void AppendLoadedSkillPromptIfNeeded(
        List<MsChatMessage> messages,
        ToolCallRecord record,
        SkillRuntimeState skillState)
    {
        if (!record.Success || !string.Equals(record.ToolId, "goldfish.load_skill", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var loadedSkillsPrompt = skillState.BuildLoadedSkillsPrompt();
        if (!string.IsNullOrWhiteSpace(loadedSkillsPrompt))
        {
            MergeIntoLeadingSystemMessage(messages, loadedSkillsPrompt);
        }
    }

    private static void MergeIntoLeadingSystemMessage(List<MsChatMessage> messages, string addition)
    {
        if (string.IsNullOrWhiteSpace(addition)) return;
        if (messages.Count == 0 || messages[0].Role != ChatRole.System)
        {
            messages.Insert(0, new MsChatMessage(ChatRole.System, addition.Trim()));
            return;
        }

        var existing = messages[0].Text ?? string.Empty;
        messages[0] = new MsChatMessage(ChatRole.System, $"{existing.TrimEnd()}\n\n{addition.Trim()}");
    }

    private static IEnumerable<ChatMessage> SelectShortTermHistory(
        IList<ChatMessage> history,
        MemoryLayerOptions options,
        int runnerMaxHistoryMessages)
    {
        if (!options.Enabled)
        {
            return [];
        }

        var cutoff = options.MaxAge.HasValue ? DateTime.UtcNow.Subtract(options.MaxAge.Value) : (DateTime?)null;
        var maxMessages = Math.Min(Math.Max(0, options.MaxMessages), Math.Max(0, runnerMaxHistoryMessages));
        return history
            .Where(m => options.IncludeRoles.Count == 0 || options.IncludeRoles.Contains(m.Role))
            .Where(m => cutoff == null || m.CreatedAt >= cutoff.Value)
            .OrderBy(m => m.CreatedAt)
            .TakeLast(maxMessages)
            .ToList();
    }

    private static List<ChatMessage> ApplyEstimatedPromptBudget(
        List<ChatMessage> history,
        string systemPrompt,
        string userMessage,
        MediumTermMemoryOptions options)
    {
        var budget = ContextTokenEstimator.EffectiveInputBudget(options);
        if (budget <= 0 || history.Count == 0)
        {
            return history;
        }

        var fixedTokens = ContextTokenEstimator.EstimateTextTokens(systemPrompt, options.EstimatedCharsPerToken)
            + ContextTokenEstimator.EstimateTextTokens(userMessage, options.EstimatedCharsPerToken)
            + 8;
        var result = history.ToList();
        while (result.Count > 0
            && fixedTokens + ContextTokenEstimator.EstimateMessageTokens(result, options.EstimatedCharsPerToken) > budget)
        {
            result.RemoveAt(0);
        }

        return result;
    }

    private static string BuildMemoryPrompt(MemoryContext memoryContext)
    {
        var sb = new StringBuilder();
        if (memoryContext.LongTermMemories.Count > 0)
        {
            sb.AppendLine("## 长期记忆");
            sb.AppendLine("以下是跨会话保留的用户偏好、事实或稳定背景。只在相关时使用，不要把记忆内容原样复述给用户。");
            foreach (var memory in memoryContext.LongTermMemories)
            {
                sb.AppendLine($"- [{memory.Type}{FormatMemoryCategory(memory.Category)}] {memory.Content}");
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

    private static string FormatMemoryCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? string.Empty : $"/{category}";

    private async Task<ToolCallRecord> ExecuteFunctionCallAsync(
        GoldfishHarnessRequest request,
        string runId,
        int step,
        FunctionCallContent functionCall,
        IReadOnlyDictionary<string, ToolFunction> toolFunctions,
        CancellationToken ct)
    {
        if (!toolFunctions.TryGetValue(functionCall.Name, out var toolFunction))
        {
            return new ToolCallRecord
            {
                ToolCallId = functionCall.CallId,
                ToolId = functionCall.Name,
                Arguments = SerializeArguments(functionCall.Arguments),
                Result = $"Tool not found: {functionCall.Name}",
                Success = false
            };
        }

        var arguments = SerializeArguments(functionCall.Arguments);
        var startedAt = DateTimeOffset.UtcNow;
        var authorization = await AuthorizeToolAsync(
            request,
            runId,
            toolFunction.Tool,
            arguments,
            ct);
        if (authorization.Decision != ToolAuthorizationDecision.Allow)
        {
            var authorizationRecord = BuildAuthorizationRecord(
                functionCall.CallId,
                toolFunction.Tool.Id,
                arguments,
                authorization);
            await RecordToolExecutionAsync(
                request,
                runId,
                step,
                authorizationRecord,
                startedAt,
                authorization.Decision,
                authorization.Reason,
                ct);
            return authorizationRecord;
        }

        await RecordToolIntentAsync(request, runId, step, functionCall.CallId,
            toolFunction.Tool.Id, arguments, startedAt, ct);

        try
        {
            var result = await toolFunction.InvokeAsync(new AIFunctionArguments(functionCall.Arguments), ct);
            var payload = result;
            string? displayText = null;
            IReadOnlyList<object>? attachments = null;
            if (result is ToolInvocationEnvelope env)
            {
                displayText = env.DisplayText;
                attachments = env.Attachments;
                payload = env.Data;
            }
            var serializedPayload = payload is string textPayload
                ? CompactToolResult(textPayload)
                : CompactToolResult(JsonSerializer.Serialize(payload, ToolArgumentJsonOptions));
            var record = new ToolCallRecord
            {
                ToolCallId = functionCall.CallId,
                ToolId = toolFunction.Tool.Id,
                Arguments = arguments,
                Result = serializedPayload,
                Success = true,
                DisplayText = string.IsNullOrWhiteSpace(displayText) ? null : displayText,
                Attachments = attachments
            };
            await RecordToolExecutionAsync(
                request,
                runId,
                step,
                record,
                startedAt,
                ToolAuthorizationDecision.Allow,
                null,
                ct);
            return record;
        }
        catch (Exception ex)
        {
            var record = new ToolCallRecord
            {
                ToolCallId = functionCall.CallId,
                ToolId = toolFunction.Tool.Id,
                Arguments = arguments,
                Result = ex.Message,
                Success = false
            };
            await RecordToolExecutionAsync(
                request,
                runId,
                step,
                record,
                startedAt,
                ToolAuthorizationDecision.Allow,
                ex.Message,
                ct);
            return record;
        }
    }

    private async Task<ToolCallRecord> ExecuteLegacyToolActionAsync(
        GoldfishHarnessRequest request,
        string runId,
        int step,
        GoldfishAction action,
        string toolCallId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var startedAt = DateTimeOffset.UtcNow;
        var tool = _toolRegistry.GetById(action.Tool!);
        if (tool == null)
        {
            var missingRecord = new ToolCallRecord
            {
                ToolCallId = toolCallId,
                ToolId = action.Tool!,
                Arguments = action.Arguments ?? "{}",
                Result = $"Tool not found: {action.Tool}",
                Success = false
            };
            await RecordToolExecutionAsync(
                request,
                runId,
                step,
                missingRecord,
                startedAt,
                ToolAuthorizationDecision.Deny,
                missingRecord.Result,
                ct);
            return missingRecord;
        }

        var authorization = await AuthorizeToolAsync(
            request,
            runId,
            tool,
            action.Arguments ?? "{}",
            ct);
        if (authorization.Decision != ToolAuthorizationDecision.Allow)
        {
            var authorizationRecord = BuildAuthorizationRecord(
                toolCallId,
                tool.Id,
                action.Arguments ?? "{}",
                authorization);
            await RecordToolExecutionAsync(
                request,
                runId,
                step,
                authorizationRecord,
                startedAt,
                authorization.Decision,
                authorization.Reason,
                ct);
            return authorizationRecord;
        }

        await RecordToolIntentAsync(request, runId, step, toolCallId,
            tool.Id, action.Arguments ?? "{}", startedAt, ct);

        var toolResult = await _toolRegistry.ExecuteAsync(action.Tool!, action.Arguments ?? "{}");
        var record = new ToolCallRecord
        {
            ToolCallId = toolCallId,
            ToolId = action.Tool!,
            Arguments = action.Arguments ?? "{}",
            Result = toolResult.Success ? CompactToolResult(JsonSerializer.Serialize(toolResult.Data, ToolArgumentJsonOptions)) : (toolResult.Error ?? "Unknown error"),
            Success = toolResult.Success,
            DisplayText = string.IsNullOrWhiteSpace(toolResult.DisplayText) ? null : toolResult.DisplayText,
            Attachments = toolResult.Attachments
        };
        await RecordToolExecutionAsync(
            request,
            runId,
            step,
            record,
            startedAt,
            ToolAuthorizationDecision.Allow,
            toolResult.Success ? null : record.Result,
            ct);
        return record;
    }

    private static async ValueTask<ToolAuthorizationResult> AuthorizeToolAsync(
        GoldfishHarnessRequest request,
        string runId,
        ITool tool,
        string arguments,
        CancellationToken ct)
    {
        var hook = request.ToolAuthorizationHook ?? AllowAllToolAuthorizationHook.Instance;
        var context = GoldfishRunContext.FromAgentInfo(request.AgentInfo, request.SessionId, request.DisableConfigCache);
        return await hook.AuthorizeAsync(new ToolAuthorizationRequest
        {
            RunId = runId,
            SessionId = request.SessionId,
            TenantId = GetExtra(request.AgentInfo, "TenantId"),
            UserId = context.User.Id,
            AgentId = context.Agent.Id,
            WorkspaceId = GetExtra(request.AgentInfo, "WorkspaceId"),
            ToolId = tool.Id,
            ToolName = tool.Name,
            Arguments = arguments
        }, ct);
    }

    private static ToolCallRecord BuildAuthorizationRecord(
        string? toolCallId,
        string toolId,
        string arguments,
        ToolAuthorizationResult authorization)
    {
        var message = authorization.Decision == ToolAuthorizationDecision.RequireApproval
            ? $"Tool authorization required: {authorization.Reason ?? "user approval required"}"
            : $"Tool authorization denied: {authorization.Reason ?? "not allowed"}";
        return new ToolCallRecord
        {
            ToolCallId = toolCallId,
            ToolId = toolId,
            Arguments = arguments,
            Result = message,
            Success = false,
            DisplayText = authorization.Decision == ToolAuthorizationDecision.RequireApproval
                ? $"需要用户授权后才能执行工具 {toolId}。ApprovalRequestId={authorization.ApprovalRequestId ?? string.Empty}"
                : $"工具 {toolId} 已被授权策略拒绝：{authorization.Reason ?? "not allowed"}"
        };
    }

    private static async Task RecordToolExecutionAsync(
        GoldfishHarnessRequest request,
        string runId,
        int step,
        ToolCallRecord record,
        DateTimeOffset startedAt,
        ToolAuthorizationDecision authorizationDecision,
        string? error,
        CancellationToken ct)
    {
        var store = request.ToolExecutionStore ?? NullToolExecutionStore.Instance;
        var context = GoldfishRunContext.FromAgentInfo(request.AgentInfo, request.SessionId, request.DisableConfigCache);
        await store.RecordAsync(new ToolExecutionRecord
        {
            ExecutionId = ToolExecutionId(request, runId, step, record.ToolCallId, record.ToolId),
            TurnId = request.TurnId,
            RunId = runId,
            SessionId = request.SessionId,
            TenantId = GetExtra(request.AgentInfo, "TenantId"),
            UserId = context.User.Id,
            AgentId = context.Agent.Id,
            WorkspaceId = GetExtra(request.AgentInfo, "WorkspaceId"),
            Step = step,
            ToolCallId = record.ToolCallId,
            ToolId = record.ToolId,
            ArgumentsHash = ToolExecutionHash.Sha256(record.Arguments),
            ResultHash = string.IsNullOrWhiteSpace(record.Result) ? null : ToolExecutionHash.Sha256(record.Result),
            ArgumentsJson = HarnessSensitiveData.Redact(record.Arguments),
            ResultJson = HarnessSensitiveData.Redact(record.Result),
            StructuredContentJson = ExtractStructuredContent(record.Result),
            IsError = ExtractIsError(record.Result),
            Status = record.Success ? "Completed" : "Failed",
            Success = record.Success,
            Error = error,
            AuthorizationDecision = authorizationDecision.ToString(),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    private static async Task RecordToolIntentAsync(
        GoldfishHarnessRequest request,
        string runId,
        int step,
        string? toolCallId,
        string toolId,
        string arguments,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var store = request.ToolExecutionStore ?? NullToolExecutionStore.Instance;
        var context = GoldfishRunContext.FromAgentInfo(request.AgentInfo, request.SessionId, request.DisableConfigCache);
        await store.RecordAsync(new ToolExecutionRecord
        {
            ExecutionId = ToolExecutionId(request, runId, step, toolCallId, toolId),
            TurnId = request.TurnId,
            RunId = runId,
            SessionId = request.SessionId,
            TenantId = GetExtra(request.AgentInfo, "TenantId"),
            UserId = context.User.Id,
            AgentId = context.Agent.Id,
            WorkspaceId = GetExtra(request.AgentInfo, "WorkspaceId"),
            Step = step,
            ToolCallId = toolCallId,
            ToolId = toolId,
            ArgumentsHash = ToolExecutionHash.Sha256(arguments),
            ArgumentsJson = HarnessSensitiveData.Redact(arguments),
            Success = false,
            Status = "Running",
            AuthorizationDecision = ToolAuthorizationDecision.Allow.ToString(),
            StartedAt = startedAt,
            CompletedAt = startedAt
        }, ct);
    }

    private static string ToolExecutionId(
        GoldfishHarnessRequest request,
        string runId,
        int step,
        string? toolCallId,
        string toolId)
        => ToolExecutionHash.Sha256($"{request.TurnId}\u001f{runId}\u001f{step}\u001f{toolCallId}\u001f{toolId}");

    private static string? ExtractStructuredContent(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("structuredContent", out var structured)
                ? HarnessSensitiveData.Redact(structured.GetRawText())
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool? ExtractIsError(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("isError", out var isError)
                && isError.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? isError.GetBoolean()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string BuildLegacyToolCallId(int step, string toolId)
        => $"legacy:{step}:{toolId}";

    private static object BuildFunctionResultPayload(ToolCallRecord record)
        => new
        {
            success = record.Success,
            result = BuildReadableToolResult(record),
            tool = record.ToolId
        };

    private static string BuildReadableToolResult(ToolCallRecord record)
    {
        // 工具自带可读文本时直接用，不再对 JSON 做反向格式化。
        if (record.Success && !string.IsNullOrWhiteSpace(record.DisplayText))
        {
            return record.DisplayText!;
        }

        if (!record.Success || string.IsNullOrWhiteSpace(record.Result))
        {
            return record.Result;
        }

        try
        {
            using var doc = JsonDocument.Parse(record.Result);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return CompactToolResult(root.GetString() ?? string.Empty);
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("path", out var pathProp)
                && root.TryGetProperty("items", out var itemsProp)
                && itemsProp.ValueKind == JsonValueKind.Array)
            {
                var directories = new List<string>();
                var files = new List<string>();
                foreach (var item in itemsProp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var type = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                    if (string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase))
                    {
                        directories.Add(name!);
                    }
                    else
                    {
                        files.Add(name!);
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"路径: {pathProp.GetString() ?? pathProp.ToString()}");
                if (directories.Count > 0)
                {
                    sb.AppendLine("目录:");
                    foreach (var directory in directories)
                    {
                        sb.AppendLine($"- {directory}/");
                    }
                }
                if (files.Count > 0)
                {
                    sb.AppendLine("文件:");
                    foreach (var file in files)
                    {
                        sb.AppendLine($"- {file}");
                    }
                }
                return sb.ToString().TrimEnd();
            }
        }
        catch
        {
            // Non-JSON and custom tool outputs keep their original representation.
        }

        return CompactToolResult(record.Result);
    }

    private static string CompactToolResult(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxToolResultChars)
        {
            return value;
        }

        var compactJson = TryCompactJson(value);
        if (!string.IsNullOrWhiteSpace(compactJson) && compactJson.Length < value.Length)
        {
            return compactJson.Length <= MaxToolResultChars
                ? compactJson
                : TruncateToolResult(compactJson);
        }

        return TruncateToolResult(value);
    }

    private static string TruncateToolResult(string value)
    {
        var headLength = MaxToolResultChars * 3 / 4;
        var tailLength = MaxToolResultChars - headLength;
        var omitted = value.Length - headLength - tailLength;
        return value[..headLength]
            + $"\n\n[Tool result truncated: omitted {omitted} characters. Use a narrower query or a follow-up tool call if more detail is required.]\n\n"
            + value[^tailLength..];
    }

    private static string? TryCompactJson(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var items = new List<Dictionary<string, object?>>();
                var count = 0;
                foreach (var item in root.EnumerateArray())
                {
                    count++;
                    if (items.Count >= 30 || item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var compact = new Dictionary<string, object?>();
                    CopyStringProperty(item, compact, "id");
                    CopyStringProperty(item, compact, "name");
                    CopyStringProperty(item, compact, "description", 300);
                    CopyStringProperty(item, compact, "graphIri");
                    CopyStringProperty(item, compact, "versionId");
                    if (item.TryGetProperty("routingProfile", out var routingProfile)
                        && routingProfile.ValueKind == JsonValueKind.Object)
                    {
                        var routing = new Dictionary<string, object?>();
                        CopyStringProperty(routingProfile, routing, "modelId");
                        CopyStringProperty(routingProfile, routing, "displayName");
                        CopyStringProperty(routingProfile, routing, "domain");
                        CopyStringProperty(routingProfile, routing, "description", 300);
                        CopyArrayProperty(routingProfile, routing, "objectKinds", 8);
                        CopyArrayProperty(routingProfile, routing, "capabilities", 8);
                        CopyArrayProperty(routingProfile, routing, "triggerTerms", 12);
                        if (routing.Count > 0) compact["routingProfile"] = routing;
                    }

                    items.Add(compact.Count > 0
                        ? compact
                        : new Dictionary<string, object?> { ["item"] = item.GetRawText() });
                }

                return JsonSerializer.Serialize(new
                {
                    compacted = true,
                    originalKind = "json_array",
                    totalItems = count,
                    includedItems = items.Count,
                    items
                }, ToolArgumentJsonOptions);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static void CopyStringProperty(JsonElement source, Dictionary<string, object?> target, string name, int maxChars = 0)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return;
        target[name] = maxChars > 0 && text!.Length > maxChars ? text[..maxChars] + "..." : text;
    }

    private static void CopyArrayProperty(JsonElement source, Dictionary<string, object?> target, string name, int maxItems)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return;
        var items = value.EnumerateArray()
            .Take(maxItems)
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (items.Count > 0) target[name] = items;
    }

    private static Dictionary<string, ToolFunction> BuildToolFunctions(IEnumerable<ITool> tools)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ToolFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var name = NormalizeFunctionName(tool.Name, usedNames);
            var function = new ToolFunction(tool, name);
            result[name] = function;
        }
        return result;
    }

    private static string NormalizeFunctionName(string value, HashSet<string> usedNames)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "tool" : value.Trim();
        var chars = raw.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        var normalized = new string(chars).Trim('_', '-');
        if (string.IsNullOrWhiteSpace(normalized)) normalized = "tool";
        if (normalized.Length > 64) normalized = normalized[..64].TrimEnd('_', '-');

        var candidate = normalized;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            var tail = "_" + suffix++;
            var headLength = Math.Min(normalized.Length, 64 - tail.Length);
            candidate = normalized[..headLength].TrimEnd('_', '-') + tail;
        }

        return candidate;
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
        => JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), ToolArgumentJsonOptions);

    private static string SerializeArguments(AIFunctionArguments arguments)
        => JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value), ToolArgumentJsonOptions);

    private static string BuildObservationPrompt(ToolCallRecord record)
    {
        return "工具观察结果如下，请继续 ReAct。"
            + $"\n工具: {record.ToolId}"
            + $"\n成功: {record.Success}"
            + $"\n结果:\n{BuildReadableToolResult(record)}";
    }

    private static GoldfishAction ParseReactAction(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json == null)
        {
            return new GoldfishAction(GoldfishActionKind.Final, null, null, null, raw);
        }

        try
        {
            var node = JsonNode.Parse(json)?.AsObject();
            var action = node?["action"]?.GetValue<string>() ?? "final";
            var thought = node?["thought"]?.GetValue<string>();
            if (string.Equals(action, "tool", StringComparison.OrdinalIgnoreCase))
            {
                var argsNode = node?["arguments"] ?? node?["action_input"] ?? node?["input"];
                return new GoldfishAction(
                    GoldfishActionKind.Tool,
                    thought,
                    node?["tool"]?.GetValue<string>(),
                    argsNode?.ToJsonString() ?? "{}",
                    null);
            }

            var answer = node?["answer"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(answer)
                && !string.Equals(action, "final", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "answer", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "final_answer", StringComparison.OrdinalIgnoreCase))
            {
                var argsNode = node?["arguments"] ?? node?["action_input"] ?? node?["input"];
                return new GoldfishAction(
                    GoldfishActionKind.Tool,
                    thought,
                    action,
                    argsNode?.ToJsonString() ?? "{}",
                    null);
            }

            return new GoldfishAction(
                GoldfishActionKind.Final,
                thought,
                null,
                null,
                answer ?? raw);
        }
        catch
        {
            return new GoldfishAction(GoldfishActionKind.Final, null, null, null, raw);
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw[start..(end + 1)];
    }

    private static string CleanFinalAnswer(string text)
    {
        return text.Trim().Trim('`').Trim();
    }

    private static string ExtractStreamingText(ChatResponseUpdate update)
    {
        var parts = update.Contents
            .OfType<TextContent>()
            .Select(content => content.RawRepresentation as string ?? content.Text)
            .Where(text => text is not null)
            .ToList();

        return parts.Count > 0 ? string.Concat(parts) : update.Text ?? string.Empty;
    }

    private static string NormalizeFinalAnswer(string answer)
        => string.IsNullOrWhiteSpace(answer) || answer.Contains('\n')
            ? answer
            : RestoreCompressedMarkdownBreaks(answer);

    private static string RestoreCompressedMarkdownBreaks(string answer)
    {
        var text = answer.Trim();
        text = Regex.Replace(text, @"(?<!^)(#{2,6}\s*)", "\n$1");
        text = Regex.Replace(text, @"(?<!^)(\*\*[^*\n]{1,80}\*\*)", "\n$1");
        text = Regex.Replace(
            text,
            @"(?<!^)(?<!\n)-\s*(`?[\p{L}\p{N}._@/\\-][^-\n]{0,120}?`?)(?=(?:-\s*`?[\p{L}\p{N}._@/\\-])|(?:#{2,6}\s*)|(?:\*\*[^*]+?\*\*)|$)",
            "\n- $1");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static bool LooksLikeStructuredReact(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal)
            || trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("```", StringComparison.Ordinal);
    }

    private static ChatRole ParseRole(string role)
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

    private sealed record GoldfishAction(
        GoldfishActionKind Kind,
        string? Thought,
        string? Tool,
        string? Arguments,
        string? Answer);

    private enum GoldfishActionKind
    {
        Final,
        Tool
    }

    // 工具返回的统一信封：data 给模型推理，displayText 给可读展示，attachments/metadata 给网关与 trace。
    private sealed record ToolInvocationEnvelope(
        object? Data,
        string? DisplayText,
        IReadOnlyList<object>? Attachments,
        IReadOnlyDictionary<string, object?>? Metadata);

    private sealed class ToolFunction : AIFunction
    {
        private readonly JsonElement _jsonSchema;

        public ToolFunction(ITool tool, string functionName)
        {
            Tool = tool;
            Name = functionName;
            Description = string.IsNullOrWhiteSpace(tool.Description)
                ? tool.Name
                : tool.Description.Trim();
            _jsonSchema = ParseToolSchema(tool.ParametersSchema);
        }

        public ITool Tool { get; }

        public override string Name { get; }

        public override string Description { get; }

        public override JsonElement JsonSchema => _jsonSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await Tool.IsAvailableAsync())
            {
                throw new InvalidOperationException($"Tool '{Tool.Name}' is not available");
            }

            var argumentsJson = SerializeArguments(arguments);
            var result = await Tool.ExecuteAsync(argumentsJson);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "Tool execution failed");
            }

            // 工具带了 displayText/attachments/metadata 时返回 envelope，让上层捕获；否则保持原样只回 Data。
            if (!string.IsNullOrWhiteSpace(result.DisplayText) || result.Attachments != null || result.Metadata != null)
            {
                return new ToolInvocationEnvelope(result.Data, result.DisplayText, result.Attachments, result.Metadata);
            }
            return result.Data;
        }

        private static JsonElement ParseToolSchema(string? schema)
        {
            if (!string.IsNullOrWhiteSpace(schema))
            {
                try
                {
                    var root = JsonDocument.Parse(schema).RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        return root.Clone();
                    }
                }
                catch
                {
                    // Fall through to a permissive object schema.
                }
            }

            return JsonDocument.Parse("""{"type":"object","additionalProperties":true}""").RootElement.Clone();
        }
    }
}

public sealed record GoldfishHarnessRequest(
    AgentInfo AgentInfo,
    string SessionId,
    string UserMessageText,
    MsChatMessage UserMessage,
    IList<ChatMessage> History,
    int MaxOutputTokens = 2048,
    float Temperature = 0.2f,
    bool DisableConfigCache = true,
    MemoryOptions? MemoryOptions = null,
    MemoryContext? MemoryContext = null,
    IGoldfishSteerSource? SteerSource = null,
    SkillOptions? SkillOptions = null,
    ISkillSessionStore? SkillSessionStore = null,
    IToolExecutionStore? ToolExecutionStore = null,
    IToolAuthorizationHook? ToolAuthorizationHook = null,
    ReasoningOptions? ReasoningOptions = null,
    ReasoningStrategySelection? CachedReasoningSelection = null,
    string? QueueKey = null,
    string? TurnId = null);

public interface IGoldfishSteerSource
{
    ValueTask<IReadOnlyList<string>> DrainAsync(string sessionId, CancellationToken ct);
}

public sealed record GoldfishHarnessRunResult
{
    public string Answer { get; set; } = string.Empty;
    public List<GoldfishHarnessEvent> Events { get; } = new();
    public List<ToolCallRecord> ToolCalls { get; } = new();
}

/// <summary>Harness standard event types. Hosts map these to their transport-specific events.</summary>
public enum GoldfishEventKind
{
    RunStarted,
    ThinkingDelta,
    TextDelta,
    ToolCallStarted,
    ToolResult,
    ReasoningStrategySelected,
    PlanCreated,
    PlanStepStarted,
    PlanStepCompleted,
    PlanStepFailed,
    ReflectionCompleted,
    ReWooGraphCreated,
    ReasoningTraceCompleted,
    TokenUsage,
    Completed,
    Failed,
}

public sealed record GoldfishHarnessEvent
{
    public GoldfishEventKind Kind { get; init; } = GoldfishEventKind.TextDelta;
    /// <summary>本次 run 的唯一标识，由 StreamAsync 统一注入；用于 trace 关联。</summary>
    public string RunId { get; init; } = string.Empty;
    /// <summary>单个事件的唯一标识。</summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("n");
    public int Step { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Delta { get; init; } = string.Empty;
    public string? ToolId { get; init; }
    public string? ToolCallId { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public bool? Success { get; init; }
    public IReadOnlyList<object>? Attachments { get; init; }
    public ReasoningStrategySelection? ReasoningSelection { get; init; }
    public UsageDetails? Usage { get; init; }

    public static GoldfishHarnessEvent RunStarted(string runId) => new()
    {
        Kind = GoldfishEventKind.RunStarted,
        RunId = runId,
        Step = 0
    };

    public static GoldfishHarnessEvent Thinking(int step, string text) => new()
    {
        Kind = GoldfishEventKind.ThinkingDelta,
        Step = step,
        Delta = text
    };

    public static GoldfishHarnessEvent Text(int step, string text) => new()
    {
        Kind = GoldfishEventKind.TextDelta,
        Step = step,
        Delta = text
    };

    public static GoldfishHarnessEvent Completed(int step, string answer) => new()
    {
        Kind = GoldfishEventKind.Completed,
        Step = step,
        Delta = answer
    };

    public static GoldfishHarnessEvent TokenUsage(int step, UsageDetails usage) => new()
    {
        Kind = GoldfishEventKind.TokenUsage,
        Step = step,
        Usage = usage
    };

    public static GoldfishHarnessEvent Failed(int step, string error) => new()
    {
        Kind = GoldfishEventKind.Failed,
        Step = step,
        Delta = error,
        Success = false
    };

    public static GoldfishHarnessEvent ToolCall(int step, ToolCallRecord record) => new()
    {
        Kind = GoldfishEventKind.ToolCallStarted,
        Step = step,
        ToolId = record.ToolId,
        ToolCallId = string.IsNullOrWhiteSpace(record.ToolCallId) ? $"{step}:{record.ToolId}" : record.ToolCallId,
        Arguments = record.Arguments,
        Result = string.IsNullOrWhiteSpace(record.DisplayText) ? record.Result : record.DisplayText!,
        Delta = BuildToolCallDelta(record.ToolId, record.Arguments),
        Success = record.Success,
        Attachments = record.Attachments
    };

    public static GoldfishHarnessEvent ToolCallStart(int step, string toolId, string arguments, string? toolCallId = null) => new()
    {
        Kind = GoldfishEventKind.ToolCallStarted,
        Step = step,
        ToolId = toolId,
        ToolCallId = string.IsNullOrWhiteSpace(toolCallId) ? $"{step}:{toolId}" : toolCallId,
        Arguments = arguments,
        Delta = BuildToolCallDelta(toolId, arguments),
        Success = null
    };

    public static GoldfishHarnessEvent ToolResult(int step, ToolCallRecord record) => new()
    {
        Kind = GoldfishEventKind.ToolResult,
        Step = step,
        ToolId = record.ToolId,
        ToolCallId = string.IsNullOrWhiteSpace(record.ToolCallId) ? $"{step}:{record.ToolId}" : record.ToolCallId,
        Arguments = record.Arguments,
        Result = string.IsNullOrWhiteSpace(record.DisplayText) ? record.Result : record.DisplayText!,
        Delta = BuildToolResultDelta(record.ToolId, string.IsNullOrWhiteSpace(record.DisplayText) ? record.Result : record.DisplayText!),
        Success = record.Success,
        Attachments = record.Attachments
    };

    public static GoldfishHarnessEvent ReasoningStrategySelected(ReasoningStrategySelection selection) => new()
    {
        Kind = GoldfishEventKind.ReasoningStrategySelected,
        Step = 0,
        Delta = $"已选择推理策略：{selection.Effective}（{selection.Requested} / {selection.Reason} / Reflexion={(selection.ReflexionEnabled ? "开启" : "关闭")}）",
        Result = selection.Effective.ToString(),
        Success = true,
        ReasoningSelection = selection
    };

    public static GoldfishHarnessEvent PlanCreated(ReasoningPlan plan) => new()
    {
        Kind = GoldfishEventKind.PlanCreated,
        Step = 0,
        Delta = BuildPlanCreatedDelta(plan),
        Result = JsonSerializer.Serialize(new
        {
            plan.PlanId,
            plan.Summary,
            Steps = plan.Steps.Select(step => new
            {
                step.Index,
                step.Title,
                step.Description
            })
        }),
        Success = true
    };

    private static string BuildPlanCreatedDelta(ReasoningPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("已创建执行计划：");
        sb.AppendLine(plan.Summary);
        foreach (var step in plan.Steps)
        {
            sb.Append(step.Index);
            sb.Append(". ");
            sb.Append(step.Title);
            if (!string.IsNullOrWhiteSpace(step.Description)
                && !string.Equals(step.Description, step.Title, StringComparison.Ordinal))
            {
                sb.Append(" - ");
                sb.Append(step.Description);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public static GoldfishHarnessEvent PlanStepStarted(ReasoningPlanStep step) => new()
    {
        Kind = GoldfishEventKind.PlanStepStarted,
        Step = step.Index,
        Delta = $"开始计划步骤 {step.Index}: {step.Title}",
        Result = step.Description,
        Success = null
    };

    public static GoldfishHarnessEvent PlanStepCompleted(ReasoningPlanStep step, string result) => new()
    {
        Kind = GoldfishEventKind.PlanStepCompleted,
        Step = step.Index,
        Delta = $"完成计划步骤 {step.Index}: {step.Title}",
        Result = result,
        Success = true
    };

    public static GoldfishHarnessEvent PlanStepFailed(ReasoningPlanStep step, string error) => new()
    {
        Kind = GoldfishEventKind.PlanStepFailed,
        Step = step.Index,
        Delta = $"计划步骤 {step.Index} 未完成: {step.Title}",
        Result = error,
        Success = false
    };

    public static GoldfishHarnessEvent ReWooGraphCreated(ReasoningReWooGraph graph) => new()
    {
        Kind = GoldfishEventKind.ReWooGraphCreated,
        Step = 0,
        Delta = BuildReWooGraphCreatedDelta(graph),
        Result = JsonSerializer.Serialize(new
        {
            graph.GraphId,
            graph.Summary,
            Nodes = graph.Nodes.Select(node => new
            {
                node.Id,
                node.Tool,
                node.Purpose,
                node.ArgumentsJson
            })
        }),
        Success = true
    };

    public static GoldfishHarnessEvent ReflectionCompleted(ReasoningReflection reflection) => new()
    {
        Kind = GoldfishEventKind.ReflectionCompleted,
        Step = 0,
        Delta = reflection.Revised
            ? $"Reflexion 已修正最终答案：{reflection.Reason}"
            : $"Reflexion 已完成校验：{reflection.Reason}",
        Result = reflection.Answer,
        Success = true
    };

    public static GoldfishHarnessEvent ReasoningTraceCompleted(IReadOnlyList<GoldfishHarnessEvent> events) => new()
    {
        Kind = GoldfishEventKind.ReasoningTraceCompleted,
        Step = 0,
        Delta = BuildReasoningTraceDelta(events),
        Success = true
    };

    private static string BuildToolCallDelta(string toolId, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments.Trim() == "{}")
        {
            return $"调用工具 {toolId}";
        }

        return $"调用工具 {toolId}\n参数: {TruncateForProcess(arguments.Trim(), 500)}";
    }

    private static string BuildReWooGraphCreatedDelta(ReasoningReWooGraph graph)
    {
        var sb = new StringBuilder();
        sb.Append("已创建 ReWOO 工具图：");
        sb.AppendLine(graph.Summary);
        foreach (var node in graph.Nodes)
        {
            sb.Append(node.Id);
            sb.Append(". ");
            sb.Append(node.Tool);
            if (!string.IsNullOrWhiteSpace(node.Purpose))
            {
                sb.Append(" - ");
                sb.Append(node.Purpose);
            }

            if (!string.IsNullOrWhiteSpace(node.ArgumentsJson) && node.ArgumentsJson.Trim() != "{}")
            {
                sb.AppendLine();
                sb.Append("   参数: ");
                sb.Append(TruncateForProcess(node.ArgumentsJson.Trim(), 500));
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildToolResultDelta(string toolId, string result)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return result;
        if (result.StartsWith($"工具 {toolId} 结果", StringComparison.Ordinal)
            || result.StartsWith($"Tool {toolId} result", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }
        return $"工具 {toolId} 结果:\n{result}";
    }

    private static string BuildReasoningTraceDelta(IReadOnlyList<GoldfishHarnessEvent> events)
    {
        var items = new List<string>();
        foreach (var ev in events)
        {
            if (!TryFormatReasoningTraceItem(ev, out var item))
            {
                continue;
            }

            if (items.Count > 0 && string.Equals(items[^1], item, StringComparison.Ordinal))
            {
                continue;
            }

            items.Add(item);
        }

        if (items.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("【推理策略执行过程】");
        for (var i = 0; i < items.Count; i++)
        {
            sb.Append(i + 1);
            sb.Append(". ");
            sb.AppendLine(items[i]);
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryFormatReasoningTraceItem(GoldfishHarnessEvent ev, out string item)
    {
        item = string.Empty;
        switch (ev.Kind)
        {
            case GoldfishEventKind.ReasoningStrategySelected:
                if (ev.ReasoningSelection is { } selection)
                {
                    item = $"策略选择：Requested={selection.Requested}，Effective={selection.Effective}，Reason={selection.Reason}，Reflexion={(selection.ReflexionEnabled ? "开启" : "关闭")}";
                }
                else
                {
                    item = $"策略选择：{Fallback(ev.Result, ev.Delta, "未知")}";
                }
                return true;
            case GoldfishEventKind.PlanCreated:
                item = "创建执行计划：" + CompactMultiline(Fallback(ev.Delta, ev.Result, "已创建执行计划。"));
                return true;
            case GoldfishEventKind.PlanStepStarted:
                item = CompactMultiline(Fallback(ev.Delta, null, $"开始计划步骤 {ev.Step}。"));
                return true;
            case GoldfishEventKind.PlanStepCompleted:
                item = CompactMultiline(Fallback(ev.Delta, ev.Result, $"完成计划步骤 {ev.Step}。"));
                return true;
            case GoldfishEventKind.PlanStepFailed:
                item = CompactMultiline(Fallback(ev.Delta, ev.Result, $"计划步骤 {ev.Step} 未完成。"));
                return true;
            case GoldfishEventKind.ReWooGraphCreated:
                item = "创建 ReWOO 工具图：" + CompactMultiline(Fallback(ev.Delta, ev.Result, "已创建 ReWOO 工具图。"));
                return true;
            case GoldfishEventKind.ToolCallStarted:
                item = string.IsNullOrWhiteSpace(ev.ToolId)
                    ? "调用工具。"
                    : $"调用工具：{ev.ToolId}" + (string.IsNullOrWhiteSpace(ev.Arguments) || ev.Arguments.Trim() == "{}"
                        ? string.Empty
                        : $"，参数={TruncateForProcess(ev.Arguments.Trim(), 300)}");
                return true;
            case GoldfishEventKind.ToolResult:
                item = $"工具返回：{Fallback(ev.ToolId, null, "tool")}，状态={(ev.Success == false ? "失败" : "成功")}";
                if (!string.IsNullOrWhiteSpace(ev.Result))
                {
                    item += $"，摘要={TruncateForProcess(CompactMultiline(ev.Result), 300)}";
                }
                return true;
            case GoldfishEventKind.ReflectionCompleted:
                item = CompactMultiline(Fallback(ev.Delta, null, "Reflexion 已完成校验。"));
                return true;
            default:
                return false;
        }
    }

    private static string CompactMultiline(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ");

    private static string Fallback(string? value, string? second, string fallback)
        => !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : !string.IsNullOrWhiteSpace(second)
                ? second.Trim()
                : fallback;

    private static string TruncateForProcess(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        return value[..maxChars] + "...";
    }
}
