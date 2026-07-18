using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Goldfish.Harness;

public sealed class SqliteMemoryOptions
{
    public bool Enabled { get; set; }
    public string DatabasePath { get; set; } = "~/.goldfish/memory.db";
    public bool EnableWriteAheadLogging { get; set; } = true;
    public int BusyTimeoutSeconds { get; set; } = 30;
    public SqliteVectorOptions Vector { get; set; } = new();
}

public sealed class SqliteVectorOptions
{
    public bool Enabled { get; set; }
    public string? ExtensionPath { get; set; }
    public string IndexMode { get; set; } = "flat";
    public string Distance { get; set; } = "cos";
    public int CandidateMultiplier { get; set; } = 4;
    public bool FallbackToManagedSearch { get; set; } = true;
}

/// <summary>
/// Durable SQLite implementation of the three-layer memory manager. Embeddings
/// are stored as little-endian float32 BLOBs and ranked in-process with cosine similarity.
/// </summary>
public sealed partial class SqliteMemoryManager : IMemoryManager
{
    private const string TimestampFormat = "O";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteMemoryOptions _sqliteOptions;
    private readonly MemoryEmbeddingOptions _embeddingOptions;
    private readonly IMemoryEmbeddingClient? _embeddingClient;
    private readonly MemoryAdmissionOptions _admissionOptions;
    private readonly string _connectionString;
    private readonly SqliteVectorOptions _vectorOptions;
    private readonly string? _vectorExtensionPath;
    private bool _vectorAvailable;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteMemoryManager(
        SqliteMemoryOptions sqliteOptions,
        MemoryEmbeddingOptions? embeddingOptions = null,
        IMemoryEmbeddingClient? embeddingClient = null,
        MemoryAdmissionOptions? admissionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(sqliteOptions);
        _sqliteOptions = sqliteOptions;
        _vectorOptions = sqliteOptions.Vector;
        _embeddingOptions = embeddingOptions ?? new MemoryEmbeddingOptions
        {
            Enabled = embeddingClient is not null
        };
        _embeddingClient = embeddingClient;
        _admissionOptions = admissionOptions ?? new MemoryAdmissionOptions();

        var databasePath = ResolveDatabasePath(sqliteOptions.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = Math.Max(1, sqliteOptions.BusyTimeoutSeconds)
        }.ToString();
        _vectorExtensionPath = ResolveVectorExtensionPath(_vectorOptions);
        _vectorAvailable = _vectorOptions.Enabled && _vectorExtensionPath is not null;
        if (_vectorOptions.Enabled && !_vectorAvailable && !_vectorOptions.FallbackToManagedSearch)
        {
            throw new FileNotFoundException(
                "SQLite Vec1 extension was enabled but could not be found.",
                _vectorOptions.ExtensionPath);
        }
        InitializeDatabase();
    }

    public SqliteMemoryManager(
        MemoryOptions options,
        HttpClient? httpClient = null)
        : this(
            options.Sqlite,
            options.Embedding,
            options.Embedding.Enabled
                ? new OpenAiCompatibleMemoryEmbeddingClient(options.Embedding, httpClient)
                : null,
            options.Admission)
    {
    }

    public static SqliteMemoryManager FromOptions(MemoryOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SqliteMemoryManager(options, httpClient);
    }

    public async Task AddMessageAsync(string sessionId, ChatMessage message)
        => await AddMessageAsync(MemoryPartition.Legacy(sessionId), message);

