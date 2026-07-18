using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Goldfish.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class GoldfishSessionQueueTests
{
    [Fact]
    public async Task SameSession_IsFifoAndNeverConcurrent()
    {
        var order = new ConcurrentQueue<string>();
        var running = 0;
        var maxRunning = 0;
        var queue = new GoldfishSessionQueue(Stream(async (request, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            order.Enqueue(request.UserMessageText);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return [GoldfishHarnessEvent.Thinking(1, request.UserMessageText)];
        }));

        var submissions = Enumerable.Range(1, 3).Select(i => queue.Enqueue(Request("s", $"m{i}"))).ToArray();
        await Task.WhenAll(submissions.Select(s => DrainAsync(s.Events)));

        Assert.Equal(["m1", "m2", "m3"], order);
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task DifferentSessions_RunConcurrently()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var queue = new GoldfishSessionQueue(Stream(async (request, ct) =>
        {
            if (Interlocked.Increment(ref count) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Array.Empty<GoldfishHarnessEvent>();
        }));

        var a = queue.Enqueue(Request("a", "one"));
        var b = queue.Enqueue(Request("b", "two"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        await Task.WhenAll(DrainAsync(a.Events), DrainAsync(b.Events));
    }

    [Fact]
    public async Task Cancel_RemovesWaitingItem()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<string>();
        var queue = new GoldfishSessionQueue(Stream(async (request, ct) =>
        {
            executed.Enqueue(request.UserMessageText);
            if (request.UserMessageText == "first") await release.Task.WaitAsync(ct);
            return Array.Empty<GoldfishHarnessEvent>();
        }));
        var first = queue.Enqueue(Request("s", "first"), "1");
        var second = queue.Enqueue(Request("s", "second"), "2");

        Assert.True(queue.Cancel("s", "2"));
        release.SetResult();
        await DrainAsync(first.Events);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DrainAsync(second.Events));
        Assert.Equal(["first"], executed);
    }

    [Fact]
    public async Task Status_ContainsRunningAndWaitingSummaries()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new GoldfishSessionQueue(Stream(async (request, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Array.Empty<GoldfishHarnessEvent>();
        }));
        var first = queue.Enqueue(Request("s", "current message"), "run");
        var second = queue.Enqueue(Request("s", "next message"), "wait");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var status = queue.GetStatus("s");
        Assert.Equal("run", status.Running?.MessageId);
        Assert.Equal(1, status.WaitingCount);
        Assert.Equal("wait", status.Waiting[0].MessageId);
        Assert.Equal("next message", status.Waiting[0].Summary);
        release.SetResult();
        await Task.WhenAll(DrainAsync(first.Events), DrainAsync(second.Events));
    }

    [Fact]
    public async Task PerItemExecutor_PreservesSteerSourceForTheActiveRun()
    {
        var steer = new TestSteerSource();
        var observed = false;
        var queue = new GoldfishSessionQueue();
        var request = Request("s", "message") with { SteerSource = steer };
        var submission = queue.Enqueue(request, Stream((actual, _) =>
        {
            observed = ReferenceEquals(steer, actual.SteerSource);
            return Task.FromResult<IEnumerable<GoldfishHarnessEvent>>([]);
        }));

        await DrainAsync(submission.Events);
        Assert.True(observed);
        Assert.Equal(0, steer.DrainCount); // only the runner, never the queue, consumes steer
    }

    [Fact]
    public async Task StreamingAndNonStreamingRuns_ShareOneFifo()
    {
        var order = new ConcurrentQueue<string>();
        var queue = new GoldfishSessionQueue();
        var streamExecutor = Stream(async (request, ct) =>
        {
            order.Enqueue(request.UserMessageText);
            await Task.Delay(15, ct);
            return [];
        });
        var first = queue.Enqueue(Request("mixed", "stream-1"), streamExecutor);
        var run = queue.EnqueueRunAsync(Request("mixed", "run-2"), async (request, ct) =>
        {
            order.Enqueue(request.UserMessageText);
            await Task.Delay(15, ct);
            return new GoldfishHarnessRunResult { Answer = "done" };
        });
        var third = queue.Enqueue(Request("mixed", "stream-3"), streamExecutor);

        await Task.WhenAll(DrainAsync(first.Events), run, DrainAsync(third.Events));
        var runResult = await run;

        Assert.Equal("done", runResult.Answer);
        Assert.Equal(["stream-1", "run-2", "stream-3"], order);
    }

    private static GoldfishHarnessRequest Request(string session, string text) => new(
        new AgentInfo { Id = "agent" }, session, text,
        new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, text), [], SteerSource: null);

    private static async Task DrainAsync(IAsyncEnumerable<GoldfishHarnessEvent> events)
    {
        await foreach (var _ in events) { }
    }

    private static Func<GoldfishHarnessRequest, CancellationToken, IAsyncEnumerable<GoldfishHarnessEvent>> Stream(
        Func<GoldfishHarnessRequest, CancellationToken, Task<IEnumerable<GoldfishHarnessEvent>>> execute) =>
        (request, ct) => StreamTestExtensions.Run(execute, request, ct);
}

internal sealed class TestSteerSource : IGoldfishSteerSource
{
    public int DrainCount { get; private set; }
    public ValueTask<IReadOnlyList<string>> DrainAsync(string sessionId, CancellationToken ct)
    {
        DrainCount++;
        return ValueTask.FromResult<IReadOnlyList<string>>([]);
    }
}

internal static class StreamTestExtensions
{
    public static async IAsyncEnumerable<GoldfishHarnessEvent> Run(
        Func<GoldfishHarnessRequest, CancellationToken, Task<IEnumerable<GoldfishHarnessEvent>>> execute,
        GoldfishHarnessRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in await execute(request, ct)) yield return item;
    }
}
