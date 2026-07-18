using Goldfish.Harness;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class SqliteMemoryManagerTests
{
    [Fact]
    public async Task UserProfile_DefaultGlobalScopeSharesAcrossAgentsButNeverAcrossUsers()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var manager = new SqliteMemoryManager(
                new SqliteMemoryOptions { Enabled = true, DatabasePath = databasePath });
            var aliceAgentA = Partition("tenant", "alice", "agent-a", "session-a");
            var aliceAgentB = Partition("tenant", "alice", "agent-b", "session-b");
            var bobAgentB = Partition("tenant", "bob", "agent-b", "session-c");

            var stored = await UserProfileMemory.ExtractAndStoreAsync(
                manager,
                aliceAgentA,
                "我偏好的界面主色是琥珀橙，偏好的回答风格是先给结论再给依据");

            Assert.Equal(2, stored);
            var aliceContext = await manager.BuildContextAsync(
                aliceAgentB,
                "我的偏好是什么",
                new MemoryOptions { UserProfile = new UserProfileMemoryOptions() });
            var bobContext = await manager.BuildContextAsync(
                bobAgentB,
                "我的偏好是什么",
                new MemoryOptions { UserProfile = new UserProfileMemoryOptions() });

            Assert.Contains(aliceContext.LongTermMemories, memory => memory.Content.Contains("琥珀橙"));
            Assert.Contains(aliceContext.LongTermMemories, memory => memory.Content.Contains("先给结论"));
            Assert.Empty(bobContext.LongTermMemories);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UserProfile_AgentScopeIsIsolatedAndUpdatesCategoryInPlace()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var manager = new SqliteMemoryManager(
                new SqliteMemoryOptions { Enabled = true, DatabasePath = databasePath });
            var agentA = Partition("tenant", "alice", "agent-a", "session-a");
            var agentB = Partition("tenant", "alice", "agent-b", "session-b");
            var options = new UserProfileMemoryOptions { Scope = UserProfileScope.Agent };

            await UserProfileMemory.ExtractAndStoreAsync(manager, agentA, "我偏好的界面主色是琥珀橙", options);
            await UserProfileMemory.ExtractAndStoreAsync(manager, agentA, "我偏好的界面主色是深海蓝", options);

            var own = await manager.BuildContextAsync(
                agentA,
                "界面主色",
                new MemoryOptions { UserProfile = options });
            var other = await manager.BuildContextAsync(
                agentB,
                "界面主色",
                new MemoryOptions { UserProfile = options });

            Assert.Single(own.LongTermMemories);
            Assert.Contains("深海蓝", own.LongTermMemories[0].Content);
            Assert.DoesNotContain("琥珀橙", own.LongTermMemories[0].Content);
            Assert.Empty(other.LongTermMemories);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SameSessionId_IsHardIsolatedByTenantUserAndAgent()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var manager = new SqliteMemoryManager(
                new SqliteMemoryOptions { Enabled = true, DatabasePath = databasePath },
                VectorOptions(),
                new ConstantEmbeddingClient());
            var alice = Partition("tenant-1", "alice", "agent-1", "same-session");
            var bob = Partition("tenant-1", "bob", "agent-1", "same-session");
            await manager.AddMessageAsync(alice, Message("Alice 的短期消息"));
            await manager.AddMessageAsync(bob, Message("Bob 的短期消息"));
            await manager.AddMemoryAsync(alice, new MemoryEntry
            {
                Content = "Alice 偏好 PostgreSQL。",
                Type = "UserPreference"
            });
            await manager.AddMemoryAsync(bob, new MemoryEntry
            {
                Content = "Bob 偏好 Redis。",
                Type = "UserPreference"
            });

            var aliceHistory = await manager.GetHistoryAsync(alice);
            var bobHistory = await manager.GetHistoryAsync(bob);
            var aliceMemories = await manager.SearchAsync(alice, "技术偏好", 10);
            var bobMemories = await manager.SearchAsync(bob, "技术偏好", 10);

            Assert.Single(aliceHistory);
            Assert.Contains("Alice", aliceHistory[0].Content);
            Assert.Single(bobHistory);
            Assert.Contains("Bob", bobHistory[0].Content);
            Assert.Single(aliceMemories);
            Assert.Contains("Alice", aliceMemories[0].Content);
            Assert.Single(bobMemories);
            Assert.Contains("Bob", bobMemories[0].Content);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LongTermAdmission_DeduplicatesAndRejectsSecrets()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var manager = new SqliteMemoryManager(
                new SqliteMemoryOptions { Enabled = true, DatabasePath = databasePath });
            var partition = Partition("tenant-1", "alice", "agent-1", "session-1");
            await manager.AddMemoryAsync(partition, new MemoryEntry
            {
                Content = "用户明确偏好深色主题。",
                Type = "UserPreference"
            });
            await manager.AddMemoryAsync(partition, new MemoryEntry
            {
                Id = "duplicate-with-another-id",
                Content = "用户明确偏好深色主题。",
                Type = "UserPreference"
            });

            var memories = await manager.SearchAsync(partition, "深色主题", 10);
            Assert.Single(memories);
            await Assert.ThrowsAsync<MemoryRejectedException>(() =>
                manager.AddMemoryAsync(partition, new MemoryEntry
                {
                    Content = "api_key=super-secret-value-123456"
                }));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExplicitMemoryId_CannotOverwriteAnotherUserPartition()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var manager = new SqliteMemoryManager(
                new SqliteMemoryOptions { Enabled = true, DatabasePath = databasePath });
            var alice = Partition("tenant-1", "alice", "agent-1", "session-1");
            var bob = Partition("tenant-1", "bob", "agent-1", "session-2");
            await manager.AddMemoryAsync(alice, new MemoryEntry
            {
                Id = "shared-external-id",
                Content = "Alice 的事实"
            });

            await Assert.ThrowsAsync<MemoryRejectedException>(() =>
                manager.AddMemoryAsync(bob, new MemoryEntry
                {
                    Id = "shared-external-id",
                    Content = "Bob 尝试覆盖"
                }));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Vec1Extension_LoadsAndPerformsCosineSearchOnAppleSilicon()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return;

        var databasePath = CreateDatabasePath();
        try
        {
            var sqliteOptions = new SqliteMemoryOptions
            {
                Enabled = true,
                DatabasePath = databasePath,
                Vector = new SqliteVectorOptions
                {
                    Enabled = true,
                    IndexMode = "flat",
                    Distance = "cos",
                    FallbackToManagedSearch = false
                }
            };
            var manager = new SqliteMemoryManager(
                sqliteOptions,
                VectorOptions(),
                new TestEmbeddingClient());
            await manager.AddMemoryAsync(new MemoryEntry
            {
                Id = "postgres",
                Content = "用户偏好 PostgreSQL 数据库。"
            });
            await manager.AddMemoryAsync(new MemoryEntry
            {
                Id = "theme",
                Content = "用户偏好深色界面主题。"
            });

            var results = await manager.SearchAsync("数据库选型", 1);

            var result = Assert.Single(results);
            Assert.Equal("postgres", result.Id);
            var extensionPath = Path.Combine(
                AppContext.BaseDirectory,
                "runtimes", "osx-arm64", "native", "vec1.dylib");
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            connection.LoadExtension(extensionPath);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT vec1_info(), COUNT(*) FROM memory_vectors;";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Contains("version 0.7", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task MemoryAndEmbeddings_SurviveManagerRestart()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var sqlite = new SqliteMemoryOptions
            {
                Enabled = true,
                DatabasePath = databasePath
            };
            var embeddingOptions = VectorOptions();
            var first = new SqliteMemoryManager(sqlite, embeddingOptions, new TestEmbeddingClient());
            await first.AddMessageAsync("session-1", Message("苹果项目的需求"));
            await first.AddMessageAsync("session-1", Message("苹果项目的方案"));
            await first.AddMessageAsync("session-1", Message("最近一条消息"));
            await first.CompressAsync("session-1", new MediumTermMemoryOptions
            {
                RetainRecentMessages = 1,
                CompressionThresholdMessages = 2
            });
            await first.AddMemoryAsync(new MemoryEntry
            {
                Id = "database-preference",
                Content = "用户偏好 PostgreSQL 作为主要数据库。",
                Type = "UserPreference"
            });

            var second = new SqliteMemoryManager(sqlite, embeddingOptions, new TestEmbeddingClient());
            var history = await second.GetHistoryAsync("session-1");
            var summaries = await second.GetMediumTermMemoriesAsync("session-1");
            var longTerm = await second.SearchAsync("数据库技术选型", 1);

            Assert.Single(history);
            Assert.Equal("最近一条消息", history[0].Content);
            var summary = Assert.Single(summaries);
            Assert.Contains("苹果项目", summary.Content);
            Assert.Equal(2, summary.Embedding!.Count);
            var memory = Assert.Single(longTerm);
            Assert.Equal("database-preference", memory.Id);
            Assert.Equal(2, memory.Embedding!.Count);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task BuildContext_UsesPersistedSemanticMediumTermRanking()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var manager = new SqliteMemoryManager(
                new SqliteMemoryOptions { Enabled = true, DatabasePath = databasePath },
                VectorOptions(),
                new TestEmbeddingClient());
            var compression = new MediumTermMemoryOptions
            {
                RetainRecentMessages = 1,
                CompressionThresholdMessages = 2
            };
            await AddMessagesAsync(manager, "session-1", "苹果需求", "苹果方案", "保留消息一");
            await manager.CompressAsync("session-1", compression);
            await AddMessagesAsync(manager, "session-1", "篮球安排", "篮球名单", "保留消息二");
            await manager.CompressAsync("session-1", compression);

            var context = await manager.BuildContextAsync(
                "session-1",
                "水果项目",
                new MemoryOptions
                {
                    ShortTerm = { Enabled = false },
                    MediumTerm = { MaxSummaries = 1, CompressionThresholdMessages = 100 },
                    LongTerm = { Enabled = false }
                });

            var summary = Assert.Single(context.MediumTermMemories);
            Assert.Contains("苹果", summary.Content);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeleteSession_RemovesShortAndMediumButKeepsLongTerm()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var manager = new SqliteMemoryManager(
                new SqliteMemoryOptions { Enabled = true, DatabasePath = databasePath });
            await AddMessagesAsync(manager, "session-1", "旧消息一", "旧消息二");
            await manager.CompressAsync("session-1", new MediumTermMemoryOptions { RetainRecentMessages = 1 });
            await manager.AddMemoryAsync(new MemoryEntry { Content = "跨会话长期事实" });

            await manager.DeleteSessionAsync("session-1");

            Assert.Empty(await manager.GetHistoryAsync("session-1"));
            Assert.Empty(await manager.GetMediumTermMemoriesAsync("session-1"));
            Assert.Single(await manager.SearchAsync("跨会话长期事实"));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static MemoryEmbeddingOptions VectorOptions() => new()
    {
        Enabled = true,
        Dimensions = 2,
        MinimumSimilarity = 0.5,
        FallbackToLexicalSearch = false
    };

    private static MemoryPartition Partition(
        string tenantId,
        string userId,
        string agentId,
        string sessionId) => new()
        {
            TenantId = tenantId,
            UserId = userId,
            AgentId = agentId,
            WorkspaceId = "workspace-1",
            SessionId = sessionId
        };

    private static ChatMessage Message(string content) => new()
    {
        Role = "user",
        Content = content
    };

    private static async Task AddMessagesAsync(
        IMemoryManager manager,
        string sessionId,
        params string[] messages)
    {
        foreach (var message in messages)
            await manager.AddMessageAsync(sessionId, Message(message));
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "goldfish-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "memory.db");
    }

    private static void DeleteDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        var directory = Path.GetDirectoryName(databasePath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class TestEmbeddingClient : IMemoryEmbeddingClient
    {
        public Task<IReadOnlyList<float>> GenerateAsync(
            string text,
            MemoryEmbeddingInputType inputType,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<float> embedding = text.Contains("苹果", StringComparison.Ordinal)
                || text.Contains("水果", StringComparison.Ordinal)
                || text.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                || text.Contains("数据库", StringComparison.Ordinal)
                    ? new[] { 1f, 0f }
                    : new[] { 0f, 1f };
            return Task.FromResult(embedding);
        }
    }

    private sealed class ConstantEmbeddingClient : IMemoryEmbeddingClient
    {
        public Task<IReadOnlyList<float>> GenerateAsync(
            string text,
            MemoryEmbeddingInputType inputType,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float>>(new[] { 1f, 0f });
    }
}
