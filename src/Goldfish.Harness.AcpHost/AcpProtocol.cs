using System.Text.Json;
using System.Text.Json.Serialization;
using Goldfish.Acp;
using Microsoft.Extensions.AI;

namespace Goldfish.Harness;

public static class GoldfishAcpProtocol
{
    public const int Version = AcpConstants.ProtocolVersion;
    public const string JsonRpcVersion = AcpConstants.JsonRpcVersion;

    public static JsonSerializerOptions JsonOptions => AcpJson.Options;

    public static object InitializeResult(
        string name,
        string version,
        int? schemaVersion = null,
        string? stateMode = null) => AcpProtocol.Convert<Goldfish.Acp.Protocol.V1.InitializeResponse>(new
    {
        protocolVersion = Version,
        agentCapabilities = new
        {
            loadSession = false,
            promptCapabilities = new { image = false, audio = false, embeddedContext = false },
            mcpCapabilities = new { http = true, sse = true },
            sessionCapabilities = new { cancel = true }
        },
        authMethods = Array.Empty<object>(),
        agentInfo = new { name, version },
        _meta = new
        {
            goldfish = new
            {
                kernelVersion = version,
                schemaVersion,
                stateMode
            },
            agentfree = new
            {
                runtime = "goldfish-harness",
                nativeEvents = false,
                goldfish = new
                {
                    kernelVersion = version,
                    schemaVersion,
                    stateMode
                }
            }
        }
    });

    public static object Response(object? id, object? result)
        => new AcpJsonRpcResponse<object?>(id, result);

    public static object Error(object? id, int code, string message, object? data = null)
        => new AcpJsonRpcErrorResponse(
            id,
            new AcpJsonRpcError(
                code,
                message,
                data == null ? null : JsonSerializer.SerializeToElement(data, AcpJson.Options)));

    public static object SessionUpdate(string sessionId, object update)
        => AcpProtocol.SessionUpdate(sessionId, update);

    public static object PromptResult(object? id, string stopReason = "end_turn")
        => AcpProtocol.PromptResult(id, stopReason);

    public static object RuntimeError(string sessionId, string message, string? code = null)
        => new
        {
            jsonrpc = JsonRpcVersion,
            method = "_agentfree/runtime.error",
            @params = new
            {
                sessionId,
                message,
            _meta = new
            {
                agentfree = new
                {
                    eventType = "_agentfree/runtime.error",
                    code,
                    message,
                    timestamp = DateTimeOffset.UtcNow.ToString("O")
                }
            }
            }
        };
}

public sealed class GoldfishAcpEventProjector
{
    public IEnumerable<object> Project(string sessionId, GoldfishHarnessEvent ev)
    {
        object? update = ev.Kind switch
        {
            GoldfishEventKind.RunStarted => SessionInfo(ev),
            GoldfishEventKind.TextDelta => MessageChunk("agent_message_chunk", ev.Delta, ev),
            GoldfishEventKind.ThinkingDelta => MessageChunk("agent_thought_chunk", ev.Delta, ev),
            GoldfishEventKind.ToolCallStarted => ToolCall(ev),
            GoldfishEventKind.ToolResult => ToolResult(ev),
            GoldfishEventKind.ReasoningStrategySelected
                or GoldfishEventKind.PlanCreated
                or GoldfishEventKind.PlanStepStarted
                or GoldfishEventKind.PlanStepCompleted
                or GoldfishEventKind.PlanStepFailed
                or GoldfishEventKind.ReflectionCompleted
                or GoldfishEventKind.ReWooGraphCreated
                or GoldfishEventKind.ReasoningTraceCompleted => MessageChunk("agent_thought_chunk", ev.Delta, ev),
            GoldfishEventKind.TokenUsage => UsageUpdate(ev),
            // Runtime failures are terminal JSON-RPC errors. Program.cs emits the
            // extension notification followed by exactly one -32603 response.
            GoldfishEventKind.Failed => null,
            GoldfishEventKind.Completed => null,
            _ => null
        };

        if (update is not null)
            yield return GoldfishAcpProtocol.SessionUpdate(sessionId, update);

        if (ev.Kind == GoldfishEventKind.ToolResult && ev.Attachments is { Count: > 0 })
        {
            foreach (var attachment in ev.Attachments)
                yield return Attachment(sessionId, attachment, ev);
        }
    }

    private static object SessionInfo(GoldfishHarnessEvent ev) => new
    {
        sessionUpdate = "session_info_update",
        updatedAt = ev.Timestamp.ToString("O"),
        _meta = Meta(ev)
    };

    private static object MessageChunk(string kind, string text, GoldfishHarnessEvent ev) => new
    {
        sessionUpdate = kind,
        content = new { type = "text", text },
        _meta = Meta(ev)
    };

    private static object ToolCall(GoldfishHarnessEvent ev) => new
    {
        sessionUpdate = "tool_call",
        toolCallId = ToolCallId(ev),
        title = ev.ToolId ?? "tool",
        kind = "other",
        status = "in_progress",
        rawInput = ParseJsonOrText(ev.Arguments),
        _meta = Meta(ev)
    };

