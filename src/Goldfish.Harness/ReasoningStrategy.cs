using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MsChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Goldfish.Harness;

public enum ReasoningStrategyKind
{
    Auto,
    ReAct,
    PlanAndExecute,
    ReWOO
}

public sealed class ReasoningOptions
{
    public static ReasoningOptions Default { get; } = new();

    public ReasoningStrategyKind Strategy { get; set; } = ReasoningStrategyKind.Auto;
    public bool EnableReflexion { get; set; } = true;
    public int MaxReasoningSteps { get; set; } = 12;
    public int MaxPlanSteps { get; set; } = 20;
    public int MaxReflectionRetries { get; set; } = 1;
    public int LongTaskEstimatedTokenThreshold { get; set; } = 6000;
    public int ReWooToolCountThreshold { get; set; } = 4;
    public bool PersistPlanState { get; set; } = true;
    public bool PersistReflections { get; set; } = false;
    public bool CacheAutoStrategyInSession { get; set; } = true;
    public bool ReevaluateAutoStrategyEveryTurn { get; set; } = true;
}

public sealed record ReasoningStrategySelection(
    ReasoningStrategyKind Requested,
    ReasoningStrategyKind Effective,
    bool ReflexionEnabled,
    string Reason)
{
    public string ToPromptSection()
    {
        var reflexion = ReflexionEnabled
            ? "启用：仅在失败、验证不通过、工具异常或用户纠错时触发。"
            : "关闭。";
        return $"""

## 推理策略
- Requested: {Requested}
- Effective: {Effective}
- SelectionReason: {Reason}
- Reflexion: {reflexion}

### 策略约束
- ReAct：作为基础工具循环执行器。
- PlanAndExecute：用于长任务，计划状态必须压缩后合并到第一条 system message。
- ReWOO：用于工具密集任务，工具图节点仍必须逐个经过授权 hook。
- Reflexion：作为纠错层，不直接写长期记忆，持久化经验必须经过 memory admission。
""";
    }
}

public sealed record ReasoningStrategyDecisionRequest(
    string SessionId,
    string UserMessage,
    ReasoningOptions? Options,
    ReasoningStrategySelection? CachedSelection = null);

public interface IReasoningStrategyDecider
{
    Task<ReasoningStrategySelection> SelectAsync(
        ReasoningStrategyDecisionRequest request,
        CancellationToken ct = default);
}

public sealed class DefaultReasoningStrategyDecider : IReasoningStrategyDecider
{
    private readonly IChatClient _chatClient;
    private readonly ILogger _logger;

    public DefaultReasoningStrategyDecider(
        IChatClient chatClient,
        ILogger? logger = null)
    {
        _chatClient = chatClient;
        _logger = logger ?? NullLogger<DefaultReasoningStrategyDecider>.Instance;
    }

    public async Task<ReasoningStrategySelection> SelectAsync(
        ReasoningStrategyDecisionRequest request,
        CancellationToken ct = default)
    {
        var options = request.Options ?? ReasoningOptions.Default;
        if (options.Strategy != ReasoningStrategyKind.Auto)
        {
            return ReasoningStrategySelector.Select(request.UserMessage, options);
        }

        if (ReasoningStrategySelector.TrySelectUserDirectedStrategy(request.UserMessage, options) is { } userDirected)
        {
            return userDirected;
        }

        try
        {
            var messages = new List<MsChatMessage>
            {
                new(ChatRole.System, """
你是 Goldfish Harness 的推理策略分类器。你只决定执行策略，不回答用户问题。
只输出 JSON，不要输出 Markdown 或解释。

可选 strategy：
- ReAct：简单问答、单步任务、无需明确计划。
- PlanAndExecute：多阶段任务、需要先规划再逐步执行、需要持续进度状态。
- ReWOO：需要多个独立信息源或多组工具调用，适合先形成工具图再汇总。

优先规则：
- 如果用户本轮明确要求“走/使用/采用/切换到”某种策略，应遵从用户要求。
- 例如“我希望你走 plan 模式”应选择 PlanAndExecute。
- 例如“解释 react 和 plan 的区别”只是询问概念，不是策略切换。

JSON 格式：
{"strategy":"ReAct|PlanAndExecute|ReWOO","confidence":0.0,"reason":"short reason"}
"""),
                new(ChatRole.User, request.UserMessage)
            };
            var response = await _chatClient.GetResponseAsync(
                messages,
                new ChatOptions
                {
                    MaxOutputTokens = 256,
                    Temperature = 0
                },
                ct);
            return ReasoningStrategySelector.SelectFromClassifier(
                request.UserMessage,
                options,
                response.Text);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Reasoning strategy classifier failed; falling back to structural selector. session={SessionId}", request.SessionId);
            return ReasoningStrategySelector.Select(request.UserMessage, options);
        }
    }

}

