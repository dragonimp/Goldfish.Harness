using System.Net;
using System.Text;
using System.Text.Json;
using Goldfish.Harness;
using Xunit;

namespace Goldfish.Harness.Tests;

public sealed class MemoryManagerTests
{
    [Fact]
    public async Task LongTermMemory_UsesSemanticSimilarityWithoutKeywordOverlap()
    {
        var embeddings = new DelegateEmbeddingClient((text, type) =>
        {
            if (type == MemoryEmbeddingInputType.Query) return [1f, 0f];
            return text.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                ? [0.98f, 0.02f]
                : [0f, 1f];
        });
        var manager = CreateVectorManager(embeddings);

        await manager.AddMemoryAsync(new MemoryEntry { Content = "用户偏好 PostgreSQL 作为主数据库。" });
        await manager.AddMemoryAsync(new MemoryEntry { Content = "用户经常使用 Redis 处理缓存。" });

        var context = await manager.BuildContextAsync(
            "session-1",
            "数据持久化方面他倾向用什么？",
            new MemoryOptions
            {
                ShortTerm = { Enabled = false },
                MediumTerm = { Enabled = false },
                LongTerm = { MaxMemories = 1 }
            });

        var memory = Assert.Single(context.LongTermMemories);
        Assert.Contains("PostgreSQL", memory.Content);
        Assert.Equal(2, memory.Embedding!.Count);
    }

    [Fact]
    public async Task MediumTermMemory_RanksOlderSummaryBySemanticSimilarity()
    {
        var embeddings = new DelegateEmbeddingClient((text, type) =>
        {
            if (type == MemoryEmbeddingInputType.Query) return [1f, 0f];
            return text.Contains("苹果", StringComparison.Ordinal)
                ? [1f, 0f]
                : [0f, 1f];
        });
        var manager = CreateVectorManager(embeddings);
        var compression = new MediumTermMemoryOptions
        {
            RetainRecentMessages = 1,
            CompressionThresholdMessages = 2,
            MaxSummaryChars = 2000
        };

        await AddMessagesAsync(manager, "session-1", "苹果项目需求", "苹果项目方案", "苹果项目结论");
        await manager.CompressAsync("session-1", compression);
        await AddMessagesAsync(manager, "session-1", "篮球活动安排", "篮球活动名单", "篮球活动结论");
        await manager.CompressAsync("session-1", compression);

        var context = await manager.BuildContextAsync(
            "session-1",
            "之前水果相关的项目是什么？",
            new MemoryOptions
            {
                ShortTerm = { Enabled = false },
                MediumTerm = { MaxSummaries = 1, CompressionThresholdMessages = 100 },
                LongTerm = { Enabled = false }
            });

        var summary = Assert.Single(context.MediumTermMemories);
        Assert.Contains("苹果项目", summary.Content);
        Assert.Equal(2, summary.Embedding!.Count);
    }

    [Fact]
    public async Task EmbeddingClient_SendsOpenAiCompatibleRequestAndInputType()
    {
        string? requestJson = null;
        string? inputType = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            inputType = request.Headers.GetValues("x-embedding-input-type").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"index\":0,\"embedding\":[0.1,0.2,0.3]}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        using var client = new OpenAiCompatibleMemoryEmbeddingClient(
            new MemoryEmbeddingOptions
            {
                Enabled = true,
                Endpoint = "http://localhost:18790/v1/embeddings",
                Model = "local-embedding",
                Dimensions = 3
            },
            httpClient);

        var result = await client.GenerateAsync(
            "待检索问题",
            MemoryEmbeddingInputType.Query,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.Equal("query", inputType);
        using var json = JsonDocument.Parse(requestJson!);
        Assert.Equal("local-embedding", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("待检索问题", json.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task EmbeddingFailure_FallsBackToLexicalSearch()
    {
        var embeddings = new DelegateEmbeddingClient((_, _) => throw new HttpRequestException("offline"));
        var manager = CreateVectorManager(embeddings, fallback: true);
        await manager.AddMemoryAsync(new MemoryEntry { Content = "用户偏好深色主题。" });

        var results = await manager.SearchAsync("深色主题", 1);

        Assert.Single(results);
    }

    private static InMemoryMemoryManager CreateVectorManager(
        IMemoryEmbeddingClient client,
        bool fallback = false)
        => new(client, new MemoryEmbeddingOptions
        {
            Enabled = true,
            Dimensions = 2,
            MinimumSimilarity = 0.5,
            FallbackToLexicalSearch = fallback
        });

    private static async Task AddMessagesAsync(
        IMemoryManager manager,
        string sessionId,
        params string[] messages)
    {
        foreach (var message in messages)
        {
            await manager.AddMessageAsync(sessionId, new ChatMessage
            {
                Role = "user",
                Content = message
            });
        }
    }

    private sealed class DelegateEmbeddingClient(
        Func<string, MemoryEmbeddingInputType, IReadOnlyList<float>> generate) : IMemoryEmbeddingClient
    {
        public Task<IReadOnlyList<float>> GenerateAsync(
            string text,
            MemoryEmbeddingInputType inputType,
            CancellationToken cancellationToken = default)
            => Task.FromResult(generate(text, inputType));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request);
    }
}
