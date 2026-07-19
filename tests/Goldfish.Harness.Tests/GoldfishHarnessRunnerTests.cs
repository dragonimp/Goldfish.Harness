using System.Reflection;
using Goldfish.Harness;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class GoldfishHarnessRunnerTests
{
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
        Assert.False(string.IsNullOrWhiteSpace(auditRecord.ArgumentsHash));
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
        command.CommandText = "SELECT arguments_hash, result_hash FROM goldfish_tool_executions WHERE tool_id = 'tool'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ToolExecutionHash.Sha256("""{"secret":"value"}"""), reader.GetString(0));
        Assert.Equal(ToolExecutionHash.Sha256("result"), reader.GetString(1));
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

    private sealed class RecordingTool : ITool
    {
        public bool Executed { get; private set; }
        public string Id => "recording.tool";
        public string Name => "recording_tool";
        public string Description => "Records whether it was executed.";
        public string ParametersSchema => """{"type":"object","additionalProperties":true}""";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<ToolResult> ExecuteAsync(string arguments)
        {
            Executed = true;
            return Task.FromResult(new ToolResult
            {
                Success = true,
                Data = new { ok = true }
            });
        }
    }

    private sealed class DenyToolAuthorizationHook(string reason) : IToolAuthorizationHook
    {
        public ValueTask<ToolAuthorizationResult> AuthorizeAsync(
            ToolAuthorizationRequest request,
            CancellationToken ct = default)
            => ValueTask.FromResult(ToolAuthorizationResult.Deny(reason));
    }
}