public static class ReasoningStrategySelector
{
    public static ReasoningStrategySelection Select(string userMessage, ReasoningOptions? options)
    {
        options ??= ReasoningOptions.Default;
        var requested = options.Strategy;
        if (requested is ReasoningStrategyKind.ReAct
            or ReasoningStrategyKind.PlanAndExecute
            or ReasoningStrategyKind.ReWOO)
        {
            return new ReasoningStrategySelection(
                requested,
                requested,
                options.EnableReflexion,
                "request-explicit");
        }

        return TrySelectUserDirectedStrategy(userMessage, options)
            ?? SelectFromFeatures(userMessage, options, "auto-structural");
    }

    public static ReasoningStrategySelection SelectFromClassifier(
        string userMessage,
        ReasoningOptions? options,
        string? classifierOutput)
    {
        options ??= ReasoningOptions.Default;
        if (options.Strategy != ReasoningStrategyKind.Auto)
        {
            return new ReasoningStrategySelection(
                options.Strategy,
                options.Strategy,
                options.EnableReflexion,
                "request-explicit");
        }

        if (TrySelectUserDirectedStrategy(userMessage, options) is { } userDirected)
        {
            return userDirected;
        }

        var classified = TryParseClassifierOutput(classifierOutput);
        if (classified is { Confidence: >= 0.65 }
            && classified.Strategy is ReasoningStrategyKind.ReAct
                or ReasoningStrategyKind.PlanAndExecute
                or ReasoningStrategyKind.ReWOO)
        {
            return new ReasoningStrategySelection(
                ReasoningStrategyKind.Auto,
                classified.Strategy,
                options.EnableReflexion,
                string.IsNullOrWhiteSpace(classified.Reason)
                    ? $"auto-classifier:{classified.Confidence:0.00}"
                    : $"auto-classifier:{classified.Confidence:0.00}:{classified.Reason}");
        }

        return SelectFromFeatures(userMessage, options, "auto-classifier-fallback");
    }

    public static ReasoningStrategySelection? TrySelectUserDirectedStrategy(
        string? userMessage,
        ReasoningOptions? options)
    {
        options ??= ReasoningOptions.Default;
        if (options.Strategy != ReasoningStrategyKind.Auto || string.IsNullOrWhiteSpace(userMessage))
        {
            return null;
        }

        var normalized = NormalizeDirectiveText(userMessage);
        var matches = new List<ReasoningStrategyKind>();
        if (HasDirectedStrategyMention(normalized, ["react", "re-act", "反应模式", "边想边做"]))
        {
            matches.Add(ReasoningStrategyKind.ReAct);
        }
        if (HasDirectedStrategyMention(normalized, ["plan", "planandexecute", "plan-and-execute", "计划模式", "规划模式", "先规划再执行"]))
        {
            matches.Add(ReasoningStrategyKind.PlanAndExecute);
        }
        if (HasDirectedStrategyMention(normalized, ["rewoo", "re-woo", "工具图模式", "多工具图"]))
        {
            matches.Add(ReasoningStrategyKind.ReWOO);
        }

        var distinct = matches.Distinct().ToList();
        return distinct.Count == 1
            ? new ReasoningStrategySelection(
                ReasoningStrategyKind.Auto,
                distinct[0],
                options.EnableReflexion,
                $"auto-user-directed:{distinct[0]}")
            : null;
    }

    private static string NormalizeDirectiveText(string text)
        => string.Concat(text
            .Trim()
            .ToLowerInvariant()
            .Where(ch => !char.IsWhiteSpace(ch)));

