using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Goldfish.Harness;

/// <summary>
/// OpenAI-compatible chat completions client used by Goldfish Harness.
/// It preserves text whitespace, exposes provider reasoning deltas, and maps native tool calls to Microsoft.Extensions.AI content.
/// </summary>
public sealed class WhitespacePreservingOpenAiChatClient : IChatClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const int MaxTransientAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly int? _defaultMaxOutputTokens;

    public WhitespacePreservingOpenAiChatClient(
        string baseUrl,
        string apiKey,
        string model,
        IReadOnlyDictionary<string, string>? defaultHeaders = null,
        int? defaultMaxOutputTokens = null)
    {
        _endpoint = ResolveChatCompletionsEndpoint(baseUrl);
        _model = model;
        _defaultMaxOutputTokens = defaultMaxOutputTokens;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        if (defaultHeaders is not null)
        {
            foreach (var (name, value) in defaultHeaders)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) continue;
                _httpClient.DefaultRequestHeaders.Remove(name);
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value.Trim());
            }
        }
    }

    public static string ResolveChatCompletionsEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return normalized;
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return normalized;
        if (normalized.EndsWith("/v1/responses", StringComparison.OrdinalIgnoreCase))
            return $"{normalized[..^"/v1/responses".Length]}/v1/chat/completions";
        if (normalized.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return $"{normalized[..^"/responses".Length]}/chat/completions";
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return $"{normalized}/chat/completions";
        return $"{normalized}/v1/chat/completions";
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(messages, options, stream: false);
        using var response = await SendJsonWithRetryAsync(payload, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI compatible chat failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var contents = new List<AIContent>();
        var text = GetString(message, "content") ?? string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            contents.Add(new TextContent(text));
        }

        foreach (var call in ReadToolCalls(message))
        {
            contents.Add(new FunctionCallContent(call.Id, call.Name, ParseArguments(call.Arguments)));
        }

        var result = new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = _model
        };
        if (TryReadUsageDetails(doc.RootElement, out var usage))
        {
            result.Usage = usage;
        }

        return result;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(messages, options, stream: true);
        using var response = await SendJsonWithRetryAsync(payload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OpenAI compatible chat stream failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamingToolCallState>();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataBuilder.AppendLine(line["data:".Length..].TrimStart());
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line)) continue;

            foreach (var update in ParseSseFrame(dataBuilder.ToString(), toolCalls))
            {
                yield return update;
            }

            dataBuilder.Clear();
        }

        foreach (var update in ParseSseFrame(dataBuilder.ToString(), toolCalls))
        {
            yield return update;
        }

        foreach (var state in toolCalls.Values.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent(
                        string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id!,
                        state.Name!,
                        ParseArguments(state.Arguments.ToString()))
                })
            {
                ModelId = _model
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _httpClient.Dispose();

    private async Task<HttpResponseMessage> SendJsonWithRetryAsync(
        object payload,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };

            try
            {
                var response = await _httpClient.SendAsync(request, completionOption, cancellationToken);
                if (!IsTransientStatus(response.StatusCode) || attempt == MaxTransientAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (Exception ex) when (IsTransientException(ex) && attempt < MaxTransientAttempts)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
        }

        throw lastException ?? new InvalidOperationException("OpenAI compatible chat request failed after retries.");
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.InternalServerError
           || statusCode == HttpStatusCode.BadGateway
           || statusCode == HttpStatusCode.ServiceUnavailable
           || statusCode == HttpStatusCode.GatewayTimeout
           || (int)statusCode == 429;

    private static bool IsTransientException(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or IOException;

    private object BuildPayload(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var tools = BuildTools(options).ToList();
        return new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["stream"] = stream,
            ["stream_options"] = stream ? new { include_usage = true } : null,
            ["messages"] = messages.Select(ToOpenAiMessage).ToList(),
            ["temperature"] = options?.Temperature,
            ["max_tokens"] = options?.MaxOutputTokens ?? _defaultMaxOutputTokens,
            ["tools"] = tools.Count == 0 ? null : tools,
            ["tool_choice"] = tools.Count == 0 ? null : "auto",
            ["parallel_tool_calls"] = tools.Count == 0 ? null : options?.AllowMultipleToolCalls
        };
    }

    private static Dictionary<string, object?> ToOpenAiMessage(Microsoft.Extensions.AI.ChatMessage message)
    {
        if (message.Role == ChatRole.System)
        {
            return new Dictionary<string, object?> { ["role"] = "system", ["content"] = message.Text ?? string.Empty };
        }

        if (message.Role == ChatRole.Assistant)
        {
            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                return new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = message.Text ?? string.Empty };
            }

            return new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = calls.Select(call => new Dictionary<string, object?>
                {
                    ["id"] = call.CallId,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = call.Name,
                        ["arguments"] = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), JsonOptions)
                    }
                }).ToList()
            };
        }

        if (message.Role == ChatRole.Tool)
        {
            var result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            return new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = result?.CallId ?? string.Empty,
                ["content"] = result?.Result?.ToString() ?? message.Text ?? string.Empty
            };
        }

        return new Dictionary<string, object?> { ["role"] = "user", ["content"] = message.Text ?? string.Empty };
    }

    private static IEnumerable<object> BuildTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools) yield break;
        foreach (var function in tools.OfType<AIFunction>())
        {
            yield return new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    ["parameters"] = JsonSerializer.Deserialize<JsonElement>(function.JsonSchema.GetRawText())
                }
            };
        }
    }

    private IEnumerable<ChatResponseUpdate> ParseSseFrame(string data, Dictionary<int, StreamingToolCallState> toolCalls)
    {
        data = data.Trim();
        if (string.IsNullOrWhiteSpace(data) || data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) yield break;

        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (TryReadUsageDetails(root, out var usage))
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                new List<AIContent> { new UsageContent(usage) { RawRepresentation = root.Clone() } })
            {
                ModelId = _model,
                RawRepresentation = root.Clone()
            };
        }

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) yield break;
        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) yield break;

        if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
        {
            var text = contentProp.GetString() ?? string.Empty;
            if (!string.IsNullOrEmpty(text))
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    new List<AIContent> { new TextContent(text) { RawRepresentation = text } })
                {
                    ModelId = _model
                };
            }
        }

        var reasoning = ReadReasoningText(delta);
        if (!string.IsNullOrEmpty(reasoning))
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                new List<AIContent> { new TextReasoningContent(reasoning) })
            {
                ModelId = _model
            };
        }

        foreach (var call in ReadToolCalls(delta))
        {
            var index = call.Index ?? toolCalls.Count;
            if (!toolCalls.TryGetValue(index, out var state))
            {
                state = new StreamingToolCallState();
                toolCalls[index] = state;
            }

            if (!string.IsNullOrWhiteSpace(call.Id)) state.Id = call.Id;
            if (!string.IsNullOrWhiteSpace(call.Name)) state.Name = call.Name;
            if (!string.IsNullOrEmpty(call.Arguments)) state.Arguments.Append(call.Arguments);
        }
    }

    private static string? ReadReasoningText(JsonElement delta)
    {
        foreach (var name in ReasoningFieldNames)
        {
            if (delta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }

        return null;
    }

    private static IEnumerable<ToolCallPart> ReadToolCalls(JsonElement element)
    {
        if (!element.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) yield break;
        foreach (var call in calls.EnumerateArray())
        {
            var function = call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object ? fn : default;
            yield return new ToolCallPart(
                GetString(call, "id") ?? Guid.NewGuid().ToString("N"),
                GetString(function, "name") ?? string.Empty,
                GetString(function, "arguments") ?? string.Empty,
                call.TryGetProperty("index", out var indexProp) && indexProp.TryGetInt32(out var index) ? index : null);
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool TryReadUsageDetails(JsonElement root, out UsageDetails usage)
    {
        usage = new UsageDetails();
        var source = root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
            ? usageElement
            : root;
        if (source.ValueKind != JsonValueKind.Object) return false;

        var input = ReadLong(source, "inputTokens", "input_tokens", "promptTokens", "prompt_tokens", "input");
        var output = ReadLong(source, "outputTokens", "output_tokens", "completionTokens", "completion_tokens", "output");
        var total = ReadLong(source, "totalTokens", "total_tokens");
        var cached = ReadLong(source, "cachedInputTokens", "cached_input_tokens", "cacheRead", "cache_read", "cachedTokens", "cached_tokens");
        if (cached <= 0)
        {
            cached = ReadNestedLong(source, "inputTokensDetails", "cachedTokens", "cached_tokens")
                + ReadNestedLong(source, "input_tokens_details", "cachedTokens", "cached_tokens")
                + ReadNestedLong(source, "promptTokensDetails", "cachedTokens", "cached_tokens")
                + ReadNestedLong(source, "prompt_tokens_details", "cachedTokens", "cached_tokens");
        }

        var reasoning = ReadLong(source, "reasoningOutputTokens", "reasoning_output_tokens", "reasoningTokens", "reasoning_tokens");
        if (reasoning <= 0)
        {
            reasoning = ReadNestedLong(source, "outputTokensDetails", "reasoningTokens", "reasoning_tokens")
                + ReadNestedLong(source, "output_tokens_details", "reasoningTokens", "reasoning_tokens")
                + ReadNestedLong(source, "completionTokensDetails", "reasoningTokens", "reasoning_tokens")
                + ReadNestedLong(source, "completion_tokens_details", "reasoningTokens", "reasoning_tokens");
        }

        if (total <= 0) total = input + output;
        if (input <= 0 && output <= 0 && total <= 0) return false;

        usage.InputTokenCount = input;
        usage.OutputTokenCount = output;
        usage.TotalTokenCount = total;
        usage.CachedInputTokenCount = cached;
        usage.ReasoningTokenCount = reasoning;
        return true;
    }

    private static long ReadNestedLong(JsonElement element, string objectName, params string[] names)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(objectName, out var nested)
           && nested.ValueKind == JsonValueKind.Object
            ? ReadLong(nested, names)
            : 0;

    private static long ReadLong(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (property.Value.TryGetInt64(out var number)) return Math.Max(0, number);
            if (long.TryParse(property.Value.ToString(), out number)) return Math.Max(0, number);
        }
        return 0;
    }

    private static readonly string[] ReasoningFieldNames =
    [
        "reasoning_content",
        "reasoningContent",
        "reasoning",
        "reasoning_text",
        "reasoningText",
        "thinking",
        "thinking_content",
        "thinkingContent"
    ];

    private static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(doc.RootElement.GetRawText(), JsonOptions) ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = json };
        }
    }

    private sealed record ToolCallPart(string Id, string Name, string Arguments, int? Index);

    private sealed class StreamingToolCallState
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
