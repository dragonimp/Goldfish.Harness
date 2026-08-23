using Goldfish.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class HarnessKernelTests
{
    [Fact]
    public async Task StreamAsync_RecordsAppendOnlyEventsAndCompletedTerminalState()
    {
        var store = new InMemoryHarnessTurnEventStore();
        var runner = new GoldfishHarnessRunner(new StaticChatClient("完成"), new ToolRegistry(), skillRegistry: null);
        var kernel = new GoldfishHarnessKernel(runner, store);
        var request = Request("thread-1");

        await foreach (var _ in kernel.StreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        var turn = Assert.Single(await store.ListAsync("thread-1", TestContext.Current.CancellationToken));
        Assert.Equal(GoldfishTurnStatus.Completed, turn.Status);
        Assert.Equal("完成", turn.TerminalReason);
        var events = await store.ReadEventsAsync(turn.TurnId, TestContext.Current.CancellationToken);
        Assert.Contains(events, entry => entry.Event.Kind == GoldfishEventKind.RunStarted);
        Assert.Contains(events, entry => entry.Event.Kind == GoldfishEventKind.Completed);
    }

    [Fact]
    public async Task JsonlStore_AppendsTerminalStateWithoutReplacingPriorEvents()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), $"goldfish-kernel-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonlHarnessTurnEventStore(stateRoot);
            var turn = new GoldfishHarnessTurn { TurnId = "turn-1", SessionId = "session-1" };
            await store.StartAsync(turn, TestContext.Current.CancellationToken);
            await store.AppendAsync(new GoldfishHarnessTurnEvent(turn.TurnId, turn.SessionId, GoldfishHarnessEvent.Thinking(1, "分析")), TestContext.Current.CancellationToken);
            await store.CompleteAsync(turn.TurnId, GoldfishTurnStatus.Failed, "denied", TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(Path.Combine(stateRoot, "turn-events.jsonl"), TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length);
            var saved = await store.GetAsync(turn.TurnId, TestContext.Current.CancellationToken);
            Assert.Equal(GoldfishTurnStatus.Failed, saved!.Status);
            Assert.Equal("denied", saved.TerminalReason);
        }
        finally
        {
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, recursive: true);
        }
    }

    private static GoldfishHarnessRequest Request(string sessionId) => new(
        new AgentInfo { Id = "agent-1", Name = "Test", SystemPrompt = "测试" },
        sessionId,
        "你好",
        new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "你好"),
        []);

    private sealed class StaticChatClient(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, response)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(response)]);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
