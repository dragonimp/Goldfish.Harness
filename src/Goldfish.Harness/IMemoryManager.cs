namespace Goldfish.Harness;

/// <summary>
/// 记忆层级。短期保留当前会话的近因上下文，中期保留会话摘要，长期保留跨会话事实和偏好。
/// </summary>
public enum MemoryScope
{
    ShortTerm,
    MediumTerm,
    LongTerm
}

public enum MemorySensitivity
{
    Public,
    Internal,
    Sensitive,
    Secret
}

public enum UserProfileScope
{
    Global,
    Agent
}

/// <summary>
/// Hard isolation boundary applied before semantic memory retrieval.
/// Empty identifiers represent the legacy single-user partition only.
/// </summary>
public sealed record MemoryPartition
{
    public string TenantId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string AgentId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;

    public static MemoryPartition Legacy(string sessionId) => new() { SessionId = sessionId };
}

/// <summary>
/// 记忆条目
/// </summary>
public class MemoryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Type { get; set; } = "General"; // General/UserPreference/Fact
    public string? Category { get; set; }
    public MemoryScope Scope { get; set; } = MemoryScope.LongTerm;
    public string? SessionId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string? SourceSessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public double Importance { get; set; } = 0.5;
    public double Confidence { get; set; } = 1.0;
    public DateTime? ExpiresAt { get; set; }
    public MemorySensitivity Sensitivity { get; set; } = MemorySensitivity.Internal;
    public string? ContentHash { get; set; }
    public IList<float>? Embedding { get; set; } // 向量嵌入
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public sealed class MemoryOptions
{
    public MemoryLayerOptions ShortTerm { get; set; } = new()
    {
        Enabled = true,
        MaxMessages = 12,
        MaxAge = TimeSpan.FromHours(6),
        IncludeRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "user", "assistant" }
    };

    public MediumTermMemoryOptions MediumTerm { get; set; } = new();

    public LongTermMemoryOptions LongTerm { get; set; } = new();

    public MemoryEmbeddingOptions Embedding { get; set; } = new();

    public SqliteMemoryOptions Sqlite { get; set; } = new();

    public MemoryAdmissionOptions Admission { get; set; } = new();

    public UserProfileMemoryOptions UserProfile { get; set; } = new();

    public static MemoryOptions Default => new();
}

public sealed class UserProfileMemoryOptions
{
    public bool Enabled { get; set; } = true;
    public UserProfileScope Scope { get; set; } = UserProfileScope.Global;
    public bool AutoExtract { get; set; } = true;
    public int MaxMemories { get; set; } = 8;
    public double MinimumConfidence { get; set; } = 0.75;
}

public class MemoryLayerOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxMessages { get; set; } = 12;
    public TimeSpan? MaxAge { get; set; }
    public ISet<string> IncludeRoles { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "user",
        "assistant"
    };
}

public sealed class MediumTermMemoryOptions
{
    public bool Enabled { get; set; } = true;
    public int CompressionThresholdMessages { get; set; } = 24;
    public int RetainRecentMessages { get; set; } = 8;
    public int MaxSummaries { get; set; } = 3;
    public int MaxSummaryChars { get; set; } = 2000;
    public ISet<string> IncludeRoles { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "user",
        "assistant",
        "tool"
    };
}

