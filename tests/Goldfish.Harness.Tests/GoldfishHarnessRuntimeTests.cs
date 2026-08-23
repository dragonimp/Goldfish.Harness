using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Goldfish.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class GoldfishHarnessRuntimeTests
{
    [Fact]
    public async Task CompletedRequestIsReplayedWithoutExecutingStrategyAgain()
    {
        var root = NewRoot();
        using var store = new SqliteHarnessStateStore(Path.Combine(root, "harness-state.db"));
        await using var runtime = new GoldfishHarnessRuntime(store,
            options: new HarnessStateOptions { DeltaBatchMilliseconds = 5 });
        var strategy = new RecordingStrategy(async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return [GoldfishHarnessEvent.Text(1, "hello"), GoldfishHarnessEvent.Completed(1, "hello")];
        });
        var request = TurnRequest("request-replay", "tenant", "user");

        var first = await DrainAsync(runtime.ExecuteAsync(request, new PassthroughContextProvider(), strategy));
        var replay = await DrainAsync(runtime.ExecuteAsync(request, new PassthroughContextProvider(), strategy));

        Assert.Equal(1, strategy.CallCount);
        Assert.Equal(first.Select(x => x.Kind), replay.Select(x => x.Kind));
        Assert.Equal("hello", replay.Single(x => x.Kind == GoldfishEventKind.Completed).Delta);
    }

    [Fact]
    public async Task FullPartitionQueueSerializesSameUserButAllowsDifferentUsers()
    {
        var root = NewRoot();
        using var store = new SqliteHarnessStateStore(Path.Combine(root, "harness-state.db"));
        await using var runtime = new GoldfishHarnessRuntime(store);
        var runningByUser = new ConcurrentDictionary<string, int>();
        var maxByUser = new ConcurrentDictionary<string, int>();
        var totalRunning = 0;
        var maxTotal = 0;
        var strategy = new RecordingStrategy(async (request, ct) =>
        {
            var user = request.AgentInfo.ExtraData!["user"];
            var currentUser = runningByUser.AddOrUpdate(user, 1, (_, value) => value + 1);
            maxByUser.AddOrUpdate(user, currentUser, (_, value) => Math.Max(value, currentUser));
            var currentTotal = Interlocked.Increment(ref totalRunning);
            maxTotal = Math.Max(maxTotal, currentTotal);
            await Task.Delay(40, ct);
            runningByUser.AddOrUpdate(user, 0, (_, value) => value - 1);
            Interlocked.Decrement(ref totalRunning);
            return [GoldfishHarnessEvent.Completed(1, user)];
        });

        var tasks = new[]
        {
            DrainAsync(runtime.ExecuteAsync(TurnRequest("a1", "tenant", "a"), new PassthroughContextProvider(), strategy)),
            DrainAsync(runtime.ExecuteAsync(TurnRequest("a2", "tenant", "a"), new PassthroughContextProvider(), strategy)),
            DrainAsync(runtime.ExecuteAsync(TurnRequest("b1", "tenant", "b"), new PassthroughContextProvider(), strategy))
        };
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxByUser["a"]);
        Assert.True(maxTotal >= 2);
    }

    [Fact]
    public async Task CancelProducesCanceledTerminalWithoutOverwritingCompletion()
    {
        var root = NewRoot();
        using var store = new SqliteHarnessStateStore(Path.Combine(root, "harness-state.db"));
        await using var runtime = new GoldfishHarnessRuntime(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var strategy = new RecordingStrategy(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        });
        var request = TurnRequest("cancel-request", "tenant", "user") with { TurnId = "cancel-turn" };
        var drain = DrainAsync(runtime.ExecuteAsync(request, new PassthroughContextProvider(), strategy));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(runtime.Cancel("cancel-turn"));
        var events = await drain;

        Assert.Contains(events, x => x.Kind == GoldfishEventKind.Failed && x.Delta == "Turn canceled.");
        Assert.Equal(GoldfishTurnStatus.Canceled,
            (await store.GetTurnAsync("cancel-turn", TestContext.Current.CancellationToken))!.Status);
    }

    private static GoldfishHarnessTurnRequest TurnRequest(string requestId, string tenant, string user)
    {
        var partition = new MemoryPartition
        {
            TenantId = tenant,
            UserId = user,
            AgentId = "agent",
            WorkspaceId = "workspace",
            SessionId = "session"
        };
        return new GoldfishHarnessTurnRequest(new GoldfishHarnessSessionRequest(
            new AgentInfo
            {
                Id = "agent",
                Name = "agent",
                ExtraData = new Dictionary<string, string> { ["user"] = user }
            },
            "session",
            requestId,
            partition),
            RequestId: requestId);
    }

    private static async Task<List<GoldfishHarnessEvent>> DrainAsync(IAsyncEnumerable<GoldfishHarnessEvent> source)
    {
        var events = new List<GoldfishHarnessEvent>();
        await foreach (var ev in source) events.Add(ev);
        return events;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "goldfish-runtime-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class PassthroughContextProvider : IHarnessContextProvider
    {
        public string Name => "test";
        public int Order => 0;
        public Task<HarnessContextBuildResult> BuildAsync(GoldfishHarnessSessionRequest request, CancellationToken ct)
            => Task.FromResult(new HarnessContextBuildResult(new GoldfishHarnessRequest(
                request.AgentInfo,
                request.SessionId,
                request.Prompt,
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, request.Prompt),
                [],
                ReasoningOptions: request.ReasoningOptions), []));
    }

    private sealed class RecordingStrategy(
        Func<GoldfishHarnessRequest, CancellationToken, Task<IEnumerable<GoldfishHarnessEvent>>> execute)
        : IHarnessExecutionStrategy
    {
        public int CallCount;

        public async IAsyncEnumerable<GoldfishHarnessEvent> ExecuteAsync(
            GoldfishHarnessRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            foreach (var ev in await execute(request, ct)) yield return ev;
        }
    }
}
