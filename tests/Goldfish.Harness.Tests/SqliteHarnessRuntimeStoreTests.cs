using Goldfish.Harness;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class SqliteHarnessRuntimeStoreTests
{
    [Fact]
    public async Task PersistsTurnEventsAndFirstTerminalStateAcrossRestart()
    {
        var root = NewRoot();
        var database = Path.Combine(root, "harness-state.db");
        var turn = NewTurn("request-1");

        using (var store = new SqliteHarnessStateStore(database))
        {
            var created = await store.GetOrCreateTurnAsync(turn, "hello", TestContext.Current.CancellationToken);
            Assert.True(created.Created);
            Assert.True(await store.TryStartAsync(turn.TurnId, "host-1", DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken));
            await store.AppendEventsAsync(turn.TurnId, turn.SessionId,
                [GoldfishHarnessEvent.Text(1, "world"), GoldfishHarnessEvent.Completed(1, "world")],
                TestContext.Current.CancellationToken);
            Assert.True(await store.TryCompleteAsync(turn.TurnId, GoldfishTurnStatus.Completed,
                "end_turn", "end_turn", "world", TestContext.Current.CancellationToken));
            Assert.False(await store.TryCompleteAsync(turn.TurnId, GoldfishTurnStatus.Canceled,
                "cancelled", "late cancel", null, TestContext.Current.CancellationToken));
        }

        using (var reopened = new SqliteHarnessStateStore(database))
        {
            var stored = await reopened.GetTurnAsync(turn.TurnId, TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            Assert.Equal(GoldfishTurnStatus.Completed, stored.Status);
            Assert.Equal("end_turn", stored.TerminalReasonCode);
            var events = await reopened.ReadEventsAsync(turn.TurnId, TestContext.Current.CancellationToken);
            Assert.Equal([GoldfishEventKind.TextDelta, GoldfishEventKind.Completed], events.Select(x => x.Event.Kind));
        }
    }

    [Fact]
    public async Task DeduplicatesRequestWithinFullPartitionAndRecoversExpiredLease()
    {
        var root = NewRoot();
        using var store = new SqliteHarnessStateStore(Path.Combine(root, "harness-state.db"));
        var turn = NewTurn("request-2");
        Assert.True((await store.GetOrCreateTurnAsync(turn, "first", TestContext.Current.CancellationToken)).Created);
        Assert.False((await store.GetOrCreateTurnAsync(turn with { TurnId = "different" }, "duplicate", TestContext.Current.CancellationToken)).Created);
        Assert.True(await store.TryStartAsync(turn.TurnId, "dead-host", DateTimeOffset.UtcNow.AddSeconds(-1), TestContext.Current.CancellationToken));
        Assert.Equal(1, await store.RecoverOrphanedAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.Equal(GoldfishTurnStatus.Orphaned,
            (await store.GetTurnAsync(turn.TurnId, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task StoresLargePayloadAsBlobAndResetPreservesAudit()
    {
        var root = NewRoot();
        var database = Path.Combine(root, "harness-state.db");
        using var store = new SqliteHarnessStateStore(database, new HarnessStateOptions { InlinePayloadBytes = 32 });
        var turn = NewTurn("request-3");
        await store.GetOrCreateTurnAsync(turn, "secret conversation", TestContext.Current.CancellationToken);
        await store.AppendEventsAsync(turn.TurnId, turn.SessionId,
            [GoldfishHarnessEvent.Text(1, new string('x', 1024))], TestContext.Current.CancellationToken);

        Assert.Equal(new string('x', 1024),
            Assert.Single(await store.ReadEventsAsync(turn.TurnId, TestContext.Current.CancellationToken)).Event.Delta);
        await store.ResetSessionAsync(turn.Partition, TestContext.Current.CancellationToken);
        Assert.NotNull(await store.GetTurnAsync(turn.TurnId, TestContext.Current.CancellationToken));

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memory_messages;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ImportsLegacyJsonlIdempotently()
    {
        var root = NewRoot();
        var legacy = new JsonlHarnessTurnEventStore(root);
        var turn = NewTurn("legacy-request") with { TurnId = "legacy-turn" };
        await legacy.StartAsync(turn, TestContext.Current.CancellationToken);
        await legacy.AppendAsync(new GoldfishHarnessTurnEvent(turn.TurnId, turn.SessionId,
            GoldfishHarnessEvent.Text(1, "legacy text")), TestContext.Current.CancellationToken);
        await legacy.CompleteAsync(turn.TurnId, GoldfishTurnStatus.Completed, "end_turn",
            TestContext.Current.CancellationToken);

        using var store = new SqliteHarnessStateStore(Path.Combine(root, "harness-state.db"));
        var first = await JsonlHarnessImporter.ImportAsync(Path.Combine(root, "turn-events.jsonl"), store,
            DateTimeOffset.UtcNow.AddDays(-1), TestContext.Current.CancellationToken);
        var second = await JsonlHarnessImporter.ImportAsync(Path.Combine(root, "turn-events.jsonl"), store,
            DateTimeOffset.UtcNow.AddDays(-1), TestContext.Current.CancellationToken);

        Assert.Equal(1, first.ImportedTurns);
        Assert.Equal(1, first.ImportedEvents);
        Assert.Equal(1, second.SkippedLines);
        Assert.Equal(GoldfishTurnStatus.Completed,
            (await store.GetTurnAsync(turn.TurnId, TestContext.Current.CancellationToken))!.Status);
    }

    private static GoldfishHarnessTurn NewTurn(string requestId) => new()
    {
        TurnId = $"turn-{requestId}",
        RequestId = requestId,
        RunId = $"run-{requestId}",
        TenantId = "tenant",
        UserId = "user",
        AgentId = "agent",
        WorkspaceId = "workspace",
        SessionId = "session",
        Strategy = "ReAct"
    };

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "goldfish-runtime-store-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}