public sealed class LongTermMemoryOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxMemories { get; set; } = 5;
    public double MinimumImportance { get; set; } = 0;
    public double MinimumConfidence { get; set; } = 0;
    public bool ExcludeExpired { get; set; } = true;
    public ISet<string> Types { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class MemoryAdmissionOptions
{
    public bool RejectDetectedSecrets { get; set; } = true;
    public bool RejectSecretSensitivity { get; set; } = true;
    public bool DeduplicateByContent { get; set; } = true;
    public int MaxContentChars { get; set; } = 8000;
}

public sealed class MemoryRejectedException(string message) : InvalidOperationException(message);

public sealed class MemoryContext
{
    public IList<ChatMessage> ShortTermMessages { get; init; } = new List<ChatMessage>();
    public IList<MemoryEntry> MediumTermMemories { get; init; } = new List<MemoryEntry>();
    public IList<MemoryEntry> LongTermMemories { get; init; } = new List<MemoryEntry>();

    public static MemoryContext FromHistory(IList<ChatMessage> history)
        => new() { ShortTermMessages = history };
}

/// <summary>
/// 记忆管理器 — 管理智能体的短期、中期和长期记忆
/// </summary>
public interface IMemoryManager
{
    /// <summary>
    /// 添加消息到会话历史（短期记忆）
    /// </summary>
    Task AddMessageAsync(string sessionId, ChatMessage message);

    Task AddMessageAsync(MemoryPartition partition, ChatMessage message)
        => AddMessageAsync(partition.SessionId, message);

    /// <summary>
    /// 获取会话历史
    /// </summary>
    Task<IList<ChatMessage>> GetHistoryAsync(string sessionId);

    Task<IList<ChatMessage>> GetHistoryAsync(MemoryPartition partition)
        => GetHistoryAsync(partition.SessionId);

    /// <summary>
    /// 基于配置构建本轮应注入模型上下文的分层记忆。
    /// </summary>
    Task<MemoryContext> BuildContextAsync(string sessionId, string query, MemoryOptions? options = null);

    Task<MemoryContext> BuildContextAsync(
        MemoryPartition partition,
        string query,
        MemoryOptions? options = null)
        => BuildContextAsync(partition.SessionId, query, options);

    /// <summary>
    /// 添加长期记忆
    /// </summary>
    Task AddMemoryAsync(MemoryEntry entry);

    Task AddMemoryAsync(MemoryPartition partition, MemoryEntry entry)
    {
        entry.TenantId = partition.TenantId;
        entry.UserId = partition.UserId;
        entry.AgentId = partition.AgentId;
        entry.WorkspaceId = partition.WorkspaceId;
        entry.SourceSessionId ??= partition.SessionId;
        return AddMemoryAsync(entry);
    }

    /// <summary>
    /// 搜索长期记忆（基于语义相似度）
    /// </summary>
    Task<IList<MemoryEntry>> SearchAsync(string query, int limit = 5);

    Task<IList<MemoryEntry>> SearchAsync(MemoryPartition partition, string query, int limit = 5)
        => SearchAsync(query, limit);

    /// <summary>
    /// 获取会话级中期摘要。
    /// </summary>
    Task<IList<MemoryEntry>> GetMediumTermMemoriesAsync(string sessionId, int limit = 3);

    Task<IList<MemoryEntry>> GetMediumTermMemoriesAsync(MemoryPartition partition, int limit = 3)
        => GetMediumTermMemoriesAsync(partition.SessionId, limit);

    /// <summary>
    /// 删除会话
    /// </summary>
    Task DeleteSessionAsync(string sessionId);

    Task DeleteSessionAsync(MemoryPartition partition)
        => DeleteSessionAsync(partition.SessionId);

    /// <summary>
    /// 压缩会话历史（减少 Token 使用）
    /// </summary>
    Task<IList<ChatMessage>> CompressAsync(string sessionId);

    /// <summary>
    /// 按中期记忆配置压缩会话历史。
    /// </summary>
    Task<IList<ChatMessage>> CompressAsync(string sessionId, MediumTermMemoryOptions options);

    Task<IList<ChatMessage>> CompressAsync(MemoryPartition partition, MediumTermMemoryOptions options)
        => CompressAsync(partition.SessionId, options);
}

/// <summary>
/// 记忆管理器内存实现（基于内存存储）
/// </summary>
public class InMemoryMemoryManager : IMemoryManager
{
    private readonly Dictionary<string, List<ChatMessage>> _sessions = new();
    private readonly Dictionary<string, List<MemoryEntry>> _mediumTermMemories = new();
    private readonly List<MemoryEntry> _longTermMemories = new();
    private readonly object _lock = new();
    private readonly IMemoryEmbeddingClient? _embeddingClient;
    private readonly MemoryEmbeddingOptions _embeddingOptions;

    public InMemoryMemoryManager(
        IMemoryEmbeddingClient? embeddingClient = null,
        MemoryEmbeddingOptions? embeddingOptions = null)
    {
        _embeddingClient = embeddingClient;
        _embeddingOptions = embeddingOptions ?? new MemoryEmbeddingOptions
        {
            Enabled = embeddingClient is not null
        };
    }

    public InMemoryMemoryManager(
        MemoryEmbeddingOptions embeddingOptions,
        HttpClient? httpClient = null)
        : this(
            embeddingOptions.Enabled
                ? new OpenAiCompatibleMemoryEmbeddingClient(embeddingOptions, httpClient)
                : null,
            embeddingOptions)
    {
    }

    public static InMemoryMemoryManager FromOptions(
        MemoryOptions options,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new InMemoryMemoryManager(options.Embedding, httpClient);
    }

    public async Task AddMessageAsync(string sessionId, ChatMessage message)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var messages))
            {
                messages = new List<ChatMessage>();
                _sessions[sessionId] = messages;
            }
            messages.Add(message);
        }
        await Task.CompletedTask;
    }

    public Task AddMessageAsync(MemoryPartition partition, ChatMessage message)
        => AddMessageAsync(PartitionSessionKey(partition), message);

    public async Task<IList<ChatMessage>> GetHistoryAsync(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var messages)
                ? messages.ToList().AsReadOnly()
                : new List<ChatMessage>();
        }
    }

    public Task<IList<ChatMessage>> GetHistoryAsync(MemoryPartition partition)
        => GetHistoryAsync(PartitionSessionKey(partition));

    public async Task<MemoryContext> BuildContextAsync(string sessionId, string query, MemoryOptions? options = null)
        => await BuildContextCoreAsync(sessionId, MemoryPartition.Legacy(sessionId), query, options);

    public Task<MemoryContext> BuildContextAsync(
        MemoryPartition partition,
        string query,
        MemoryOptions? options = null)
        => BuildContextCoreAsync(PartitionSessionKey(partition), partition, query, options);

    private async Task<MemoryContext> BuildContextCoreAsync(
        string sessionKey,
        MemoryPartition partition,
        string query,
        MemoryOptions? options)
    {
        options ??= MemoryOptions.Default;

        if (options.MediumTerm.Enabled)
        {
            await CompressIfNeededAsync(sessionKey, options.MediumTerm);
        }

        var queryEmbedding = await TryGenerateEmbeddingAsync(query, MemoryEmbeddingInputType.Query);
        IList<ChatMessage> shortTermMessages = new List<ChatMessage>();
        IList<MemoryEntry> mediumTermMemories = new List<MemoryEntry>();
        IList<MemoryEntry> longTermMemories = new List<MemoryEntry>();

        lock (_lock)
        {
            if (options.ShortTerm.Enabled && _sessions.TryGetValue(sessionKey, out var messages))
            {
                shortTermMessages = SelectShortTermMessages(messages, options.ShortTerm);
            }

            if (options.MediumTerm.Enabled && _mediumTermMemories.TryGetValue(sessionKey, out var summaries))
            {
                mediumTermMemories = RankMediumTermMemories(summaries, query, queryEmbedding)
                    .Take(Math.Max(0, options.MediumTerm.MaxSummaries))
                    .Select(Touch)
                    .ToList();
            }

            if (options.LongTerm.Enabled)
            {
                longTermMemories = SearchLongTermMemories(
                        query,
                        queryEmbedding,
                        options.LongTerm,
                        partition)
                    .Select(Touch)
                    .ToList();
            }
        }

        return new MemoryContext
        {
            ShortTermMessages = shortTermMessages,
            MediumTermMemories = mediumTermMemories,
            LongTermMemories = longTermMemories
        };
    }

    public async Task AddMemoryAsync(MemoryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString("N");

        if (entry.Embedding is not { Count: > 0 })
        {
            entry.Embedding = await TryGenerateEmbeddingAsync(
                entry.Content,
                MemoryEmbeddingInputType.Document);
        }

        lock (_lock)
        {
            entry.Scope = MemoryScope.LongTerm;
            entry.CreatedAt = entry.CreatedAt == default ? DateTime.UtcNow : entry.CreatedAt;
            entry.LastAccessedAt = entry.LastAccessedAt == default ? entry.CreatedAt : entry.LastAccessedAt;
            _longTermMemories.Add(entry);
        }
        await Task.CompletedTask;
    }

    public async Task<IList<MemoryEntry>> SearchAsync(string query, int limit = 5)
        => await SearchAsync(MemoryPartition.Legacy(string.Empty), query, limit);

    public async Task<IList<MemoryEntry>> SearchAsync(
        MemoryPartition partition,
        string query,
        int limit = 5)
    {
        var queryEmbedding = await TryGenerateEmbeddingAsync(query, MemoryEmbeddingInputType.Query);
        lock (_lock)
        {
            var options = new LongTermMemoryOptions { MaxMemories = Math.Max(0, limit) };
            var results = SearchLongTermMemories(query, queryEmbedding, options, partition)
                .Take(limit)
                .ToList();

            return results;
        }
    }

    public async Task<IList<MemoryEntry>> GetMediumTermMemoriesAsync(string sessionId, int limit = 3)
    {
        lock (_lock)
        {
            return _mediumTermMemories.TryGetValue(sessionId, out var memories)
                ? memories.OrderByDescending(m => m.CreatedAt).Take(Math.Max(0, limit)).ToList()
                : new List<MemoryEntry>();
        }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        lock (_lock)
        {
            _sessions.Remove(sessionId);
            _mediumTermMemories.Remove(sessionId);
        }
        await Task.CompletedTask;
    }

    public Task DeleteSessionAsync(MemoryPartition partition)
        => DeleteSessionAsync(PartitionSessionKey(partition));

    public async Task<IList<ChatMessage>> CompressAsync(string sessionId)
        => await CompressAsync(sessionId, MemoryOptions.Default.MediumTerm);

    public async Task<IList<ChatMessage>> CompressAsync(string sessionId, MediumTermMemoryOptions options)
    {
        MemoryEntry? summary = null;
        List<ChatMessage>? recent;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var messages)
                || messages.Count <= Math.Max(1, options.RetainRecentMessages))
            {
                return messages?.ToList() ?? new List<ChatMessage>();
            }

            var retainCount = Math.Max(1, options.RetainRecentMessages);
            recent = messages.Skip(Math.Max(0, messages.Count - retainCount)).ToList();
            var earlyMessages = messages.Take(messages.Count - retainCount)
                .Where(m => options.IncludeRoles.Count == 0 || options.IncludeRoles.Contains(m.Role))
                .ToList();

            if (earlyMessages.Any())
            {
                summary = BuildSummaryMemory(sessionId, earlyMessages, options.MaxSummaryChars);
            }

            _sessions[sessionId] = recent;
        }

        if (summary is not null)
        {
            summary.Embedding = await TryGenerateEmbeddingAsync(
                summary.Content,
                MemoryEmbeddingInputType.Document);
            lock (_lock)
            {
                if (!_mediumTermMemories.TryGetValue(sessionId, out var summaries))
                {
                    summaries = new List<MemoryEntry>();
                    _mediumTermMemories[sessionId] = summaries;
                }
                summaries.Add(summary);
            }
        }

        return recent.AsReadOnly();
    }

    public Task<IList<ChatMessage>> CompressAsync(
        MemoryPartition partition,
        MediumTermMemoryOptions options)
        => CompressAsync(PartitionSessionKey(partition), options);

    private async Task CompressIfNeededAsync(string sessionId, MediumTermMemoryOptions options)
    {
        var shouldCompress = false;
        lock (_lock)
        {
            shouldCompress = _sessions.TryGetValue(sessionId, out var messages)
                && messages.Count > Math.Max(options.RetainRecentMessages, options.CompressionThresholdMessages);
        }

        if (shouldCompress)
        {
            await CompressAsync(sessionId, options);
        }
    }

    private static IList<ChatMessage> SelectShortTermMessages(IList<ChatMessage> messages, MemoryLayerOptions options)
    {
        var cutoff = options.MaxAge.HasValue ? DateTime.UtcNow.Subtract(options.MaxAge.Value) : (DateTime?)null;
        return messages
            .Where(m => options.IncludeRoles.Count == 0 || options.IncludeRoles.Contains(m.Role))
            .Where(m => cutoff == null || m.CreatedAt >= cutoff.Value)
            .OrderBy(m => m.CreatedAt)
            .TakeLast(Math.Max(0, options.MaxMessages))
            .Select(CloneMessage)
            .ToList();
    }

    private IEnumerable<MemoryEntry> RankMediumTermMemories(
        IEnumerable<MemoryEntry> memories,
        string query,
        IList<float>? queryEmbedding)
    {
        var candidates = memories.ToList();
        if (queryEmbedding is { Count: > 0 })
        {
            var semantic = candidates
                .Where(memory => memory.Embedding is { Count: > 0 })
                .Select(memory => new
                {
                    Memory = memory,
                    Similarity = CosineSimilarity(queryEmbedding, memory.Embedding!)
                })
                .Where(item => item.Similarity >= _embeddingOptions.MinimumSimilarity)
                .OrderByDescending(item => item.Similarity)
                .ThenByDescending(item => item.Memory.CreatedAt)
                .Select(item => item.Memory)
                .ToList();

            if (semantic.Count > 0)
            {
                return semantic.Concat(candidates
                    .Except(semantic)
                    .OrderByDescending(memory => memory.CreatedAt));
            }
        }

        return candidates.OrderByDescending(memory => memory.CreatedAt);
    }

    private List<MemoryEntry> SearchLongTermMemories(
        string query,
        IList<float>? queryEmbedding,
        LongTermMemoryOptions options,
        MemoryPartition partition)
    {
        var candidates = _longTermMemories
            .Where(memory => SamePartition(memory, partition))
            .Where(m => m.Importance >= options.MinimumImportance)
            .Where(m => options.Types.Count == 0 || options.Types.Contains(m.Type))
            .ToList();

        if (queryEmbedding is { Count: > 0 })
        {
            var semantic = candidates
                .Where(memory => memory.Embedding is { Count: > 0 })
                .Select(memory => new
                {
                    Memory = memory,
                    Similarity = CosineSimilarity(queryEmbedding, memory.Embedding!)
                })
                .Where(item => item.Similarity >= _embeddingOptions.MinimumSimilarity)
                .OrderByDescending(item => item.Similarity)
                .ThenByDescending(item => item.Memory.Importance)
                .ThenByDescending(item => item.Memory.LastAccessedAt)
                .Select(item => item.Memory)
                .Take(Math.Max(0, options.MaxMemories))
                .ToList();

            if (semantic.Count > 0 || !_embeddingOptions.FallbackToLexicalSearch)
                return semantic;
        }

        return candidates
            .Where(m => MatchesQuery(m, query))
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.LastAccessedAt)
            .ThenByDescending(m => m.CreatedAt)
            .Take(Math.Max(0, options.MaxMemories))
            .ToList();
    }

    private static bool SamePartition(MemoryEntry memory, MemoryPartition partition)
        => string.Equals(memory.TenantId, partition.TenantId, StringComparison.Ordinal)
            && string.Equals(memory.UserId, partition.UserId, StringComparison.Ordinal)
            && string.Equals(memory.AgentId, partition.AgentId, StringComparison.Ordinal)
            && string.Equals(memory.WorkspaceId, partition.WorkspaceId, StringComparison.Ordinal);

    private static string PartitionSessionKey(MemoryPartition partition)
        => string.Join('\u001f',
            partition.TenantId,
            partition.UserId,
            partition.AgentId,
            partition.WorkspaceId,
            partition.SessionId);

    private async Task<IList<float>?> TryGenerateEmbeddingAsync(
        string text,
        MemoryEmbeddingInputType inputType)
    {
        if (_embeddingClient is null || !_embeddingOptions.Enabled || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var embedding = await _embeddingClient.GenerateAsync(text, inputType);
            return embedding.Count > 0 ? embedding.ToList() : null;
        }
        catch when (_embeddingOptions.FallbackToLexicalSearch)
        {
            return null;
        }
    }

    internal static double CosineSimilarity(IList<float> left, IList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
            return double.NegativeInfinity;

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        if (leftNorm == 0 || rightNorm == 0)
            return double.NegativeInfinity;
        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static bool MatchesQuery(MemoryEntry memory, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return memory.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
            || memory.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (memory.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || memory.Metadata.Values.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static MemoryEntry Touch(MemoryEntry memory)
    {
        memory.LastAccessedAt = DateTime.UtcNow;
        return memory;
    }

    private static ChatMessage CloneMessage(ChatMessage message)
        => new()
        {
            Role = message.Role,
            Content = message.Content,
            ToolCallId = message.ToolCallId,
            CreatedAt = message.CreatedAt
        };

    private static MemoryEntry BuildSummaryMemory(string sessionId, IList<ChatMessage> messages, int maxChars)
    {
        var startedAt = messages.Min(m => m.CreatedAt);
        var endedAt = messages.Max(m => m.CreatedAt);
        var lines = messages
            .Select(m => $"{m.CreatedAt:O} {m.Role}: {m.Content}")
            .ToList();
        var content = string.Join(Environment.NewLine, lines);
        if (maxChars > 0 && content.Length > maxChars)
        {
            content = content[..maxChars] + "...";
        }

        return new MemoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Scope = MemoryScope.MediumTerm,
            Type = "ConversationSummary",
            Category = "Session",
            SessionId = sessionId,
            Content = $"会话片段摘要 ({startedAt:O} - {endedAt:O}, {messages.Count} 条消息):\n{content}",
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            Importance = 0.6,
            Metadata =
            {
                ["startedAt"] = startedAt.ToString("O"),
                ["endedAt"] = endedAt.ToString("O"),
                ["messageCount"] = messages.Count.ToString()
            }
        };
    }
}