    public async Task AddMessageAsync(MemoryPartition partition, ChatMessage message)
    {
        ValidatePartition(partition, requireSession: true);
        ArgumentNullException.ThrowIfNull(message);
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_messages(
                    tenant_id, user_id, agent_id, workspace_id,
                    session_id, role, content, tool_call_id, created_at)
                VALUES (
                    $tenantId, $userId, $agentId, $workspaceId,
                    $sessionId, $role, $content, $toolCallId, $createdAt);
                """;
            AddPartitionParameters(command, partition);
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue("$toolCallId", (object?)message.ToolCallId ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", ToTimestamp(message.CreatedAt));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IList<ChatMessage>> GetHistoryAsync(string sessionId)
        => await GetHistoryAsync(MemoryPartition.Legacy(sessionId));

    public async Task<IList<ChatMessage>> GetHistoryAsync(MemoryPartition partition)
    {
        ValidatePartition(partition, requireSession: true);
        await using var connection = await OpenConnectionAsync();
        var stored = await LoadMessagesAsync(connection, partition);
        return stored.Select(item => item.Message).ToList();
    }

    public async Task<MemoryContext> BuildContextAsync(
        string sessionId,
        string query,
        MemoryOptions? options = null)
        => await BuildContextAsync(MemoryPartition.Legacy(sessionId), query, options);

    public async Task<MemoryContext> BuildContextAsync(
        MemoryPartition partition,
        string query,
        MemoryOptions? options = null)
    {
        ValidatePartition(partition, requireSession: true);
        options ??= MemoryOptions.Default;
        if (options.MediumTerm.Enabled)
            await CompressIfNeededAsync(partition, options.MediumTerm);

        var queryEmbedding = options.MediumTerm.Enabled || options.LongTerm.Enabled
            ? await TryGenerateEmbeddingAsync(query, MemoryEmbeddingInputType.Query)
            : null;
        await using var connection = await OpenConnectionAsync();

        IList<ChatMessage> shortTerm = [];
        if (options.ShortTerm.Enabled)
        {
            var history = (await LoadMessagesAsync(connection, partition))
                .Select(item => item.Message);
            var cutoff = options.ShortTerm.MaxAge.HasValue
                ? DateTime.UtcNow.Subtract(options.ShortTerm.MaxAge.Value)
                : (DateTime?)null;
            shortTerm = history
                .Where(message => options.ShortTerm.IncludeRoles.Count == 0
                    || options.ShortTerm.IncludeRoles.Contains(message.Role))
                .Where(message => cutoff is null || message.CreatedAt >= cutoff.Value)
                .OrderBy(message => message.CreatedAt)
                .TakeLast(Math.Max(0, options.ShortTerm.MaxMessages))
                .ToList();
        }

        IList<MemoryEntry> mediumTerm = [];
        if (options.MediumTerm.Enabled)
        {
            var limit = Math.Max(0, options.MediumTerm.MaxSummaries);
            var vectorMatches = await SearchVectorMemoriesAsync(
                connection,
                MemoryScope.MediumTerm,
                partition,
                queryEmbedding,
                limit);
            if (vectorMatches is { Count: > 0 })
            {
                mediumTerm = vectorMatches;
            }
            else
            {
                var summaries = await LoadMemoriesAsync(
                    connection,
                    MemoryScope.MediumTerm,
                    partition);
                mediumTerm = RankMediumTermMemories(summaries, queryEmbedding)
                    .Take(limit)
                    .ToList();
            }
        }

        IList<MemoryEntry> longTerm = [];
        if (options.LongTerm.Enabled)
        {
            var vectorMatches = await SearchVectorMemoriesAsync(
                connection,
                MemoryScope.LongTerm,
                partition with { SessionId = string.Empty },
                queryEmbedding,
                Math.Max(0, options.LongTerm.MaxMemories));
            if (vectorMatches is { Count: > 0 })
            {
                longTerm = RerankLongTermCandidates(
                    vectorMatches,
                    queryEmbedding,
                    options.LongTerm);
            }
            else
            {
                var memories = await LoadMemoriesAsync(
                    connection,
                    MemoryScope.LongTerm,
                    partition with { SessionId = string.Empty });
                longTerm = RankLongTermMemories(memories, query, queryEmbedding, options.LongTerm);
            }

            if (options.UserProfile.Enabled && options.UserProfile.MaxMemories > 0)
            {
                var profilePartition = UserProfileMemory.ProfilePartition(
                    partition,
                    options.UserProfile.Scope);
                if (profilePartition.AgentId != partition.AgentId
                    || profilePartition.WorkspaceId != partition.WorkspaceId)
                {
                    // A user portrait is deliberately small and already protected by
                    // exact tenant/user/scope partition filters. Load the bounded set
                    // directly so ANN similarity cannot hide a stable profile field.
                    var storedProfiles = await LoadMemoriesAsync(
                        connection,
                        MemoryScope.LongTerm,
                        profilePartition);
                    IList<MemoryEntry> profiles = storedProfiles
                        .Where(memory => memory.Confidence >= options.UserProfile.MinimumConfidence)
                        .OrderByDescending(memory => memory.Importance)
                        .ThenByDescending(memory => memory.LastAccessedAt)
                        .Take(options.UserProfile.MaxMemories)
                        .ToList();

                    longTerm = longTerm
                        .Concat(profiles)
                        .GroupBy(memory => memory.Id, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToList();
                }
            }
        }

        var touched = mediumTerm.Concat(longTerm).ToList();
        if (touched.Count > 0)
            await TouchAsync(touched);

        return new MemoryContext
        {
            ShortTermMessages = shortTerm,
            MediumTermMemories = mediumTerm,
            LongTermMemories = longTerm
        };
    }

    public async Task AddMemoryAsync(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ApplyAdmissionPolicy(entry);
        if (string.IsNullOrWhiteSpace(entry.Id))
            entry.Id = entry.ContentHash![..32];
        if (entry.Embedding is not { Count: > 0 })
        {
            entry.Embedding = await TryGenerateEmbeddingAsync(
                entry.Content,
                MemoryEmbeddingInputType.Document);
        }

        entry.Scope = MemoryScope.LongTerm;
        entry.CreatedAt = entry.CreatedAt == default ? DateTime.UtcNow : entry.CreatedAt;
        entry.LastAccessedAt = entry.LastAccessedAt == default ? entry.CreatedAt : entry.LastAccessedAt;
        await UpsertMemoryAsync(entry);
    }

    public Task AddMemoryAsync(MemoryPartition partition, MemoryEntry entry)
    {
        ValidatePartition(partition, requireSession: false);
        entry.TenantId = partition.TenantId;
        entry.UserId = partition.UserId;
        entry.AgentId = partition.AgentId;
        entry.WorkspaceId = partition.WorkspaceId;
        entry.SourceSessionId ??= partition.SessionId;
        return AddMemoryAsync(entry);
    }

    public async Task<IList<MemoryEntry>> SearchAsync(string query, int limit = 5)
        => await SearchAsync(MemoryPartition.Legacy(string.Empty), query, limit);

    public async Task<IList<MemoryEntry>> SearchAsync(
        MemoryPartition partition,
        string query,
        int limit = 5)
    {
        ValidatePartition(partition, requireSession: false);
        var queryEmbedding = await TryGenerateEmbeddingAsync(query, MemoryEmbeddingInputType.Query);
        await using var connection = await OpenConnectionAsync();
        var vectorMatches = await SearchVectorMemoriesAsync(
            connection,
            MemoryScope.LongTerm,
            partition with { SessionId = string.Empty },
            queryEmbedding,
            Math.Max(0, limit));
        if (vectorMatches is { Count: > 0 })
        {
            var reranked = RerankLongTermCandidates(
                vectorMatches,
                queryEmbedding,
                new LongTermMemoryOptions { MaxMemories = Math.Max(0, limit) });
            await TouchAsync(reranked);
            return reranked;
        }
        var memories = await LoadMemoriesAsync(
            connection,
            MemoryScope.LongTerm,
            partition with { SessionId = string.Empty });
        var results = RankLongTermMemories(
            memories,
            query,
            queryEmbedding,
            new LongTermMemoryOptions { MaxMemories = Math.Max(0, limit) });
        if (results.Count > 0)
            await TouchAsync(results);
        return results;
    }

    public async Task<IList<MemoryEntry>> GetMediumTermMemoriesAsync(string sessionId, int limit = 3)
        => await GetMediumTermMemoriesAsync(MemoryPartition.Legacy(sessionId), limit);

    public async Task<IList<MemoryEntry>> GetMediumTermMemoriesAsync(
        MemoryPartition partition,
        int limit = 3)
    {
        ValidatePartition(partition, requireSession: true);
        await using var connection = await OpenConnectionAsync();
        var memories = await LoadMemoriesAsync(connection, MemoryScope.MediumTerm, partition);
        return memories
            .OrderByDescending(memory => memory.CreatedAt)
            .Take(Math.Max(0, limit))
            .ToList();
    }

    public async Task DeleteSessionAsync(string sessionId)
        => await DeleteSessionAsync(MemoryPartition.Legacy(sessionId));

    public async Task DeleteSessionAsync(MemoryPartition partition)
    {
        ValidatePartition(partition, requireSession: true);
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (_vectorAvailable)
            {
                await DeleteSessionVectorsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    partition);
            }
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM memory_messages
                WHERE tenant_id = $tenantId AND user_id = $userId
                  AND agent_id = $agentId AND workspace_id = $workspaceId
                  AND session_id = $sessionId;
                DELETE FROM memory_entries
                WHERE scope = $scope
                  AND tenant_id = $tenantId AND user_id = $userId
                  AND agent_id = $agentId AND workspace_id = $workspaceId
                  AND session_id = $sessionId;
                """;
            AddPartitionParameters(command, partition);
            command.Parameters.AddWithValue("$scope", (int)MemoryScope.MediumTerm);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IList<ChatMessage>> CompressAsync(string sessionId)
        => CompressAsync(MemoryPartition.Legacy(sessionId), MemoryOptions.Default.MediumTerm);

    public async Task<IList<ChatMessage>> CompressAsync(
        string sessionId,
        MediumTermMemoryOptions options)
        => await CompressAsync(MemoryPartition.Legacy(sessionId), options);

    public async Task<IList<ChatMessage>> CompressAsync(
        MemoryPartition partition,
        MediumTermMemoryOptions options)
    {
        ValidatePartition(partition, requireSession: true);
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var stored = await LoadMessagesAsync(connection, partition);
            var retainCount = Math.Max(1, options.RetainRecentMessages);
            if (stored.Count <= retainCount)
                return stored.Select(item => item.Message).ToList();

            var early = stored.Take(stored.Count - retainCount).ToList();
            var recent = stored.Skip(stored.Count - retainCount).ToList();
            var summaryMessages = early
                .Select(item => item.Message)
                .Where(message => options.IncludeRoles.Count == 0
                    || options.IncludeRoles.Contains(message.Role))
                .ToList();
            MemoryEntry? summary = summaryMessages.Count > 0
                ? BuildSummaryMemory(partition, summaryMessages, options.MaxSummaryChars)
                : null;
            if (summary is not null)
            {
                summary.Embedding = await TryGenerateEmbeddingAsync(
                    summary.Content,
                    MemoryEmbeddingInputType.Document);
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = """
                    DELETE FROM memory_messages
                    WHERE id <= $lastId
                      AND tenant_id = $tenantId AND user_id = $userId
                      AND agent_id = $agentId AND workspace_id = $workspaceId
                      AND session_id = $sessionId;
                    """;
                delete.Parameters.AddWithValue("$lastId", early[^1].Id);
                AddPartitionParameters(delete, partition);
                await delete.ExecuteNonQueryAsync();
            }
            if (summary is not null)
                await UpsertMemoryAsync(connection, transaction, summary);
            await transaction.CommitAsync();
            return recent.Select(item => item.Message).ToList();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task CompressIfNeededAsync(MemoryPartition partition, MediumTermMemoryOptions options)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM memory_messages
            WHERE tenant_id = $tenantId AND user_id = $userId
              AND agent_id = $agentId AND workspace_id = $workspaceId
              AND session_id = $sessionId;
            """;
        AddPartitionParameters(command, partition);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        if (count > Math.Max(options.RetainRecentMessages, options.CompressionThresholdMessages))
            await CompressAsync(partition, options);
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        LoadVectorExtension(connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA busy_timeout = {Math.Max(1, _sqliteOptions.BusyTimeoutSeconds) * 1000};
            PRAGMA journal_mode = {(_sqliteOptions.EnableWriteAheadLogging ? "WAL" : "DELETE")};
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS memory_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                workspace_id TEXT NOT NULL DEFAULT '',
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                tool_call_id TEXT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_memory_messages_session_created
                ON memory_messages(session_id, created_at, id);

            CREATE TABLE IF NOT EXISTS memory_entries (
                id TEXT PRIMARY KEY,
                scope INTEGER NOT NULL,
                tenant_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                workspace_id TEXT NOT NULL DEFAULT '',
                session_id TEXT NULL,
                source_session_id TEXT NULL,
                type TEXT NOT NULL,
                category TEXT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                importance REAL NOT NULL,
                confidence REAL NOT NULL DEFAULT 1.0,
                expires_at TEXT NULL,
                sensitivity INTEGER NOT NULL DEFAULT 1,
                content_hash TEXT NULL,
                embedding BLOB NULL,
                metadata_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_memory_entries_scope_session_created
                ON memory_entries(scope, session_id, created_at);
            CREATE INDEX IF NOT EXISTS ix_memory_entries_scope_importance
                ON memory_entries(scope, importance DESC, last_accessed_at DESC);
            """;
        command.ExecuteNonQuery();
        MigrateSchema(connection);

        if (_vectorAvailable)
            InitializeVectorSchema(connection);
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        LoadVectorExtension(connection);
        return connection;
    }

    private void LoadVectorExtension(SqliteConnection connection)
    {
        if (!_vectorAvailable || _vectorExtensionPath is null) return;
        try
        {
            connection.LoadExtension(_vectorExtensionPath);
        }
        catch when (_vectorOptions.FallbackToManagedSearch)
        {
            _vectorAvailable = false;
        }
    }

    private static void MigrateSchema(SqliteConnection connection)
    {
        EnsureColumns(connection, "memory_messages", new Dictionary<string, string>
        {
            ["tenant_id"] = "TEXT NOT NULL DEFAULT ''",
            ["user_id"] = "TEXT NOT NULL DEFAULT ''",
            ["agent_id"] = "TEXT NOT NULL DEFAULT ''",
            ["workspace_id"] = "TEXT NOT NULL DEFAULT ''"
        });
        EnsureColumns(connection, "memory_entries", new Dictionary<string, string>
        {
            ["tenant_id"] = "TEXT NOT NULL DEFAULT ''",
            ["user_id"] = "TEXT NOT NULL DEFAULT ''",
            ["agent_id"] = "TEXT NOT NULL DEFAULT ''",
            ["workspace_id"] = "TEXT NOT NULL DEFAULT ''",
            ["source_session_id"] = "TEXT NULL",
            ["confidence"] = "REAL NOT NULL DEFAULT 1.0",
            ["expires_at"] = "TEXT NULL",
            ["sensitivity"] = "INTEGER NOT NULL DEFAULT 1",
            ["content_hash"] = "TEXT NULL"
        });
        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_memory_messages_partition_session
                ON memory_messages(tenant_id, user_id, agent_id, workspace_id, session_id, created_at);
            CREATE INDEX IF NOT EXISTS ix_memory_entries_partition_scope
                ON memory_entries(tenant_id, user_id, agent_id, workspace_id, scope, session_id);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_entries_partition_content
                ON memory_entries(tenant_id, user_id, agent_id, workspace_id, content_hash)
                WHERE content_hash IS NOT NULL;
            """;
        indexes.ExecuteNonQuery();
    }

    private static void EnsureColumns(
        SqliteConnection connection,
        string table,
        IReadOnlyDictionary<string, string> columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(1));
        }
        foreach (var column in columns.Where(column => !existing.Contains(column.Key)))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column.Key} {column.Value};";
            alter.ExecuteNonQuery();
        }
    }

    private void InitializeVectorSchema(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'memory_vectors';";
        var isNew = Convert.ToInt32(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
        if (!isNew && !VectorSchemaHasPartitionKeys(connection))
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE memory_vectors;";
            drop.ExecuteNonQuery();
            isNew = true;
        }

        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_vector_map (
                vector_rowid INTEGER PRIMARY KEY AUTOINCREMENT,
                memory_id TEXT NOT NULL UNIQUE
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS memory_vectors
                USING vec1(
                    embedding, scope, tenant_key, user_key,
                    agent_key, workspace_key, session_key
                );
            """;
        schema.ExecuteNonQuery();

        if (isNew)
        {
            var indexMode = NormalizeVectorSetting(_vectorOptions.IndexMode, "flat", "none");
            var distance = NormalizeVectorSetting(_vectorOptions.Distance, "cos", "l2");
            using var configure = connection.CreateCommand();
            configure.CommandText = "INSERT INTO memory_vectors(cmd, arg) VALUES('rebuild', $config);";
            configure.Parameters.AddWithValue(
                "$config",
                JsonSerializer.Serialize(new { index = indexMode, distance }));
            configure.ExecuteNonQuery();
        }

        using var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT id, scope, tenant_id, user_id, agent_id, workspace_id,
                   session_id, embedding
            FROM memory_entries
            WHERE embedding IS NOT NULL;
            """;
        using var reader = existing.ExecuteReader();
        var entries = new List<MemoryEntry>();
        while (reader.Read())
        {
            entries.Add(new MemoryEntry
            {
                Id = reader.GetString(0),
                Scope = (MemoryScope)reader.GetInt32(1),
                TenantId = reader.GetString(2),
                UserId = reader.GetString(3),
                AgentId = reader.GetString(4),
                WorkspaceId = reader.GetString(5),
                SessionId = reader.IsDBNull(6) ? null : reader.GetString(6),
                Embedding = DeserializeEmbedding((byte[])reader[7])
            });
        }
        reader.Close();
        foreach (var entry in entries)
            SyncVectorAsync(connection, null, entry).GetAwaiter().GetResult();
    }

    private static bool VectorSchemaHasPartitionKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(memory_vectors);";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.Contains("tenant_key")
            && columns.Contains("user_key")
            && columns.Contains("agent_key")
            && columns.Contains("workspace_key")
            && columns.Contains("session_key");
    }

    private async Task SyncVectorAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryEntry entry)
    {
        if (!_vectorAvailable) return;

        await using var map = connection.CreateCommand();
        map.Transaction = transaction;
        map.CommandText = """
            INSERT INTO memory_vector_map(memory_id) VALUES($memoryId)
            ON CONFLICT(memory_id) DO NOTHING;
            SELECT vector_rowid FROM memory_vector_map WHERE memory_id = $memoryId;
            """;
        map.Parameters.AddWithValue("$memoryId", entry.Id);
        var vectorRowId = Convert.ToInt64(await map.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM memory_vectors WHERE rowid = $rowid;";
        delete.Parameters.AddWithValue("$rowid", vectorRowId);
        await delete.ExecuteNonQueryAsync();

        if (entry.Embedding is not { Count: > 0 }) return;
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO memory_vectors(
                rowid, embedding, scope, tenant_key, user_key,
                agent_key, workspace_key, session_key)
            VALUES(
                $rowid, $embedding, $scope, $tenantKey, $userKey,
                $agentKey, $workspaceKey, $sessionKey);
            """;
        insert.Parameters.AddWithValue("$rowid", vectorRowId);
        insert.Parameters.AddWithValue("$embedding", SerializeEmbedding(entry.Embedding));
        insert.Parameters.AddWithValue("$scope", (int)entry.Scope);
        insert.Parameters.AddWithValue("$tenantKey", StablePartitionKey(entry.TenantId));
        insert.Parameters.AddWithValue("$userKey", StablePartitionKey(entry.UserId));
        insert.Parameters.AddWithValue("$agentKey", StablePartitionKey(entry.AgentId));
        insert.Parameters.AddWithValue("$workspaceKey", StablePartitionKey(entry.WorkspaceId));
        insert.Parameters.AddWithValue("$sessionKey", StablePartitionKey(entry.SessionId ?? string.Empty));
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<List<MemoryEntry>?> SearchVectorMemoriesAsync(
        SqliteConnection connection,
        MemoryScope scope,
        MemoryPartition partition,
        IList<float>? queryEmbedding,
        int limit)
    {
        if (!_vectorAvailable || queryEmbedding is not { Count: > 0 } || limit <= 0)
            return null;

        var candidateCount = Math.Max(limit, limit * Math.Max(1, _vectorOptions.CandidateMultiplier));
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vm.memory_id, v.distance
            FROM memory_vectors($embedding, $parameters) AS v
            JOIN memory_vector_map AS vm ON vm.vector_rowid = v.rowid
            WHERE v.scope = $scope
              AND v.tenant_key = $tenantKey
              AND v.user_key = $userKey
              AND v.agent_key = $agentKey
              AND v.workspace_key = $workspaceKey
              AND v.session_key = $sessionKey
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$embedding", SerializeEmbedding(queryEmbedding));
        command.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(new { k = candidateCount }));
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$limit", candidateCount);
        AddVectorPartitionParameters(command, partition);

        var matches = new List<(string Id, double Similarity)>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var similarity = 1d - reader.GetDouble(1);
                if (similarity >= _embeddingOptions.MinimumSimilarity)
                    matches.Add((reader.GetString(0), similarity));
            }
        }
        catch when (_vectorOptions.FallbackToManagedSearch)
        {
            return null;
        }