    private static object ToolResult(GoldfishHarnessEvent ev) => new
    {
        sessionUpdate = "tool_call_update",
        toolCallId = ToolCallId(ev),
        title = ev.ToolId ?? "tool",
        kind = "other",
        status = ev.Success == false ? "failed" : "completed",
        content = new[] { new { type = "content", content = new { type = "text", text = ev.Result ?? ev.Delta } } },
        rawOutput = ev.Result,
        _meta = Meta(ev)
    };

    private static object UsageUpdate(GoldfishHarnessEvent ev)
    {
        var last = TokenBreakdown(ev.Usage);
        return new
        {
            sessionUpdate = "usage_update",
            used = last.TotalTokens,
            size = last.TotalTokens,
            last,
            total = last,
            _meta = new
            {
                native = new { last, total = last },
                agentfree = new
                {
                    eventType = "usage_update",
                    ev.RunId,
                    ev.EventId,
                    ev.Step,
                    timestamp = ev.Timestamp.ToString("O")
                }
            }
        };
    }

    private static object Attachment(string sessionId, object attachment, GoldfishHarnessEvent ev)
    {
        var uri = ReadAttachment(attachment, "url", "dataUrl");
        if (!string.IsNullOrWhiteSpace(uri))
        {
            return GoldfishAcpProtocol.SessionUpdate(sessionId, new
            {
                sessionUpdate = "agent_message_chunk",
                content = new
                {
                    type = "resource_link",
                    name = ReadAttachment(attachment, "name", "fileName") ?? "attachment",
                    uri,
                    mimeType = ReadAttachment(attachment, "mediaType", "mimeType") ?? "application/octet-stream"
                },
                _meta = new { agentfree = new { attachment, ev.RunId, ev.EventId, ev.Step } }
            });
        }

        var path = ReadAttachment(attachment, "path", "localPath") ?? string.Empty;
        var requestedMode = ReadAttachment(attachment, "deliveryMode") ?? "channel";
        var deliveryMode = string.Equals(requestedMode, "passthrough", StringComparison.OrdinalIgnoreCase)
            ? "gateway_passthrough"
            : "channel_upload";
        return new
        {
            jsonrpc = GoldfishAcpProtocol.JsonRpcVersion,
            method = "_agentfree/file.staged",
            @params = new
            {
                sessionId,
                attachment = new
                {
                    type = "file",
                    name = ReadAttachment(attachment, "name", "fileName") ?? Path.GetFileName(path),
                    mediaType = ReadAttachment(attachment, "mediaType", "mimeType") ?? "application/octet-stream",
                    metadata = new
                    {
                        localPath = path,
                        allowedRoot = Path.GetDirectoryName(path),
                        deliveryMode,
                        requestedDeliveryMode = requestedMode,
                        expiryMinutes = ReadAttachmentNumber(attachment, "expiryMinutes") ?? 30
                    }
                },
                _meta = new { agentfree = new { source = "goldfish-harness", ev.RunId, ev.EventId, ev.Step } }
            }
        };
    }

    private static object Meta(GoldfishHarnessEvent ev) => new
    {
        agentfree = new
        {
            ev.RunId,
            ev.EventId,
            ev.Step,
            timestamp = ev.Timestamp.ToString("O")
        }
    };

    private static AcpTokenBreakdown TokenBreakdown(UsageDetails? usage)
    {
        var input = Math.Max(0, usage?.InputTokenCount ?? 0);
        var cached = Math.Max(0, usage?.CachedInputTokenCount ?? 0);
        var output = Math.Max(0, usage?.OutputTokenCount ?? 0);
        var reasoning = Math.Max(0, usage?.ReasoningTokenCount ?? 0);
        var total = Math.Max(0, usage?.TotalTokenCount ?? 0);
        if (total <= 0) total = input + output;
        return new AcpTokenBreakdown(input, cached, output, reasoning, total);
    }

    private sealed record AcpTokenBreakdown(
        [property: JsonPropertyName("input_tokens")] long InputTokens,
        [property: JsonPropertyName("cached_input_tokens")] long CachedInputTokens,
        [property: JsonPropertyName("output_tokens")] long OutputTokens,
        [property: JsonPropertyName("reasoning_output_tokens")] long ReasoningOutputTokens,
        [property: JsonPropertyName("total_tokens")] long TotalTokens);

    private static string ToolCallId(GoldfishHarnessEvent ev)
        => string.IsNullOrWhiteSpace(ev.ToolCallId) ? $"{ev.Step}:{ev.ToolId ?? "tool"}" : ev.ToolCallId;

    private static object? ParseJsonOrText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string? ReadAttachment(object value, params string[] names)
    {
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, GoldfishAcpProtocol.JsonOptions);
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }
        return null;
    }

    private static int? ReadAttachmentNumber(object value, params string[] names)
    {
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, GoldfishAcpProtocol.JsonOptions);
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.TryGetInt32(out var number))
                return number;
        }
        return null;
    }
}