    private static bool HasDirectedStrategyMention(string normalized, IReadOnlyList<string> aliases)
    {
        foreach (var alias in aliases)
        {
            var normalizedAlias = NormalizeDirectiveText(alias);
            var index = normalized.IndexOf(normalizedAlias, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var before = normalized[Math.Max(0, index - 18)..index];
                var afterStart = index + normalizedAlias.Length;
                var after = normalized[afterStart..Math.Min(normalized.Length, afterStart + 8)];
                if (!ContainsNegatedDirective(before)
                    && (ContainsDirectiveVerb(before) || StartsWithDirectiveSuffix(after)))
                {
                    return true;
                }

                index = normalized.IndexOf(normalizedAlias, index + normalizedAlias.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool ContainsDirectiveVerb(string text)
        => text.Contains("希望你", StringComparison.Ordinal)
            || text.Contains("请你", StringComparison.Ordinal)
            || text.Contains("让你", StringComparison.Ordinal)
            || text.Contains("你走", StringComparison.Ordinal)
            || text.Contains("走", StringComparison.Ordinal)
            || text.Contains("使用", StringComparison.Ordinal)
            || text.Contains("采用", StringComparison.Ordinal)
            || text.Contains("按照", StringComparison.Ordinal)
            || text.Contains("按", StringComparison.Ordinal)
            || text.Contains("切换到", StringComparison.Ordinal)
            || text.Contains("切到", StringComparison.Ordinal)
            || text.Contains("指定", StringComparison.Ordinal)
            || text.Contains("选择", StringComparison.Ordinal)
            || text.Contains("强制", StringComparison.Ordinal)
            || text.EndsWith("用", StringComparison.Ordinal);

    private static bool ContainsNegatedDirective(string text)
        => text.Contains("不要", StringComparison.Ordinal)
            || text.Contains("别", StringComparison.Ordinal)
            || text.Contains("不用", StringComparison.Ordinal)
            || text.Contains("无需", StringComparison.Ordinal)
            || text.Contains("禁止", StringComparison.Ordinal);

    private static bool StartsWithDirectiveSuffix(string text)
        => text.StartsWith("模式执行", StringComparison.Ordinal)
            || text.StartsWith("策略执行", StringComparison.Ordinal)
            || text.StartsWith("模式来", StringComparison.Ordinal)
            || text.StartsWith("策略来", StringComparison.Ordinal)
            || text.StartsWith("方式来", StringComparison.Ordinal);

    private static ReasoningStrategySelection SelectFromFeatures(
        string? userMessage,
        ReasoningOptions options,
        string reasonPrefix)
    {
        var features = ReasoningRequestFeatures.From(userMessage);
        if (features.ToolDensityScore >= 4)
        {
            return new ReasoningStrategySelection(
                ReasoningStrategyKind.Auto,
                ReasoningStrategyKind.ReWOO,
                options.EnableReflexion,
                $"{reasonPrefix}:tool-density={features.ToolDensityScore}");
        }

        if (features.PlanComplexityScore >= 5
            || features.EstimatedTokens >= options.LongTaskEstimatedTokenThreshold)
        {
            return new ReasoningStrategySelection(
                ReasoningStrategyKind.Auto,
                ReasoningStrategyKind.PlanAndExecute,
                options.EnableReflexion,
                $"{reasonPrefix}:complexity={features.PlanComplexityScore}");
        }

        return new ReasoningStrategySelection(
            ReasoningStrategyKind.Auto,
            ReasoningStrategyKind.ReAct,
            options.EnableReflexion,
            $"{reasonPrefix}:simple");
    }

    private static ClassifierDecision? TryParseClassifierOutput(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return null;

        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var strategyText = root?["strategy"]?.GetValue<string>();
            var confidence = root?["confidence"]?.GetValue<double>() ?? 0;
            var reason = root?["reason"]?.GetValue<string>();
            if (!Enum.TryParse<ReasoningStrategyKind>(strategyText, ignoreCase: true, out var strategy))
            {
                return null;
            }

            return new ClassifierDecision(strategy, confidence, reason);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private sealed record ClassifierDecision(
        ReasoningStrategyKind Strategy,
        double Confidence,
        string? Reason);
}

public sealed record ReasoningRequestFeatures(
    int EstimatedTokens,
    int NonEmptyLineCount,
    int ListMarkerCount,
    int ClauseBoundaryCount,
    int UrlOrPathLikeCount,
    int JsonOrCodeFenceCount,
    int PlanComplexityScore,
    int ToolDensityScore)
{
    public static ReasoningRequestFeatures From(string? userMessage)
    {
        var text = userMessage ?? string.Empty;
        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var listMarkers = lines.Count(line =>
            line.StartsWith("-", StringComparison.Ordinal)
            || line.StartsWith("*", StringComparison.Ordinal)
            || line.StartsWith("•", StringComparison.Ordinal)
            || (line.Length >= 2 && char.IsDigit(line[0]) && (line[1] == '.' || line[1] == ')')));
        var clauseBoundaries = text.Count(ch => ch is '。' or '，' or '；' or ';' or ',' or '.' or '?' or '？' or '!' or '！');
        var urlOrPathLike = CountPattern(text, "://")
            + CountPattern(text, "/")
            + CountPattern(text, "\\")
            + CountPattern(text, ".cs")
            + CountPattern(text, ".ts")
            + CountPattern(text, ".tsx")
            + CountPattern(text, ".js")
            + CountPattern(text, ".json")
            + CountPattern(text, ".md");
        var codeFence = CountPattern(text, "```")
            + CountPattern(text, "{")
            + CountPattern(text, "}");
        var estimatedTokens = ContextTokenEstimator.EstimateTextTokens(text, estimatedCharsPerToken: 4.0);

        var complexity = 0;
        if (estimatedTokens >= 300) complexity += 2;
        if (estimatedTokens >= 800) complexity += 2;
        if (lines.Count >= 3) complexity += 2;
        if (listMarkers >= 2) complexity += 2;
        if (clauseBoundaries >= 4) complexity += 1;
        if (urlOrPathLike >= 2) complexity += 1;
        if (codeFence >= 2) complexity += 1;

        var toolDensity = 0;
        if (urlOrPathLike >= 3) toolDensity += 2;
        if (listMarkers >= 3) toolDensity += 1;
        if (codeFence >= 3) toolDensity += 1;
        if (lines.Count >= 5) toolDensity += 1;

        return new ReasoningRequestFeatures(
            estimatedTokens,
            lines.Count,
            listMarkers,
            clauseBoundaries,
            urlOrPathLike,
            codeFence,
            complexity,
            toolDensity);
    }

    private static int CountPattern(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return 0;
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}

public sealed record ReasoningPlan(
    string PlanId,
    IReadOnlyList<ReasoningPlanStep> Steps,
    string Summary)
{
    public string ToPromptSection()
    {
        var lines = Steps.Count == 0
            ? "- 1. 完成用户请求。"
            : string.Join("\n", Steps.Select(step => $"- {step.Index}. {step.Title}: {step.Description}"));
        return $"""

## 当前执行计划
PlanId: {PlanId}
Summary: {Summary}
Steps:
{lines}

执行约束：
- 按计划推进，但如果工具结果证明计划不合适，可以调整执行顺序。
- 每一步仍必须优先使用 native tools/function calling，不要在正文伪造工具调用。
- 最终回答只面向用户总结结果，不输出内部计划 JSON。
""";
    }
}

public sealed record ReasoningPlanStep(
    int Index,
    string Title,
    string Description);

public sealed record ReasoningReWooGraph(
    string GraphId,
    string Summary,
    IReadOnlyList<ReasoningReWooNode> Nodes)
{
    public string ToPromptSection()
    {
        var lines = Nodes.Count == 0
            ? "- 无可执行工具节点；按普通执行链路继续。"
            : string.Join("\n", Nodes.Select(node =>
                $"- {node.Id}. {node.Tool}: {node.Purpose} args={node.ArgumentsJson}"));
        return $"""

## 当前 ReWOO 工具图
GraphId: {GraphId}
Summary: {Summary}
Nodes:
{lines}

执行约束：
- Harness 会逐个执行工具图节点，且每个节点仍必须经过工具授权 hook。
- 工具观察会合并回上下文，最终回答只能基于用户请求和真实工具观察综合。
- 不要在正文伪造未执行的工具结果。
""";
    }
}

public sealed record ReasoningReWooNode(
    string Id,
    string Tool,
    string ArgumentsJson,
    string Purpose);

public static class ReasoningReWooGraphParser
{
    public static ReasoningReWooGraph ParseOrFallback(
        string? raw,
        IReadOnlySet<string> availableToolNames,
        int maxNodes)
    {
        maxNodes = Math.Max(1, maxNodes);
        var nodes = TryParseNodes(raw, availableToolNames, maxNodes);
        var summary = TryReadString(raw, "summary")
            ?? TryReadString(raw, "graph_summary")
            ?? "ReWOO 自动生成工具图。";
        return new ReasoningReWooGraph(
            Guid.NewGuid().ToString("n"),
            Compact(summary),
            nodes);
    }

    private static List<ReasoningReWooNode> TryParseNodes(
        string? raw,
        IReadOnlySet<string> availableToolNames,
        int maxNodes)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return [];

        try
        {
            var root = JsonNode.Parse(json);
            var array = root?["nodes"] as JsonArray
                ?? root?["steps"] as JsonArray
                ?? root?["tools"] as JsonArray;
            if (array == null) return [];

            var nodes = new List<ReasoningReWooNode>();
            foreach (var item in array.Take(maxNodes))
            {
                var tool = ReadString(item, "tool")
                    ?? ReadString(item, "tool_name")
                    ?? ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(tool)
                    || !availableToolNames.Contains(tool.Trim()))
                {
                    continue;
                }

                var id = ReadString(item, "id")
                    ?? ReadString(item, "node_id")
                    ?? $"N{nodes.Count + 1}";
                var purpose = ReadString(item, "purpose")
                    ?? ReadString(item, "description")
                    ?? ReadString(item, "goal")
                    ?? tool;
                var args = item?["arguments"]
                    ?? item?["args"]
                    ?? item?["input"];
                var argsJson = args is JsonObject
                    ? args.ToJsonString()
                    : "{}";
                nodes.Add(new ReasoningReWooNode(
                    Compact(id),
                    tool.Trim(),
                    argsJson,
                    Compact(purpose)));
            }

            return nodes;
        }
        catch
        {
            return [];
        }
    }

    private static string? TryReadString(string? raw, string name)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return null;
        try
        {
            return ReadString(JsonNode.Parse(json), name);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null)
        {
            return null;
        }

        return value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString();
    }

    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static string Compact(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}

public sealed record ReasoningReflection(
    bool Revised,
    string Answer,
    string Reason);

public static class ReasoningReflectionParser
{
    public static ReasoningReflection ParseOrKeep(string? raw, string currentAnswer)
    {
        var json = ExtractJsonObject(raw);
        if (json == null)
        {
            return new ReasoningReflection(false, currentAnswer, "reflection-unparseable");
        }

        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var action = root?["action"]?.GetValue<string>() ?? "keep";
            var reason = root?["reason"]?.GetValue<string>() ?? "reflection-checked";
            var revised = root?["answer"]?.GetValue<string>()
                ?? root?["revised_answer"]?.GetValue<string>();
            if (string.Equals(action, "revise", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(revised))
            {
                return new ReasoningReflection(true, revised.Trim(), reason);
            }

            return new ReasoningReflection(false, currentAnswer, reason);
        }
        catch
        {
            return new ReasoningReflection(false, currentAnswer, "reflection-unparseable");
        }
    }

    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }
}

public static class ReasoningPlanParser
{
    public static ReasoningPlan ParseOrFallback(string? raw, string userMessage, int maxSteps)
    {
        maxSteps = Math.Max(1, maxSteps);
        var steps = TryParseSteps(raw, maxSteps);
        if (steps.Count == 0)
        {
            steps =
            [
                new ReasoningPlanStep(
                    1,
                    "完成请求",
                    string.IsNullOrWhiteSpace(userMessage) ? "处理当前用户请求。" : userMessage.Trim())
            ];
        }
        else if (steps.Count == 1 && maxSteps > 1)
        {
            steps.Add(new ReasoningPlanStep(
                2,
                "汇总并输出结果",
                "基于已完成的处理结果，形成可直接回答用户的最终答复。"));
        }

        var summary = TryReadString(raw, "summary")
            ?? TryReadString(raw, "plan_summary")
            ?? "Plan-and-Execute 自动生成计划。";
        return new ReasoningPlan(
            Guid.NewGuid().ToString("n"),
            steps,
            summary);
    }

    private static List<ReasoningPlanStep> TryParseSteps(string? raw, int maxSteps)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return [];

        try
        {
            var root = JsonNode.Parse(json);
            var array = root?["steps"] as JsonArray
                ?? root?["plan"] as JsonArray
                ?? root?["tasks"] as JsonArray;
            if (array == null) return [];

            var steps = new List<ReasoningPlanStep>();
            foreach (var item in array.Take(maxSteps))
            {
                var fallbackTitle = item?.ToJsonString() ?? "执行步骤";
                var title = ReadString(item, "title")
                    ?? ReadString(item, "name")
                    ?? ReadString(item, "step")
                    ?? fallbackTitle;
                var description = ReadString(item, "description")
                    ?? ReadString(item, "detail")
                    ?? ReadString(item, "goal")
                    ?? title;
                steps.Add(new ReasoningPlanStep(steps.Count + 1, Compact(title), Compact(description)));
            }

            return steps;
        }
        catch
        {
            return [];
        }
    }

    private static string? TryReadString(string? raw, string name)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return null;
        try
        {
            return ReadString(JsonNode.Parse(json), name);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value == null)
        {
            return null;
        }

        var text = value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue)
            ? stringValue
            : value.ToJsonString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static string Compact(string text)
    {
        text = text.Trim();
        return text.Length <= 200 ? text : text[..200].TrimEnd() + "...";
    }
}
