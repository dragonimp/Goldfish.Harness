using System.Text.Json;
using Goldfish.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class AcpProtocolTests
{
    private readonly GoldfishAcpEventProjector _projector = new();

    [Fact]
    public void TextDelta_ProjectsToStandardAgentMessageChunk()
    {
        var frame = Assert.Single(_projector.Project("session-1", GoldfishHarnessEvent.Text(1, "hello")));
        var json = JsonSerializer.SerializeToElement(frame, GoldfishAcpProtocol.JsonOptions);

        Assert.Equal("2.0", json.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/update", json.GetProperty("method").GetString());
        var update = json.GetProperty("params").GetProperty("update");
        Assert.Equal("agent_message_chunk", update.GetProperty("sessionUpdate").GetString());
        Assert.Equal("hello", update.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public void ToolLifecycle_ProjectsWithoutNativeHarnessEvent()
    {
        var start = GoldfishHarnessEvent.ToolCallStart(2, "read_file", "{\"path\":\"a.txt\"}", "call-1");
        var result = GoldfishHarnessEvent.ToolResult(2, new ToolCallRecord
        {
            ToolId = "read_file",
            ToolCallId = "call-1",
            Arguments = "{\"path\":\"a.txt\"}",
            Result = "content",
            Success = true
        });

        var startJson = JsonSerializer.SerializeToElement(Assert.Single(_projector.Project("session-1", start)));
        var resultJson = JsonSerializer.SerializeToElement(Assert.Single(_projector.Project("session-1", result)));

        Assert.Equal("tool_call", startJson.GetProperty("params").GetProperty("update").GetProperty("sessionUpdate").GetString());
        Assert.Equal("tool_call_update", resultJson.GetProperty("params").GetProperty("update").GetProperty("sessionUpdate").GetString());
        Assert.DoesNotContain("GoldfishEventKind", startJson.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_AdvertisesAcpVersionOne()
    {
        var json = JsonSerializer.SerializeToElement(GoldfishAcpProtocol.InitializeResult("goldfish", "1.0"));
        Assert.Equal(1, json.GetProperty("protocolVersion").GetInt32());
        Assert.True(json.GetProperty("agentCapabilities").GetProperty("sessionCapabilities").GetProperty("cancel").GetBoolean());
    }

    [Fact]
    public void RuntimeError_IsAcpSessionUpdateWithAgentFreeMetadata()
    {
        var json = JsonSerializer.SerializeToElement(GoldfishAcpProtocol.RuntimeError("session-1", "failed", "TestError"));
        Assert.Equal("session/update", json.GetProperty("method").GetString());
        var update = json.GetProperty("params").GetProperty("update");
        Assert.Equal("agent_message_chunk", update.GetProperty("sessionUpdate").GetString());
        Assert.Equal("_agentfree/runtime.error", update.GetProperty("_meta").GetProperty("agentfree").GetProperty("eventType").GetString());
    }

    [Fact]
    public void TokenUsage_ProjectsToStandardUsageUpdate()
    {
        var usage = new UsageDetails
        {
            InputTokenCount = 101,
            CachedInputTokenCount = 70,
            OutputTokenCount = 23,
            ReasoningTokenCount = 9,
            TotalTokenCount = 124
        };

        var frame = Assert.Single(_projector.Project("session-usage", GoldfishHarnessEvent.TokenUsage(1, usage)));
        var json = JsonSerializer.SerializeToElement(frame, GoldfishAcpProtocol.JsonOptions);
        var update = json.GetProperty("params").GetProperty("update");
        var last = update.GetProperty("last");

        Assert.Equal("usage_update", update.GetProperty("sessionUpdate").GetString());
        Assert.Equal(124, update.GetProperty("used").GetInt64());
        Assert.Equal(101, last.GetProperty("input_tokens").GetInt64());
        Assert.Equal(70, last.GetProperty("cached_input_tokens").GetInt64());
        Assert.Equal(23, last.GetProperty("output_tokens").GetInt64());
        Assert.Equal(9, last.GetProperty("reasoning_output_tokens").GetInt64());
        Assert.Equal("usage_update", update.GetProperty("_meta").GetProperty("agentfree").GetProperty("eventType").GetString());
    }

    [Fact]
    public void LocalToolAttachment_ProjectsToStagedFileExtension()
    {
        var ev = GoldfishHarnessEvent.ToolResult(2, new ToolCallRecord
        {
            ToolId = "send_gateway_file",
            ToolCallId = "call-file",
            Result = "ok",
            Success = true,
            Attachments = [new { path = "/tmp/report.txt", name = "report.txt", deliveryMode = "passthrough", expiryMinutes = 45 }]
        });

        var frames = _projector.Project("session-file", ev)
            .Select(frame => JsonSerializer.SerializeToElement(frame, GoldfishAcpProtocol.JsonOptions))
            .ToList();
        var file = Assert.Single(frames, frame => frame.TryGetProperty("method", out var method)
            && method.GetString() == "_agentfree/file.staged");
        var metadata = file.GetProperty("params").GetProperty("attachment").GetProperty("metadata");
        Assert.Equal("/tmp/report.txt", metadata.GetProperty("localPath").GetString());
        Assert.Equal("gateway_passthrough", metadata.GetProperty("deliveryMode").GetString());
        Assert.Equal(45, metadata.GetProperty("expiryMinutes").GetInt32());
    }
}
