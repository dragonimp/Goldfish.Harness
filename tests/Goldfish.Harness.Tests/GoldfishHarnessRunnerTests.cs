using System.Reflection;
using System.Text.Json;
using Goldfish.Harness;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class GoldfishHarnessRunnerTests
{
    [Fact]
    public void OpenAiChatClient_ResolvesChatCompletionsEndpoint()
    {
        Assert.Equal(
            "https://llm.example/v1/chat/completions",
            WhitespacePreservingOpenAiChatClient.ResolveChatCompletionsEndpoint("https://llm.example/v1"));
        Assert.Equal(
            "https://llm.example/v1/chat/completions",
            WhitespacePreservingOpenAiChatClient.ResolveChatCompletionsEndpoint("https://llm.example/v1/responses"));
        Assert.Equal(
            "https://llm.example/v1/chat/completions",
            WhitespacePreservingOpenAiChatClient.ResolveChatCompletionsEndpoint("https://llm.example/v1/chat/completions"));
    }

    [Fact]
    public void OpenAiChatClient_RetriesOnlyTransientStatuses()
    {
        var method = typeof(WhitespacePreservingOpenAiChatClient).GetMethod(
            "IsTransientStatus",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.True(Invoke(System.Net.HttpStatusCode.InternalServerError));
        Assert.True(Invoke(System.Net.HttpStatusCode.BadGateway));
        Assert.False(Invoke(System.Net.HttpStatusCode.BadRequest));
        Assert.False(Invoke(System.Net.HttpStatusCode.Unauthorized));

        bool Invoke(System.Net.HttpStatusCode statusCode)
            => Assert.IsType<bool>(method.Invoke(null, [statusCode]));
    }

    [Fact]
    public void OpenAiChatClient_RecognizesReasoningContentDeltas()
    {
        var method = typeof(WhitespacePreservingOpenAiChatClient).GetMethod(
            "ReadReasoningText",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        using var document = JsonDocument.Parse("""{"reasoning_content":"真实思考","content":"正文"}""");

        var text = Assert.IsType<string>(method.Invoke(null, [document.RootElement]));

        Assert.Equal("真实思考", text);
    }

    [Fact]
    public void OpenAiChatClient_SerializesRequiredSpecificToolChoice()
    {
        var method = typeof(WhitespacePreservingOpenAiChatClient).GetMethod(
            "ResolveToolChoice",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var choice = method.Invoke(null,
        [
            new ChatOptions { ToolMode = ChatToolMode.RequireSpecific("family_reward_apply_matching_rule") },
            1
        ]);

        Assert.Equal(
            "{\"type\":\"function\",\"function\":{\"name\":\"family_reward_apply_matching_rule\"}}",
            JsonSerializer.Serialize(choice));
    }

    [Fact]
    public void GoldfishLlmHeaders_UseGatewayRequestIdAsAppRequestId()
    {
        using var document = JsonDocument.Parse("""
        {
          "GatewayRequestId": "req_trace_123",
          "GatewayUserId": "wzs",
          "GatewayType": "WebApp",
          "SessionId": "session-1"
        }
        """);
        var metadata = document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);

        var headers = GoldfishLlmHeaders.Build(metadata);

        Assert.Equal("req_trace_123", headers["X-LLMFree-App-Request-Id"]);
        Assert.Equal("wzs", headers["X-LLMFree-App-User"]);
        Assert.Equal("Goldfish", headers["X-LLMFree-Agent-Type"]);
    }

    [Fact]
    public void ReasoningStrategySelector_AutoUsesStructuralFeaturesWithoutKeywordSignals()
    {
        var selection = ReasoningStrategySelector.Select(
            """
            1. A，B。
            2. C，D。
            3. E，F。
            """,
            new ReasoningOptions { Strategy = ReasoningStrategyKind.Auto });

        Assert.Equal(ReasoningStrategyKind.Auto, selection.Requested);
        Assert.Equal(ReasoningStrategyKind.PlanAndExecute, selection.Effective);
        Assert.True(selection.ReflexionEnabled);
        Assert.StartsWith("auto-structural:", selection.Reason);
    }

    [Fact]
    public void ReasoningOptions_DefaultsToReact()
    {
        Assert.Equal(ReasoningStrategyKind.ReAct, ReasoningOptions.Default.Strategy);
        Assert.Equal(ReasoningStrategyKind.ReAct, new ReasoningOptions().Strategy);
    }

    [Fact]
    public void ReasoningStrategySelector_AutoHonorsUserDirectedPlanMode()
    {
        var selection = ReasoningStrategySelector.Select(
            "我希望你走plan模式，进行mcp服务的获取和分析。",
            new ReasoningOptions { Strategy = ReasoningStrategyKind.Auto });

        Assert.Equal(ReasoningStrategyKind.Auto, selection.Requested);
        Assert.Equal(ReasoningStrategyKind.PlanAndExecute, selection.Effective);
        Assert.True(selection.ReflexionEnabled);
        Assert.Equal("auto-user-directed:PlanAndExecute", selection.Reason);
    }

    [Fact]
    public void ReasoningStrategySelector_AutoDoesNotTreatComparisonAsUserDirectedMode()
    {
        var selection = ReasoningStrategySelector.TrySelectUserDirectedStrategy(
            "请解释 react 和 plan 模式有什么区别。",
            new ReasoningOptions { Strategy = ReasoningStrategyKind.Auto });

        Assert.Null(selection);
    }

    [Fact]
    public void ReasoningStrategySelector_ExplicitReWooOverridesAuto()
    {
        var selection = ReasoningStrategySelector.Select(
            "简单问题",
            new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.ReWOO,
                EnableReflexion = false
            });

        Assert.Equal(ReasoningStrategyKind.ReWOO, selection.Requested);
        Assert.Equal(ReasoningStrategyKind.ReWOO, selection.Effective);
        Assert.False(selection.ReflexionEnabled);
        Assert.Equal("request-explicit", selection.Reason);
    }

    [Fact]
    public async Task ReasoningStrategyDecider_AutoCallsClassifierEvenWhenSessionCacheExists()
    {
        var chatClient = new QueueChatClient("""{"strategy":"ReAct","confidence":0.90,"reason":"simple"}""");
        var decider = new DefaultReasoningStrategyDecider(chatClient);
        var cached = new ReasoningStrategySelection(
            ReasoningStrategyKind.Auto,
            ReasoningStrategyKind.PlanAndExecute,
            true,
            "auto-classifier:0.90:previous");

        var selection = await decider.SelectAsync(
            new ReasoningStrategyDecisionRequest(
                "session-1",
                "一句简单问题",
                new ReasoningOptions { Strategy = ReasoningStrategyKind.Auto },
                cached),
            TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningStrategyKind.ReAct, selection.Effective);
        Assert.StartsWith("auto-classifier:0.90", selection.Reason);
        Assert.Single(chatClient.Calls);
    }

    [Fact]
    public async Task ReasoningStrategyDecider_DefaultOptionsSkipsClassifier()
    {
        var chatClient = new QueueChatClient();
        var decider = new DefaultReasoningStrategyDecider(chatClient);

        var selection = await decider.SelectAsync(
            new ReasoningStrategyDecisionRequest(
                "session-1",
                "包含多个步骤的普通请求",
                Options: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningStrategyKind.ReAct, selection.Requested);
        Assert.Equal(ReasoningStrategyKind.ReAct, selection.Effective);
        Assert.Equal("request-explicit", selection.Reason);
        Assert.Empty(chatClient.Calls);
    }

    [Fact]
    public async Task ReasoningStrategyDecider_UserDirectedModeSkipsClassifierEvenWhenSessionCacheExists()
    {
        var chatClient = new QueueChatClient();
        var decider = new DefaultReasoningStrategyDecider(chatClient);
        var cached = new ReasoningStrategySelection(
            ReasoningStrategyKind.Auto,
            ReasoningStrategyKind.ReAct,
            true,
            "auto-classifier:0.90:previous");

        var selection = await decider.SelectAsync(
            new ReasoningStrategyDecisionRequest(
                "session-1",
                "我希望你走plan模式，进行mcp服务的获取和分析。",
                new ReasoningOptions { Strategy = ReasoningStrategyKind.Auto },
                cached),
            TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningStrategyKind.PlanAndExecute, selection.Effective);
        Assert.Equal("auto-user-directed:PlanAndExecute", selection.Reason);
        Assert.Empty(chatClient.Calls);
    }

    [Fact]
    public async Task ReasoningStrategyDecider_ReevaluatesAutoWhenRequested()
    {
        var chatClient = new QueueChatClient("""{"strategy":"ReAct","confidence":0.88,"reason":"simple"}""");
        var decider = new DefaultReasoningStrategyDecider(chatClient);
        var cached = new ReasoningStrategySelection(
            ReasoningStrategyKind.Auto,
            ReasoningStrategyKind.PlanAndExecute,
            true,
            "auto-classifier:0.90:previous");

        var selection = await decider.SelectAsync(
            new ReasoningStrategyDecisionRequest(
                "session-1",
                "一句简单问题",
                new ReasoningOptions
                {
                    Strategy = ReasoningStrategyKind.Auto,
                    ReevaluateAutoStrategyEveryTurn = true
                },
                cached),
            TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningStrategyKind.ReAct, selection.Effective);
        Assert.StartsWith("auto-classifier:0.88", selection.Reason);
        Assert.Single(chatClient.Calls);
    }

    [Fact]
    public void BuildMessages_IncludesReasoningStrategyInLeadingSystemMessage()
    {
        var runner = new GoldfishHarnessRunner(
            null!,
            new ToolRegistry(),
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "多源检索并汇总",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "多源检索并汇总"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.ReWOO,
                EnableReflexion = false
            });

        var buildMessages = typeof(GoldfishHarnessRunner).GetMethod(
            "BuildMessages",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(GoldfishHarnessRequest)]);

        var messages = Assert.IsType<List<Microsoft.Extensions.AI.ChatMessage>>(
            buildMessages!.Invoke(runner, [request]));

        var system = Assert.Single(messages, message => message.Role == ChatRole.System);
        Assert.Same(messages[0], system);
        Assert.Contains("## 推理策略", system.Text);
        Assert.Contains("Effective: ReWOO", system.Text);
        Assert.Contains("Reflexion: 关闭", system.Text);
    }

    [Fact]
    public void ReasoningStrategySelectedEvent_CarriesEffectiveStrategy()
    {
        var selection = new ReasoningStrategySelection(
            ReasoningStrategyKind.Auto,
            ReasoningStrategyKind.PlanAndExecute,
            true,
            "auto-classifier:0.91:multi-step");

        var ev = GoldfishHarnessEvent.ReasoningStrategySelected(selection);

        Assert.Equal(GoldfishEventKind.ReasoningStrategySelected, ev.Kind);
        Assert.Equal("PlanAndExecute", ev.Result);
        Assert.True(ev.Success);
        Assert.Contains("auto-classifier:0.91", ev.Delta);
        Assert.Contains("已选择推理策略：PlanAndExecute", ev.Delta);
        Assert.Same(selection, ev.ReasoningSelection);
    }

    [Fact]
    public async Task RunAsync_AutoUsesClassifierToSelectPlanAndExecute()
    {
        var chatClient = new QueueChatClient(
            """{"strategy":"PlanAndExecute","confidence":0.91,"reason":"multi-step"}""",
            """
            {"summary":"先规划后执行","steps":[{"title":"确认","description":"确认范围"},{"title":"落地","description":"执行任务"}]}
            """,
            "执行完成");
        var runner = new GoldfishHarnessRunner(
            chatClient,
            new ToolRegistry(),
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "处理这个任务",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "处理这个任务"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.Auto
            });

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("执行完成", result.Answer);
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.ReasoningStrategySelected
            && ev.Result == "PlanAndExecute"
            && ev.Delta.Contains("auto-classifier:0.91"));
        Assert.Contains(result.Events, ev => ev.Kind == GoldfishEventKind.PlanCreated);
        Assert.Equal(3, chatClient.Calls.Count);
        Assert.Contains("推理策略分类器", chatClient.Calls[0][0].Text);
        Assert.Contains("如果用户本轮明确要求", chatClient.Calls[0][0].Text);
        Assert.Contains("我希望你走 plan 模式", chatClient.Calls[0][0].Text);
        Assert.Contains("请为当前用户请求生成一个简洁执行计划", chatClient.Calls[1].Last().Text);
        Assert.Contains("## 当前执行计划", chatClient.Calls[2][0].Text);
    }

    [Fact]
    public async Task RunAsync_AutoUserDirectedPlanEmitsPlanSelectionInsteadOfCachedReAct()
    {
        var chatClient = new QueueChatClient(
            """
            {"summary":"获取并分析 MCP 服务","steps":[{"title":"获取","description":"获取 MCP 服务"},{"title":"分析","description":"分析服务能力"}]}
            """,
            "已完成 MCP 服务获取和分析。");
        var runner = new GoldfishHarnessRunner(
            chatClient,
            new ToolRegistry(),
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "我希望你走plan模式，进行mcp服务的获取和分析。",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "我希望你走plan模式，进行mcp服务的获取和分析。"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.Auto
            },
            CachedReasoningSelection: new ReasoningStrategySelection(
                ReasoningStrategyKind.Auto,
                ReasoningStrategyKind.ReAct,
                true,
                "session-cache:session-cache:auto-classifier:0.90:用户发送了简单的问候语，属于单步交互，无需复杂规划或多步推理。"));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("已完成 MCP 服务获取和分析。", result.Answer);
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.ReasoningStrategySelected
            && ev.Result == "PlanAndExecute"
            && ev.Delta.Contains("Auto / auto-user-directed:PlanAndExecute"));
        Assert.Contains(result.Events, ev => ev.Kind == GoldfishEventKind.PlanCreated);
        Assert.Equal(2, chatClient.Calls.Count);
        Assert.DoesNotContain(result.Events, ev => ev.Delta.Contains("session-cache:session-cache"));
    }

    [Fact]
    public async Task RunAsync_PlanAndExecute_CreatesPlanAndAppendsItToExecutionSystemPrompt()
    {
        var chatClient = new QueueChatClient(
            """
            {"summary":"先分析再执行","steps":[{"title":"分析","description":"确认任务边界"},{"title":"执行","description":"完成修改"}]}
            """,
            "完成了");
        var runner = new GoldfishHarnessRunner(
            chatClient,
            new ToolRegistry(),
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "请重构并测试",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "请重构并测试"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.PlanAndExecute,
                MaxPlanSteps = 3
            });

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("完成了", result.Answer);
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.PlanCreated
            && ev.Delta.Contains("已创建执行计划：先分析再执行")
            && ev.Delta.Contains("1. 分析 - 确认任务边界")
            && ev.Delta.Contains("2. 执行 - 完成修改"));
        Assert.Contains(result.Events, ev => ev.Kind == GoldfishEventKind.PlanStepStarted && ev.Step == 1);
        Assert.Contains(result.Events, ev => ev.Kind == GoldfishEventKind.PlanStepCompleted && ev.Step == 1);
        Assert.Contains(result.Events, ev => ev.Kind == GoldfishEventKind.PlanStepStarted && ev.Step == 2);
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.PlanStepCompleted
            && ev.Step == 2
            && ev.Result == "已由最终回答覆盖完成。");
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.ReasoningTraceCompleted
            && ev.Delta.Contains("【推理策略执行过程】")
            && ev.Delta.Contains("策略选择：Requested=PlanAndExecute，Effective=PlanAndExecute")
            && ev.Delta.Contains("创建执行计划")
            && ev.Delta.Contains("完成计划步骤 2"));
        Assert.Equal(2, chatClient.Calls.Count);
        Assert.Contains("请为当前用户请求生成一个简洁执行计划", chatClient.Calls[0].Last().Text);
        Assert.Contains("## 当前执行计划", chatClient.Calls[1][0].Text);
        Assert.Contains("分析: 确认任务边界", chatClient.Calls[1][0].Text);
    }

    [Fact]
    public async Task RunAsync_ReWoo_CreatesGraphExecutesToolNodesAndAppendsObservations()
    {
        var chatClient = new QueueChatClient(
            """
            {"summary":"收集证据后综合","nodes":[{"id":"N1","tool":"recording_tool","purpose":"读取测试证据","arguments":{"value":42}}]}
            """,
            "基于工具观察完成汇总");
        var registry = new ToolRegistry();
        var tool = new RecordingTool();
        registry.Register(tool);
        var runner = new GoldfishHarnessRunner(
            chatClient,
            registry,
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "请多源检索并汇总",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "请多源检索并汇总"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.ReWOO
            });

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("基于工具观察完成汇总", result.Answer);
        Assert.True(tool.Executed);
        Assert.Contains("\"value\":42", tool.LastArguments);
        Assert.Contains(result.Events, ev => ev.Kind == GoldfishEventKind.ReWooGraphCreated && ev.Delta.Contains("收集证据后综合"));
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.ReWooGraphCreated
            && ev.Delta.Contains("N1. recording_tool - 读取测试证据")
            && ev.Delta.Contains("参数:"));
        Assert.Contains(result.Events, ev => ev.Kind == GoldfishEventKind.ToolResult && ev.ToolId == "recording.tool" && ev.Step == 0);
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.ReasoningTraceCompleted
            && ev.Delta.Contains("创建 ReWOO 工具图")
            && ev.Delta.Contains("调用工具：recording.tool")
            && ev.Delta.Contains("工具返回：recording.tool，状态=成功"));
        Assert.Equal(2, chatClient.Calls.Count);
        Assert.Contains("请为当前用户请求生成 ReWOO 工具图", chatClient.Calls[0].Last().Text);
        Assert.Contains("## 当前 ReWOO 工具图", chatClient.Calls[1][0].Text);
        Assert.Contains("[ReWOO 工具图观察]", chatClient.Calls[1].Last().Text);
    }

    [Fact]
    public async Task RunAsync_Reflexion_RevisesFinalAnswerWhenConstraintsAreMissed()
    {
        var chatClient = new QueueChatClient(
            "不是 JSON",
            """{"action":"revise","reason":"必须输出 JSON","answer":"{\"ok\":true}"}""");
        var runner = new GoldfishHarnessRunner(
            chatClient,
            new ToolRegistry(),
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "必须只输出 JSON",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "必须只输出 JSON"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.ReAct,
                EnableReflexion = true,
                MaxReflectionRetries = 1
            });

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\":true}", result.Answer);
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.ReflectionCompleted
            && ev.Delta.Contains("Reflexion 已修正最终答案")
            && ev.Result == "{\"ok\":true}");
        Assert.Contains(result.Events, ev =>
            ev.Kind == GoldfishEventKind.ReasoningTraceCompleted
            && ev.Delta.Contains("策略选择：Requested=ReAct，Effective=ReAct")
            && ev.Delta.Contains("Reflexion 已修正最终答案"));
        Assert.Equal(2, chatClient.Calls.Count);
        Assert.Contains("Reflexion 校验器", chatClient.Calls[1].Last().Text);
    }

    [Fact]
    public async Task StreamAsync_FinalTextOnlyRejectsProvisionalAnswerAndContinues()
    {
        var chatClient = new QueueChatClient(
            "好的，正在为您查询所有孩子的积分。",
            "玥玥当前有 140.50 分。");
        var runner = new GoldfishHarnessRunner(
            chatClient,
            new ToolRegistry(),
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "家庭积分应用",
                SystemPrompt = "查询必须完成后再回答。",
                ExtraData = new Dictionary<string, string>
                {
                    ["RuntimeResponseMode"] = "final_text_only"
                }
            },
            "speaker-session",
            "查询悦悦的积分",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "查询悦悦的积分"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.ReAct
            });

        var events = new List<GoldfishHarnessEvent>();
        await foreach (var ev in runner.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        Assert.DoesNotContain(events, ev =>
            ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta.Contains("请稍等"));
        Assert.Contains(events, ev =>
            ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta == "玥玥当前有 140.50 分。");
        Assert.Contains(events, ev =>
            ev.Kind == GoldfishEventKind.ReasoningTraceCompleted
            && ev.Delta.Contains("【推理策略执行过程】")
            && ev.Delta.Contains("策略选择：Requested=ReAct，Effective=ReAct"));
        Assert.Equal(2, chatClient.Calls.Count);
        Assert.Contains("通道最终答复校验", chatClient.Calls[1].Last().Text);
    }

    [Fact]
    public async Task RunAsync_RequiredToolWithoutSuccess_ReturnsSafeIncompleteResponse()
    {
        var chatClient = new QueueChatClient("玥玥当前有 100 分。");
        var runner = new GoldfishHarnessRunner(
            chatClient,
            new ToolRegistry(),
            maxReactSteps: 1,
            skillRegistry: null);
        var request = RequiredFamilyRewardRequest("查询玥玥积分");

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("当前查询未完成，暂不能提供积分数值，请稍后重试。", result.Answer);
        Assert.DoesNotContain("100", result.Answer);
        Assert.Single(chatClient.Calls);
    }

    [Fact]
    public async Task StreamAsync_RequiredToolWithoutSuccess_NeverStreamsModelScore()
    {
        var chatClient = new QueueChatClient("玥玥当前有 100 分。");
        var runner = new GoldfishHarnessRunner(
            chatClient,
            new ToolRegistry(),
            maxReactSteps: 1,
            skillRegistry: null);
        var request = RequiredFamilyRewardRequest("查询玥玥积分");
        var events = new List<GoldfishHarnessEvent>();

        await foreach (var ev in runner.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        Assert.DoesNotContain(events, ev => ev.Kind == GoldfishEventKind.TextDelta && ev.Delta.Contains("100"));
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta == "当前查询未完成，暂不能提供积分数值，请稍后重试。");
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.Completed
            && ev.Delta == "当前查询未完成，暂不能提供积分数值，请稍后重试。");
        Assert.Single(chatClient.Calls);
    }

    [Fact]
    public async Task StreamAsync_RequiredFamilyRewardTool_DoesNotAcceptAnUnrelatedSuccessfulTool()
    {
        var chatClient = new QueueChatClient(
            """{"action":"tool","tool":"goldfish_skill_index","arguments":{}}""",
            "玥玥当前有 100 分。");
        var registry = new ToolRegistry();
        registry.Register(new NamedRecordingTool("goldfish_skill_index"));
        var runner = new GoldfishHarnessRunner(
            chatClient,
            registry,
            maxReactSteps: 2,
            skillRegistry: null);
        var events = new List<GoldfishHarnessEvent>();

        await foreach (var ev in runner.StreamAsync(
            RequiredFamilyRewardRequest("查询玥玥积分"),
            TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.ToolResult
            && ev.ToolId == "goldfish_skill_index"
            && ev.Success == true);
        Assert.DoesNotContain(events, ev => ev.Kind == GoldfishEventKind.TextDelta && ev.Delta.Contains("100"));
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta == "当前查询未完成，暂不能提供积分数值，请稍后重试。");
    }

    [Fact]
    public async Task StreamAsync_FailedScoreAdjustment_DoesNotAcceptAnotherFamilyRewardTool()
    {
        var chatClient = new QueueChatClient(
            """{"action":"tool","tool":"family_reward_adjust_score","arguments":{}}""",
            """{"action":"tool","tool":"family_reward_list_children","arguments":{}}""",
            "已给玥玥加 2 分，当前积分 10 分。");
        var registry = new ToolRegistry();
        registry.Register(new ResultTool("family_reward_adjust_score", success: false));
        registry.Register(new ResultTool("family_reward_list_children", success: true));
        var runner = new GoldfishHarnessRunner(
            chatClient,
            registry,
            maxReactSteps: 3,
            skillRegistry: null);
        var events = new List<GoldfishHarnessEvent>();

        await foreach (var ev in runner.StreamAsync(
            RequiredFamilyRewardRequest("对"),
            TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.ToolResult
            && ev.ToolId == "family_reward_adjust_score"
            && ev.Success == false);
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.ToolResult
            && ev.ToolId == "family_reward_list_children"
            && ev.Success == true);
        Assert.DoesNotContain(events, ev => ev.Kind == GoldfishEventKind.TextDelta
            && (ev.Delta.Contains("已给玥玥加") || ev.Delta.Contains("10 分")));
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta == "当前操作未完成，未进行积分变更，请稍后重试。");
    }

    [Fact]
    public async Task StreamAsync_RuleBasedScoreAddition_RequiresBalanceVerificationAfterWrite()
    {
        var chatClient = new QueueChatClient(
            """{"action":"tool","tool":"family_reward_query_rules","arguments":{}}""",
            "已查到规则，现在为玥玥加分。",
            """{"action":"tool","tool":"family_reward_apply_matching_rule","arguments":{}}""",
            "已按规则给玥玥加分。",
            """{"action":"tool","tool":"family_reward_query_score","arguments":{}}""",
            "已按“主动帮助别人”规则给玥玥加 2 分，首页余额已核验为 12 分。");
        var registry = new ToolRegistry();
        registry.Register(new ResultTool("family_reward_query_rules", success: true));
        registry.Register(new ResultTool("family_reward_apply_matching_rule", success: true));
        registry.Register(new ResultTool("family_reward_query_score", success: true));
        var runner = new GoldfishHarnessRunner(
            chatClient,
            registry,
            maxReactSteps: 6,
            skillRegistry: null);
        var events = new List<GoldfishHarnessEvent>();

        await foreach (var ev in runner.StreamAsync(
            RequiredFamilyRewardRequest("玥玥主动帮助别人，加分"),
            TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.ToolResult
            && ev.ToolId == "family_reward_query_rules"
            && ev.Success == true);
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.ToolResult
            && ev.ToolId == "family_reward_apply_matching_rule"
            && ev.Success == true);
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.ToolResult
            && ev.ToolId == "family_reward_query_score"
            && ev.Success == true);
        Assert.Contains(events, ev => ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta.Contains("首页余额已核验为 12 分"));
        Assert.Equal(6, chatClient.Calls.Count);
        Assert.Contains("家庭积分规则记分", chatClient.Calls[2].Last().Text);
        Assert.Contains("家庭积分余额核验", chatClient.Calls[4].Last().Text);
    }

    [Fact]
    public async Task StreamAsync_FinalTextOnlyContinuesAfterUnresolvedExactNameMiss()
    {
        var chatClient = new QueueChatClient(
            "系统中没有找到“悦悦”这个名字的孩子，请确认姓名是否正确。",
            "你说的是玥玥吗？她当前有 140.50 分。");
        var registry = new ToolRegistry();
        registry.Register(new RecordingTool());
        var runner = new GoldfishHarnessRunner(
            chatClient,
            registry,
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "家庭积分应用",
                SystemPrompt = "没有精确命中时必须继续查询候选。",
                ExtraData = new Dictionary<string, string>
                {
                    ["RuntimeResponseMode"] = "final_text_only"
                }
            },
            "speaker-session",
            "查询悦悦的积分",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "查询悦悦的积分"),
            [],
            ReasoningOptions: new ReasoningOptions
            {
                Strategy = ReasoningStrategyKind.ReAct
            });

        var events = new List<GoldfishHarnessEvent>();
        await foreach (var ev in runner.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        Assert.DoesNotContain(events, ev =>
            ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta.Contains("没有找到"));
        Assert.Contains(events, ev =>
            ev.Kind == GoldfishEventKind.TextDelta
            && ev.Delta == "你说的是玥玥吗？她当前有 140.50 分。");
        Assert.Equal(2, chatClient.Calls.Count);
        Assert.Contains("完整候选", chatClient.Calls[1].Last().Text);
    }

    [Fact]
    public void ReasoningPlanParser_FallsBackToSingleStepForUnstructuredPlannerOutput()
    {
        var plan = ReasoningPlanParser.ParseOrFallback("not json", "修复并测试", 1);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(1, step.Index);
        Assert.Equal("完成请求", step.Title);
        Assert.Equal("修复并测试", step.Description);
    }

    [Fact]
    public void ReasoningPlanParser_AddsObservableFinalStepWhenPlannerReturnsOneStep()
    {
        var plan = ReasoningPlanParser.ParseOrFallback(
            """{"summary":"统计积分","steps":[{"title":"获取孩子清单","description":"读取所有孩子及积分"}]}""",
            "统计所有孩子的总积分",
            5);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("获取孩子清单", plan.Steps[0].Title);
        Assert.Equal("汇总并输出结果", plan.Steps[1].Title);
    }

    [Fact]
    public void ToolCallStart_IncludesArgumentsInProcessDelta()
    {
        var ev = GoldfishHarnessEvent.ToolCallStart(
            1,
            "family_reward_query_children",
            """{"includeScores":true}""");

        Assert.Contains("调用工具 family_reward_query_children", ev.Delta);
        Assert.Contains("参数:", ev.Delta);
        Assert.Contains("includeScores", ev.Delta);
    }

    [Fact]
    public void BuildMessages_MergesMemoryIntoTheLeadingSystemMessage()
    {
        var runner = new GoldfishHarnessRunner(
            null!,
            new ToolRegistry(),
            skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "当前问题",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "当前问题"),
            [],
            MemoryContext: new MemoryContext
            {
                LongTermMemories =
                [
                    new MemoryEntry
                    {
                        Type = "UserPreference",
                        Content = "用户偏好先给结论。"
                    }
                ]
            });

        var buildMessages = typeof(GoldfishHarnessRunner).GetMethod(
            "BuildMessages",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(GoldfishHarnessRequest)]);

        var messages = Assert.IsType<List<Microsoft.Extensions.AI.ChatMessage>>(
            buildMessages!.Invoke(runner, [request]));

        var system = Assert.Single(messages, message => message.Role == ChatRole.System);
        Assert.Same(messages[0], system);
        Assert.Contains("基础系统提示", system.Text);
        Assert.Contains("用户偏好先给结论", system.Text);
    }

    [Fact]
    public void BuildMessages_TrimsOldHistoryWhenEstimatedPromptBudgetIsExceeded()
    {
        var runner = new GoldfishHarnessRunner(
            null!,
            new ToolRegistry(),
            skillRegistry: null);
        var now = DateTime.UtcNow;
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示"
            },
            "session-1",
            "当前问题",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "当前问题"),
            [
                new ChatMessage { Role = "user", Content = "old-" + new string('x', 5000), CreatedAt = now.AddMinutes(-3) },
                new ChatMessage { Role = "assistant", Content = "recent-one", CreatedAt = now.AddMinutes(-2) },
                new ChatMessage { Role = "user", Content = "recent-two", CreatedAt = now.AddMinutes(-1) }
            ],
            MemoryOptions: new MemoryOptions
            {
                ShortTerm = { MaxMessages = 10 },
                MediumTerm =
                {
                    MaxEstimatedInputTokens = 450,
                    EstimatedCharsPerToken = 10.0
                }
            });

        var buildMessages = typeof(GoldfishHarnessRunner).GetMethod(
            "BuildMessages",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(GoldfishHarnessRequest)]);

        var messages = Assert.IsType<List<Microsoft.Extensions.AI.ChatMessage>>(
            buildMessages!.Invoke(runner, [request]));

        Assert.DoesNotContain(messages, message => message.Text?.Contains("old-") == true);
        Assert.Contains(messages, message => message.Text == "recent-one");
        Assert.Contains(messages, message => message.Text == "recent-two");
        Assert.Contains(messages, message => message.Text == "当前问题");
    }

    [Fact]
    public void PromptBuilder_MergesMemoryIntoSingleLeadingSystemMessage()
    {
        var builder = new PromptBuilder();
        var messages = builder.BuildMessages(
            new AgentInfo { SystemPrompt = "基础系统提示" },
            "当前问题",
            new MemoryContext
            {
                LongTermMemories =
                [
                    new MemoryEntry
                    {
                        Type = "UserPreference",
                        Content = "用户偏好单一 system。"
                    }
                ]
            },
            []);

        var system = Assert.Single(messages, message => message.Role == "system");
        Assert.Same(messages[0], system);
        Assert.Contains("基础系统提示", system.Content);
        Assert.Contains("用户偏好单一 system", system.Content);
    }

    [Fact]
    public async Task SkillSessionStore_IsolatesLoadedSkillsBySession()
    {
        var store = new InMemorySkillSessionStore();
        var first = new SkillSessionKey
        {
            TenantId = "tenant",
            UserId = "user",
            AgentId = "agent",
            WorkspaceId = "workspace",
            SessionId = "session-1"
        };
        var second = first with { SessionId = "session-2" };

        await store.RecordLoadedAsync(first, new SkillSessionEntry { SkillName = "docs", Source = "test" });
        await store.RecordLoadedAsync(first, new SkillSessionEntry { SkillName = "docs", Source = "test-duplicate" });
        await store.RecordLoadedAsync(second, new SkillSessionEntry { SkillName = "code", Source = "test" });

        var firstEntries = await store.LoadAsync(first);
        var secondEntries = await store.LoadAsync(second);

        var firstEntry = Assert.Single(firstEntries);
        Assert.Equal("docs", firstEntry.SkillName);
        Assert.Equal("test-duplicate", firstEntry.Source);
        Assert.Equal("code", Assert.Single(secondEntries).SkillName);
    }

    [Fact]
    public async Task ToolAuthorizationDeny_SkipsToolExecutionAndRecordsAudit()
    {
        var tool = new RecordingTool();
        var registry = new ToolRegistry();
        registry.Register(tool);
        var audit = new InMemoryToolExecutionStore();
        var runner = new GoldfishHarnessRunner(null!, registry, skillRegistry: null);
        var request = new GoldfishHarnessRequest(
            new AgentInfo
            {
                Id = "agent-1",
                Name = "Agent One",
                SystemPrompt = "基础系统提示",
                ExtraData = new Dictionary<string, string>
                {
                    ["UserId"] = "user-1",
                    ["TenantId"] = "tenant-1"
                }
            },
            "session-1",
            "run tool",
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "run tool"),
            [],
            ToolExecutionStore: audit,
            ToolAuthorizationHook: new DenyToolAuthorizationHook("needs approval"));

        var action = CreateLegacyToolAction("recording.tool", """{"value":1}""");
        var method = typeof(GoldfishHarnessRunner).GetMethod(
            "ExecuteLegacyToolActionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var task = Assert.IsAssignableFrom<Task<ToolCallRecord>>(
            method!.Invoke(runner, [request, "run-1", 1, action, "call-1", CancellationToken.None]));
        var record = await task;

        Assert.False(tool.Executed);
        Assert.False(record.Success);
        Assert.Contains("denied", record.Result);
        var auditRecord = Assert.Single(audit.Records);
        Assert.Equal("Deny", auditRecord.AuthorizationDecision);
        Assert.Equal("recording.tool", auditRecord.ToolId);
        Assert.True(auditRecord.IsError);
        Assert.False(string.IsNullOrWhiteSpace(auditRecord.ArgumentsHash));
    }

    [Fact]
    public void RequiredRetryDetection_IgnoresArrayToolResults()
    {
        var toolFunctionType = typeof(GoldfishHarnessRunner).GetNestedType(
            "ToolFunction",
            BindingFlags.NonPublic)!;
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), toolFunctionType);
        var toolFunctions = Activator.CreateInstance(dictionaryType)!;
        var method = typeof(GoldfishHarnessRunner).GetMethod(
            "TryCreateRequiredRetryCall",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            new ToolCallRecord { Result = """[{"name":"mcp"}]""", Success = true },
            toolFunctions,
            1,
            null
        ];

        var created = Assert.IsType<bool>(method.Invoke(null, arguments));

        Assert.False(created);
        Assert.Null(arguments[3]);
    }

    [Fact]
    public async Task SqliteHarnessStateStore_PersistsSkillsAndToolAuditHashes()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "goldfish-harness-tests",
            Guid.NewGuid().ToString("n"),
            "state.db");
        var key = new SkillSessionKey
        {
            TenantId = "tenant",
            UserId = "user",
            AgentId = "agent",
            WorkspaceId = "workspace",
            SessionId = "session"
        };

        using (var store = new SqliteHarnessStateStore(databasePath))
        {
            await store.RecordLoadedAsync(key, new SkillSessionEntry { SkillName = "memory", Source = "test" });
            await store.RecordAsync(new ToolExecutionRecord
            {
                RunId = "run",
                SessionId = "session",
                TenantId = "tenant",
                UserId = "user",
                AgentId = "agent",
                WorkspaceId = "workspace",
                Step = 1,
                ToolCallId = "call",
                ToolId = "tool",
                ArgumentsHash = ToolExecutionHash.Sha256("""{"secret":"value"}"""),
                ResultHash = ToolExecutionHash.Sha256("result"),
                ArgumentsJson = HarnessSensitiveData.Redact("""{"apiKey":"must-not-persist","query":"value"}"""),
                ResultJson = """{"structuredContent":{"value":1},"isError":false}""",
                StructuredContentJson = """{"value":1}""",
                IsError = false,
                Success = true,
                AuthorizationDecision = ToolAuthorizationDecision.Allow.ToString()
            });
        }

        using (var reopened = new SqliteHarnessStateStore(databasePath))
        {
            var entry = Assert.Single(await reopened.LoadAsync(key));
            Assert.Equal("memory", entry.SkillName);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT arguments_hash, result_hash, arguments_json, structured_content_json, is_error FROM goldfish_tool_executions WHERE tool_id = 'tool'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ToolExecutionHash.Sha256("""{"secret":"value"}"""), reader.GetString(0));
        Assert.Equal(ToolExecutionHash.Sha256("result"), reader.GetString(1));
        Assert.DoesNotContain("must-not-persist", reader.GetString(2));
        Assert.Contains("[REDACTED]", reader.GetString(2));
        Assert.Equal("""{"value":1}""", reader.GetString(3));
        Assert.Equal(0, reader.GetInt32(4));
    }

    private static object CreateLegacyToolAction(string toolId, string arguments)
    {
        var actionType = typeof(GoldfishHarnessRunner).GetNestedType(
            "GoldfishAction",
            BindingFlags.NonPublic);
        var kindType = typeof(GoldfishHarnessRunner).GetNestedType(
            "GoldfishActionKind",
            BindingFlags.NonPublic);
        var toolKind = Enum.Parse(kindType!, "Tool");
        return Activator.CreateInstance(actionType!, toolKind, null, toolId, arguments, null)!;
    }

    private static GoldfishHarnessRequest RequiredFamilyRewardRequest(string prompt)
        => new(
            new AgentInfo
            {
                Id = "family-reward-agent",
                Name = "家庭积分应用",
                SystemPrompt = "积分查询必须成功调用家庭积分工具后才能回答。",
                ExtraData = new Dictionary<string, string>
                {
                    ["RuntimeResponseMode"] = "final_text_only",
                    ["GoldfishRequireToolBeforeFinal"] = "true",
                    ["GoldfishRequiredToolPrefix"] = "family_reward"
                }
            },
            "family-reward-session",
            prompt,
            new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, prompt),
            [],
            ReasoningOptions: new ReasoningOptions { Strategy = ReasoningStrategyKind.ReAct });

    private sealed class RecordingTool : ITool
    {
        public bool Executed { get; private set; }
        public string LastArguments { get; private set; } = string.Empty;
        public string Id => "recording.tool";
        public string Name => "recording_tool";
        public string Description => "Records whether it was executed.";
        public string ParametersSchema => """{"type":"object","additionalProperties":true}""";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<ToolResult> ExecuteAsync(string arguments)
        {
            Executed = true;
            LastArguments = arguments;
            return Task.FromResult(new ToolResult
            {
                Success = true,
                Data = new { ok = true }
            });
        }
    }

    private sealed class NamedRecordingTool : ITool
    {
        public NamedRecordingTool(string id) => Id = id;

        public string Id { get; }
        public string Name => Id;
        public string Description => "Records a successful internal tool call.";
        public string ParametersSchema => """{"type":"object","additionalProperties":true}""";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<ToolResult> ExecuteAsync(string arguments) => Task.FromResult(new ToolResult
        {
            Success = true,
            Data = new { ok = true }
        });
    }

    private sealed class ResultTool(string id, bool success) : ITool
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => "Returns the requested test result.";
        public string ParametersSchema => """{\"type\":\"object\",\"additionalProperties\":true}""";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<ToolResult> ExecuteAsync(string arguments) => Task.FromResult(new ToolResult
        {
            Success = success,
            Error = success ? null : "score adjustment failed",
            Data = new { ok = success }
        });
    }

    private sealed class DenyToolAuthorizationHook(string reason) : IToolAuthorizationHook
    {
        public ValueTask<ToolAuthorizationResult> AuthorizeAsync(
            ToolAuthorizationRequest request,
            CancellationToken ct = default)
            => ValueTask.FromResult(ToolAuthorizationResult.Deny(reason));
    }

    private sealed class QueueChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public List<List<Microsoft.Extensions.AI.ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            var text = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
            return Task.FromResult(new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            var text = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new TextContent(text)])
            {
                ModelId = "test"
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
