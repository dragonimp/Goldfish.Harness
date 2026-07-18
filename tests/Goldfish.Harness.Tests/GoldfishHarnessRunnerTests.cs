using System.Reflection;
using Goldfish.Harness;
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
            BindingFlags.Instance | BindingFlags.NonPublic);

        var messages = Assert.IsType<List<Microsoft.Extensions.AI.ChatMessage>>(
            buildMessages!.Invoke(runner, [request]));

        var system = Assert.Single(messages, message => message.Role == ChatRole.System);
        Assert.Same(messages[0], system);
        Assert.Contains("基础系统提示", system.Text);
        Assert.Contains("用户偏好先给结论", system.Text);
    }
}