        if (matches.Count == 0) return [];
        var all = await LoadMemoriesAsync(connection, scope, partition);
        var byId = all.ToDictionary(memory => memory.Id, StringComparer.Ordinal);
        return matches
            .Where(match => byId.ContainsKey(match.Id))
            .Select(match => byId[match.Id])
            .Take(limit)
            .ToList();
    }

    private static async Task DeleteSessionVectorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryPartition partition)
    {
        var rowIds = new List<long>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT vm.vector_rowid
                FROM memory_vector_map AS vm
                JOIN memory_entries AS me ON me.id = vm.memory_id
                WHERE me.scope = $scope
                  AND me.tenant_id = $tenantId AND me.user_id = $userId
                  AND me.agent_id = $agentId AND me.workspace_id = $workspaceId
                  AND me.session_id = $sessionId;
                """;
            select.Parameters.AddWithValue("$scope", (int)MemoryScope.MediumTerm);
            AddPartitionParameters(select, partition);
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rowIds.Add(reader.GetInt64(0));
        }

        foreach (var rowId in rowIds)
        {
            await using var deleteVector = connection.CreateCommand();
            deleteVector.Transaction = transaction;
            deleteVector.CommandText = "DELETE FROM memory_vectors WHERE rowid = $rowid;";
            deleteVector.Parameters.AddWithValue("$rowid", rowId);
            await deleteVector.ExecuteNonQueryAsync();
        }

        await using var deleteMap = connection.CreateCommand();
        deleteMap.Transaction = transaction;
        deleteMap.CommandText = """
            DELETE FROM memory_vector_map
            WHERE vector_rowid IN (
                SELECT vm.vector_rowid
                FROM memory_vector_map AS vm
                JOIN memory_entries AS me ON me.id = vm.memory_id
                WHERE me.scope = $scope
                  AND me.tenant_id = $tenantId AND me.user_id = $userId
                  AND me.agent_id = $agentId AND me.workspace_id = $workspaceId
                  AND me.session_id = $sessionId
            );
            """;
        deleteMap.Parameters.AddWithValue("$scope", (int)MemoryScope.MediumTerm);
        AddPartitionParameters(deleteMap, partition);
        await deleteMap.ExecuteNonQueryAsync();
    }

    private static string NormalizeVectorSetting(string value, string first, string second)
    {
        if (string.Equals(value, first, StringComparison.OrdinalIgnoreCase)) return first;
        if (string.Equals(value, second, StringComparison.OrdinalIgnoreCase)) return second;
        throw new ArgumentException($"Unsupported Vec1 setting '{value}'. Expected '{first}' or '{second}'.");
    }

    private static async Task<List<StoredMessage>> LoadMessagesAsync(
        SqliteConnection connection,
        MemoryPartition partition)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, role, content, tool_call_id, created_at
            FROM memory_messages
            WHERE tenant_id = $tenantId AND user_id = $userId
              AND agent_id = $agentId AND workspace_id = $workspaceId
              AND session_id = $sessionId
            ORDER BY created_at, id;
            """;
        AddPartitionParameters(command, partition);
        var messages = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            messages.Add(new StoredMessage(
                reader.GetInt64(0),
                new ChatMessage
                {
                    Role = reader.GetString(1),
                    Content = reader.GetString(2),
                    ToolCallId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAt = ParseTimestamp(reader.GetString(4))
                }));
        }
        return messages;
    }

    private static async Task<List<MemoryEntry>> LoadMemoriesAsync(
        SqliteConnection connection,
        MemoryScope scope,
        MemoryPartition partition)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, content, type, category, scope, session_id, created_at,
                   last_accessed_at, importance, embedding, metadata_json,
                   tenant_id, user_id, agent_id, workspace_id, source_session_id,
                   confidence, expires_at, sensitivity, content_hash
            FROM memory_entries
            WHERE scope = $scope
              AND tenant_id = $tenantId AND user_id = $userId
              AND agent_id = $agentId AND workspace_id = $workspaceId
              AND COALESCE(session_id, '') = $sessionId;
            """;
        command.Parameters.AddWithValue("$scope", (int)scope);
        AddPartitionParameters(command, partition);

        var memories = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            memories.Add(new MemoryEntry
            {
                Id = reader.GetString(0),
                Content = reader.GetString(1),
                Type = reader.GetString(2),
                Category = reader.IsDBNull(3) ? null : reader.GetString(3),
                Scope = (MemoryScope)reader.GetInt32(4),
                SessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = ParseTimestamp(reader.GetString(6)),
                LastAccessedAt = ParseTimestamp(reader.GetString(7)),
                Importance = reader.GetDouble(8),
                Embedding = reader.IsDBNull(9) ? null : DeserializeEmbedding((byte[])reader[9]),
                Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    reader.GetString(10),
                    JsonOptions) ?? new Dictionary<string, string>(),
                TenantId = reader.GetString(11),
                UserId = reader.GetString(12),
                AgentId = reader.GetString(13),
                WorkspaceId = reader.GetString(14),
                SourceSessionId = reader.IsDBNull(15) ? null : reader.GetString(15),
                Confidence = reader.GetDouble(16),
                ExpiresAt = reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
                Sensitivity = (MemorySensitivity)reader.GetInt32(18),
                ContentHash = reader.IsDBNull(19) ? null : reader.GetString(19)
            });
        }
        return memories;
    }

    private async Task UpsertMemoryAsync(MemoryEntry entry)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureMemoryIdOwnershipAsync(connection, entry);
            if (_admissionOptions.DeduplicateByContent && entry.ContentHash is not null)
            {
                await using var find = connection.CreateCommand();
                find.CommandText = """
                    SELECT id FROM memory_entries
                    WHERE tenant_id = $tenantId AND user_id = $userId
                      AND agent_id = $agentId AND workspace_id = $workspaceId
                      AND content_hash = $contentHash
                    LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$tenantId", entry.TenantId);
                find.Parameters.AddWithValue("$userId", entry.UserId);
                find.Parameters.AddWithValue("$agentId", entry.AgentId);
                find.Parameters.AddWithValue("$workspaceId", entry.WorkspaceId);
                find.Parameters.AddWithValue("$contentHash", entry.ContentHash);
                if (await find.ExecuteScalarAsync() is string existingId)
                    entry.Id = existingId;
            }
            await UpsertMemoryAsync(connection, null, entry);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task EnsureMemoryIdOwnershipAsync(
        SqliteConnection connection,
        MemoryEntry entry)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tenant_id, user_id, agent_id, workspace_id
            FROM memory_entries WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return;
        var sameOwner = string.Equals(reader.GetString(0), entry.TenantId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(1), entry.UserId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(2), entry.AgentId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), entry.WorkspaceId, StringComparison.Ordinal);
        if (!sameOwner)
            throw new MemoryRejectedException("Memory ID is already owned by another partition.");
    }

    private async Task UpsertMemoryAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        MemoryEntry entry)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = """
            INSERT INTO memory_entries(
                id, scope, tenant_id, user_id, agent_id, workspace_id,
                session_id, source_session_id, type, category, content, created_at,
                last_accessed_at, importance, confidence, expires_at, sensitivity,
                content_hash, embedding, metadata_json)
            VALUES(
                $id, $scope, $tenantId, $userId, $agentId, $workspaceId,
                $sessionId, $sourceSessionId, $type, $category, $content, $createdAt,
                $lastAccessedAt, $importance, $confidence, $expiresAt, $sensitivity,
                $contentHash, $embedding, $metadataJson)
            ON CONFLICT(id) DO UPDATE SET
                scope = excluded.scope,
                tenant_id = excluded.tenant_id,
                user_id = excluded.user_id,
                agent_id = excluded.agent_id,
                workspace_id = excluded.workspace_id,
                session_id = excluded.session_id,
                source_session_id = excluded.source_session_id,
                type = excluded.type,
                category = excluded.category,
                content = excluded.content,
                created_at = excluded.created_at,
                last_accessed_at = excluded.last_accessed_at,
                importance = excluded.importance,
                confidence = excluded.confidence,
                expires_at = excluded.expires_at,
                sensitivity = excluded.sensitivity,
                content_hash = excluded.content_hash,
                embedding = excluded.embedding,
                metadata_json = excluded.metadata_json;
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$scope", (int)entry.Scope);
        command.Parameters.AddWithValue("$tenantId", entry.TenantId);
        command.Parameters.AddWithValue("$userId", entry.UserId);
        command.Parameters.AddWithValue("$agentId", entry.AgentId);
        command.Parameters.AddWithValue("$workspaceId", entry.WorkspaceId);
        command.Parameters.AddWithValue("$sessionId", (object?)entry.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceSessionId", (object?)entry.SourceSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", entry.Type);
        command.Parameters.AddWithValue("$category", (object?)entry.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$createdAt", ToTimestamp(entry.CreatedAt));
        command.Parameters.AddWithValue("$lastAccessedAt", ToTimestamp(entry.LastAccessedAt));
        command.Parameters.AddWithValue("$importance", entry.Importance);
        command.Parameters.AddWithValue("$confidence", entry.Confidence);
        command.Parameters.AddWithValue(
            "$expiresAt",
            entry.ExpiresAt.HasValue ? ToTimestamp(entry.ExpiresAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$sensitivity", (int)entry.Sensitivity);
        command.Parameters.AddWithValue("$contentHash", (object?)entry.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$embedding",
            entry.Embedding is { Count: > 0 }
                ? SerializeEmbedding(entry.Embedding)
                : DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", JsonSerializer.Serialize(entry.Metadata, JsonOptions));
        await command.ExecuteNonQueryAsync();
        await SyncVectorAsync(connection, (SqliteTransaction?)transaction, entry);
    }

    private async Task TouchAsync(IEnumerable<MemoryEntry> memories)
    {
        var entries = memories.ToList();
        if (entries.Count == 0) return;
        var now = DateTime.UtcNow;
        await _writeLock.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var memory in entries)
            {
                memory.LastAccessedAt = now;
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "UPDATE memory_entries SET last_accessed_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$now", ToTimestamp(now));
                command.Parameters.AddWithValue("$id", memory.Id);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private IEnumerable<MemoryEntry> RankMediumTermMemories(
        IEnumerable<MemoryEntry> memories,
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
                    Similarity = InMemoryMemoryManager.CosineSimilarity(queryEmbedding, memory.Embedding!)
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

    private List<MemoryEntry> RankLongTermMemories(
        IEnumerable<MemoryEntry> memories,
        string query,
        IList<float>? queryEmbedding,
        LongTermMemoryOptions options)
    {
        var candidates = memories
            .Where(memory => memory.Importance >= options.MinimumImportance)
            .Where(memory => memory.Confidence >= options.MinimumConfidence)
            .Where(memory => !options.ExcludeExpired
                || memory.ExpiresAt is null
                || memory.ExpiresAt > DateTime.UtcNow)
            .Where(memory => options.Types.Count == 0 || options.Types.Contains(memory.Type))
            .ToList();
        if (queryEmbedding is { Count: > 0 })
        {
            var semantic = candidates
                .Where(memory => memory.Embedding is { Count: > 0 })
                .Select(memory => new
                {
                    Memory = memory,
                    Similarity = InMemoryMemoryManager.CosineSimilarity(queryEmbedding, memory.Embedding!)
                })
                .Where(item => item.Similarity >= _embeddingOptions.MinimumSimilarity)
                .Select(item => item.Memory)
                .ToList();
            semantic = RerankLongTermCandidates(semantic, queryEmbedding, options);
            if (semantic.Count > 0 || !_embeddingOptions.FallbackToLexicalSearch)
                return semantic;
        }

        return candidates
            .Where(memory => MatchesQuery(memory, query))
            .OrderByDescending(memory => memory.Importance)
            .ThenByDescending(memory => memory.LastAccessedAt)
            .ThenByDescending(memory => memory.CreatedAt)
            .Take(Math.Max(0, options.MaxMemories))
            .ToList();
    }

    private static List<MemoryEntry> RerankLongTermCandidates(
        IEnumerable<MemoryEntry> memories,
        IList<float>? queryEmbedding,
        LongTermMemoryOptions options)
    {
        var now = DateTime.UtcNow;
        return memories
            .Where(memory => memory.Importance >= options.MinimumImportance)
            .Where(memory => memory.Confidence >= options.MinimumConfidence)
            .Where(memory => !options.ExcludeExpired
                || memory.ExpiresAt is null
                || memory.ExpiresAt > now)
            .Where(memory => options.Types.Count == 0 || options.Types.Contains(memory.Type))
            .Select(memory =>
            {
                var semantic = queryEmbedding is { Count: > 0 }
                    && memory.Embedding is { Count: > 0 }
                    ? Math.Clamp(
                        InMemoryMemoryManager.CosineSimilarity(queryEmbedding, memory.Embedding),
                        0,
                        1)
                    : 0;
                var ageDays = Math.Max(0, (now - memory.LastAccessedAt).TotalDays);
                var freshness = Math.Exp(-ageDays / 180d);
                var score = 0.55 * semantic
                    + 0.20 * memory.Importance
                    + 0.15 * freshness
                    + 0.10 * memory.Confidence;
                return new { Memory = memory, Score = score };
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Memory.CreatedAt)
            .Select(item => item.Memory)
            .Take(Math.Max(0, options.MaxMemories))
            .ToList();
    }

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

    private static bool MatchesQuery(MemoryEntry memory, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return memory.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
            || memory.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (memory.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || memory.Metadata.Values.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private MemoryEntry BuildSummaryMemory(
        MemoryPartition partition,
        IList<ChatMessage> messages,
        int maxChars)
    {
        var startedAt = messages.Min(message => message.CreatedAt);
        var endedAt = messages.Max(message => message.CreatedAt);
        var content = string.Join(
            Environment.NewLine,
            messages.Select(message =>
                $"{message.CreatedAt:O} {message.Role}: {RedactDetectedSecrets(message.Content)}"));
        if (maxChars > 0 && content.Length > maxChars)
            content = content[..maxChars] + "...";
        return new MemoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Scope = MemoryScope.MediumTerm,
            Type = "ConversationSummary",
            Category = "Session",
            SessionId = partition.SessionId,
            TenantId = partition.TenantId,
            UserId = partition.UserId,
            AgentId = partition.AgentId,
            WorkspaceId = partition.WorkspaceId,
            SourceSessionId = partition.SessionId,
            Content = $"会话片段摘要 ({startedAt:O} - {endedAt:O}, {messages.Count} 条消息):\n{content}",
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            Importance = 0.6,
            Metadata =
            {
                ["startedAt"] = startedAt.ToString("O"),
                ["endedAt"] = endedAt.ToString("O"),
                ["messageCount"] = messages.Count.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    private void ApplyAdmissionPolicy(MemoryEntry entry)
    {
        entry.Content = entry.Content.Trim();
        if (entry.Content.Length == 0)
            throw new MemoryRejectedException("Empty memory content is not allowed.");
        if (_admissionOptions.MaxContentChars > 0
            && entry.Content.Length > _admissionOptions.MaxContentChars)
        {
            throw new MemoryRejectedException(
                $"Memory content exceeds {_admissionOptions.MaxContentChars} characters.");
        }
        if (entry.Importance is < 0 or > 1 || entry.Confidence is < 0 or > 1)
            throw new MemoryRejectedException("Memory importance and confidence must be between 0 and 1.");
        if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= DateTime.UtcNow)
            throw new MemoryRejectedException("Expired memory cannot be added.");
        if (_admissionOptions.RejectSecretSensitivity
            && entry.Sensitivity == MemorySensitivity.Secret)
        {
            throw new MemoryRejectedException("Secret-classified content cannot be persisted as long-term memory.");
        }
        if (_admissionOptions.RejectDetectedSecrets && SecretPattern().IsMatch(entry.Content))
            throw new MemoryRejectedException("Potential credential or secret detected in memory content.");

        entry.TenantId = NormalizePartitionValue(entry.TenantId);
        entry.UserId = NormalizePartitionValue(entry.UserId);
        entry.AgentId = NormalizePartitionValue(entry.AgentId);
        entry.WorkspaceId = NormalizePartitionValue(entry.WorkspaceId);
        entry.SourceSessionId ??= entry.SessionId;
        entry.SessionId = null;
        entry.ContentHash = ComputeContentHash(entry);
    }

    private string RedactDetectedSecrets(string content)
        => _admissionOptions.RejectDetectedSecrets
            ? SecretPattern().Replace(content, "[REDACTED]")
            : content;

    private static string ComputeContentHash(MemoryEntry entry)
    {
        var normalized = string.Join('\n',
            entry.TenantId,
            entry.UserId,
            entry.AgentId,
            entry.WorkspaceId,
            entry.Type.Trim().ToLowerInvariant(),
            entry.Category?.Trim().ToLowerInvariant() ?? string.Empty,
            Regex.Replace(entry.Content.Trim().ToLowerInvariant(), @"\s+", " "));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    [GeneratedRegex(
        @"(?i)(-----BEGIN [A-Z ]*PRIVATE KEY-----|\bBearer\s+[A-Za-z0-9._~+/=-]{12,}|\bsk-[A-Za-z0-9_-]{12,}|\b(?:password|passwd|api[_-]?key|access[_-]?token|secret)\s*[:=]\s*\S{6,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    private static byte[] SerializeEmbedding(IList<float> embedding)
    {
        var values = embedding.ToArray();
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void ValidatePartition(MemoryPartition partition, bool requireSession)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (requireSession && string.IsNullOrWhiteSpace(partition.SessionId))
            throw new ArgumentException("Memory partition session ID is required.", nameof(partition));
    }

    private static void AddPartitionParameters(SqliteCommand command, MemoryPartition partition)
    {
        command.Parameters.AddWithValue("$tenantId", NormalizePartitionValue(partition.TenantId));
        command.Parameters.AddWithValue("$userId", NormalizePartitionValue(partition.UserId));
        command.Parameters.AddWithValue("$agentId", NormalizePartitionValue(partition.AgentId));
        command.Parameters.AddWithValue("$workspaceId", NormalizePartitionValue(partition.WorkspaceId));
        command.Parameters.AddWithValue("$sessionId", NormalizePartitionValue(partition.SessionId));
    }

    private static void AddVectorPartitionParameters(SqliteCommand command, MemoryPartition partition)
    {
        command.Parameters.AddWithValue("$tenantKey", StablePartitionKey(partition.TenantId));
        command.Parameters.AddWithValue("$userKey", StablePartitionKey(partition.UserId));
        command.Parameters.AddWithValue("$agentKey", StablePartitionKey(partition.AgentId));
        command.Parameters.AddWithValue("$workspaceKey", StablePartitionKey(partition.WorkspaceId));
        command.Parameters.AddWithValue("$sessionKey", StablePartitionKey(partition.SessionId));
    }

    private static long StablePartitionKey(string? value)
    {
        var normalized = NormalizePartitionValue(value);
        if (normalized.Length == 0) return 0;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }

    private static string NormalizePartitionValue(string? value)
        => value?.Trim() ?? string.Empty;

    private static IList<float> DeserializeEmbedding(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
            throw new InvalidDataException("Stored embedding BLOB length is invalid.");
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static string ResolveDatabasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("SQLite memory database path is required.", nameof(path));
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~")
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        return Path.GetFullPath(expanded);
    }

    private static string? ResolveVectorExtensionPath(SqliteVectorOptions options)
    {
        if (!options.Enabled) return null;
        if (!string.IsNullOrWhiteSpace(options.ExtensionPath))
        {
            var configured = Environment.ExpandEnvironmentVariables(options.ExtensionPath);
            var absolute = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(configured);
            return File.Exists(absolute) ? absolute : null;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-arm64", "native", "vec1.dylib"),
            Path.Combine(AppContext.BaseDirectory, "vec1.dylib"),
            Path.Combine(
                Path.GetDirectoryName(typeof(SqliteMemoryManager).Assembly.Location) ?? AppContext.BaseDirectory,
                "runtimes", "osx-arm64", "native", "vec1.dylib")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ToTimestamp(DateTime value)
        => (value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime())
            .ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseTimestamp(string value)
        => DateTime.ParseExact(
            value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record StoredMessage(long Id, ChatMessage Message);
}
