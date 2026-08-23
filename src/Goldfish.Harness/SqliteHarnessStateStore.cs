using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Goldfish.Harness;

public sealed class SqliteHarnessStateStore : ISkillSessionStore, IToolExecutionStore, IHarnessRuntimeStore, IDisposable
{
    public const int CurrentSchemaVersion = 2;
    private readonly string _databasePath;
    private readonly HarnessStateOptions _options;
    private readonly SemaphoreSlim _writerLock = new(1, 1);
    private bool _disposed;

    public SqliteHarnessStateStore(string? databasePath = null, HarnessStateOptions? options = null)
    {
        _options = options ?? new HarnessStateOptions();
        _databasePath = ExpandPath(string.IsNullOrWhiteSpace(databasePath)
            ? "~/.goldfish/harness-state.db"
            : databasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        Initialize(connection);
        RestrictPermissions();
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public async Task<HarnessTurnCreateResult> GetOrCreateTurnAsync(
        GoldfishHarnessTurn turn,
        string userMessage,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO goldfish_turns (
                    turn_id, request_id, run_id, tenant_id, user_id, agent_id, workspace_id,
                    session_id, strategy, retry_of_turn_id, status, version, created_at)
                VALUES (
                    $turn_id, $request_id, $run_id, $tenant_id, $user_id, $agent_id, $workspace_id,
                    $session_id, $strategy, $retry_of_turn_id, $status, 0, $created_at);
                """;
            AddTurnParameters(command, turn);
            var created = await command.ExecuteNonQueryAsync(ct) == 1;
            if (created)
            {
                await using var message = connection.CreateCommand();
                message.Transaction = transaction;
                message.CommandText = """
                    INSERT INTO memory_messages (
                        tenant_id, user_id, agent_id, workspace_id, session_id,
                        turn_id, message_sequence, role, content, created_at)
                    VALUES ($tenant_id, $user_id, $agent_id, $workspace_id, $session_id,
                        $turn_id, 0, 'user', $content, $created_at);
                    """;
                AddPartitionParameters(message, turn.Partition);
                message.Parameters.AddWithValue("$turn_id", turn.TurnId);
                message.Parameters.AddWithValue("$content", userMessage);
                message.Parameters.AddWithValue("$created_at", turn.CreatedAt.ToString("O"));
                await message.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            var stored = created ? turn : await GetByRequestCoreAsync(connection, turn.Partition, turn.RequestId, ct)
                ?? throw new InvalidOperationException("The request id exists but its turn could not be loaded.");
            return new HarnessTurnCreateResult(stored, created);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task<bool> TryStartAsync(
        string turnId,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE goldfish_turns
                SET status = 'Running', started_at = $now, heartbeat_at = $now,
                    lease_owner = $owner, lease_expires_at = $expires, version = version + 1
                WHERE turn_id = $turn_id AND status = 'Queued';
                """;
            var now = DateTimeOffset.UtcNow.ToString("O");
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$owner", leaseOwner);
            command.Parameters.AddWithValue("$expires", leaseExpiresAt.ToString("O"));
            command.Parameters.AddWithValue("$turn_id", turnId);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task AppendEventsAsync(
        string turnId,
        string sessionId,
        IReadOnlyList<GoldfishHarnessEvent> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0) return;
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var sequence = await NextSequenceAsync(connection, transaction, turnId, ct);
            foreach (var ev in events)
                await InsertEventAsync(connection, transaction, turnId, sessionId, sequence++, ev, ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task<bool> TryCompleteAsync(
        string turnId,
        GoldfishTurnStatus status,
        string? terminalReasonCode,
        string? terminalReason,
        string? assistantMessage,
        CancellationToken ct = default)
    {
        if (status is not (GoldfishTurnStatus.Completed or GoldfishTurnStatus.Failed
            or GoldfishTurnStatus.Canceled or GoldfishTurnStatus.Orphaned))
            throw new ArgumentOutOfRangeException(nameof(status), "A terminal status is required.");

        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE goldfish_turns
                SET status = $status, completed_at = $completed_at,
                    terminal_reason_code = $reason_code, terminal_reason = $reason,
                    lease_owner = NULL, lease_expires_at = NULL, version = version + 1
                WHERE turn_id = $turn_id AND status IN ('Queued', 'Running');
                """;
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$completed_at", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$reason_code", (object?)terminalReasonCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$reason", (object?)terminalReason ?? DBNull.Value);
            update.Parameters.AddWithValue("$turn_id", turnId);
            var changed = await update.ExecuteNonQueryAsync(ct) == 1;
            if (changed && status == GoldfishTurnStatus.Completed && assistantMessage is not null)
            {
                await using var message = connection.CreateCommand();
                message.Transaction = transaction;
                message.CommandText = """
                    INSERT OR IGNORE INTO memory_messages (
                        tenant_id, user_id, agent_id, workspace_id, session_id,
                        turn_id, message_sequence, role, content, created_at)
                    SELECT tenant_id, user_id, agent_id, workspace_id, session_id,
                        turn_id, 1, 'assistant', $content, $created_at
                    FROM goldfish_turns WHERE turn_id = $turn_id;
                    """;
                message.Parameters.AddWithValue("$turn_id", turnId);
                message.Parameters.AddWithValue("$content", assistantMessage);
                message.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
                await message.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return changed;
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task<bool> TryCompleteWithEventAsync(
        string turnId,
        string sessionId,
        GoldfishHarnessEvent terminalEvent,
        GoldfishTurnStatus status,
        string? terminalReasonCode,
        string? terminalReason,
        string? assistantMessage,
        CancellationToken ct = default)
    {
        if (terminalEvent.Kind is not (GoldfishEventKind.Completed or GoldfishEventKind.Failed))
            throw new ArgumentException("A terminal Harness event is required.", nameof(terminalEvent));
        if (status is not (GoldfishTurnStatus.Completed or GoldfishTurnStatus.Failed
            or GoldfishTurnStatus.Canceled or GoldfishTurnStatus.Orphaned))
            throw new ArgumentOutOfRangeException(nameof(status));

        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE goldfish_turns
                SET status = $status, completed_at = $completed_at,
                    terminal_reason_code = $reason_code, terminal_reason = $reason,
                    lease_owner = NULL, lease_expires_at = NULL, version = version + 1
                WHERE turn_id = $turn_id AND status IN ('Queued', 'Running');
                """;
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$completed_at", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$reason_code", (object?)terminalReasonCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$reason", (object?)terminalReason ?? DBNull.Value);
            update.Parameters.AddWithValue("$turn_id", turnId);
            var changed = await update.ExecuteNonQueryAsync(ct) == 1;
            if (changed)
            {
                var sequence = await NextSequenceAsync(connection, transaction, turnId, ct);
                await InsertEventAsync(connection, transaction, turnId, sessionId, sequence, terminalEvent, ct);
                if (status == GoldfishTurnStatus.Completed && assistantMessage is not null)
                {
                    await using var message = connection.CreateCommand();
                    message.Transaction = transaction;
                    message.CommandText = """
                        INSERT OR IGNORE INTO memory_messages (
                            tenant_id, user_id, agent_id, workspace_id, session_id,
                            turn_id, message_sequence, role, content, created_at)
                        SELECT tenant_id, user_id, agent_id, workspace_id, session_id,
                            turn_id, 1, 'assistant', $content, $created_at
                        FROM goldfish_turns WHERE turn_id = $turn_id;
                        """;
                    message.Parameters.AddWithValue("$turn_id", turnId);
                    message.Parameters.AddWithValue("$content", assistantMessage);
                    message.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
                    await message.ExecuteNonQueryAsync(ct);
                }
            }
            await transaction.CommitAsync(ct);
            return changed;
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task HeartbeatAsync(string turnId, string leaseOwner, DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE goldfish_turns SET heartbeat_at = $now, lease_expires_at = $expires
            WHERE turn_id = $turn_id AND status = 'Running' AND lease_owner = $owner;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$expires", leaseExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("$turn_id", turnId);
        command.Parameters.AddWithValue("$owner", leaseOwner);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GoldfishHarnessTurn?> GetTurnAsync(string turnId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await using var connection = OpenConnection();
        await using var command = TurnSelect(connection);
        command.CommandText += " WHERE turn_id = $turn_id;";
        command.Parameters.AddWithValue("$turn_id", turnId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTurn(reader) : null;
    }

    public async Task<GoldfishHarnessTurn?> GetByRequestAsync(GoldfishTurnPartition partition, string requestId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await using var connection = OpenConnection();
        return await GetByRequestCoreAsync(connection, partition, requestId, ct);
    }

    public async Task<IReadOnlyList<GoldfishHarnessTurnEvent>> ReadEventsAsync(string turnId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, payload_json, blob_id
            FROM goldfish_turn_events WHERE turn_id = $turn_id ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$turn_id", turnId);
        var result = new List<GoldfishHarnessTurnEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sessionId = reader.GetString(0);
            var payload = reader.GetString(1);
            if (!reader.IsDBNull(2)) payload = await ReadBlobJsonAsync(connection, reader.GetString(2), ct);
            var ev = JsonSerializer.Deserialize<GoldfishHarnessEvent>(payload, HarnessRuntimeJson.Options)
                ?? throw new InvalidDataException("Stored Harness event is empty.");
            result.Add(new GoldfishHarnessTurnEvent(turnId, sessionId, ev));
        }
        return result;
    }

    public async Task<int> RecoverOrphanedAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE goldfish_turns
                SET status = 'Orphaned', completed_at = $now,
                    terminal_reason_code = 'lease_expired',
                    terminal_reason = 'Harness process exited before a terminal event.',
                    lease_owner = NULL, lease_expires_at = NULL, version = version + 1
                WHERE status = 'Running' AND (lease_expires_at IS NULL OR lease_expires_at < $now);
                """;
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task ResetSessionAsync(GoldfishTurnPartition partition, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var table in new[] { "memory_messages", "goldfish_skill_sessions" })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    DELETE FROM {table}
                    WHERE tenant_id = $tenant_id AND user_id = $user_id AND agent_id = $agent_id
                      AND workspace_id = $workspace_id AND session_id = $session_id;
                    """;
                AddPartitionParameters(command, partition);
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var total = 0;
            foreach (var sql in new[]
            {
                "DELETE FROM goldfish_turn_events WHERE created_at < $cutoff;",
                "DELETE FROM goldfish_turn_blobs WHERE created_at < $cutoff;",
                "UPDATE goldfish_tool_executions SET arguments_json = NULL, result_json = NULL, structured_content_json = NULL WHERE completed_at < $cutoff;"
            })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
                total += await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "PRAGMA incremental_vacuum(128);";
            await vacuum.ExecuteNonQueryAsync(ct);
            return total;
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task<IReadOnlyList<SkillSessionEntry>> LoadAsync(
        SkillSessionKey key,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT skill_name, loaded_at, source
            FROM goldfish_skill_sessions
            WHERE tenant_id = $tenant_id
              AND user_id = $user_id
              AND agent_id = $agent_id
              AND workspace_id = $workspace_id
              AND session_id = $session_id
            ORDER BY loaded_at ASC;
            """;
        AddSkillKeyParameters(command, key);

        var entries = new List<SkillSessionEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new SkillSessionEntry
            {
                SkillName = reader.GetString(0),
                LoadedAt = DateTimeOffset.Parse(reader.GetString(1)),
                Source = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return entries;
    }

    public async Task RecordLoadedAsync(
        SkillSessionKey key,
        SkillSessionEntry entry,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.SkillName))
        {
            return;
        }

        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO goldfish_skill_sessions (
                    tenant_id, user_id, agent_id, workspace_id, session_id,
                    skill_name, loaded_at, source
                ) VALUES (
                    $tenant_id, $user_id, $agent_id, $workspace_id, $session_id,
                    $skill_name, $loaded_at, $source
                )
                ON CONFLICT(tenant_id, user_id, agent_id, workspace_id, session_id, skill_name)
                DO UPDATE SET loaded_at = excluded.loaded_at, source = excluded.source;
                """;
            AddSkillKeyParameters(command, key);
            command.Parameters.AddWithValue("$skill_name", entry.SkillName.Trim());
            command.Parameters.AddWithValue("$loaded_at", entry.LoadedAt.ToString("O"));
            command.Parameters.AddWithValue("$source", (object?)entry.Source ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task RecordAsync(ToolExecutionRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO goldfish_tool_executions (
                    id, run_id, session_id, tenant_id, user_id, agent_id, workspace_id,
                    step, tool_call_id, tool_id, arguments_hash, result_hash, success,
                    error, authorization_decision, started_at, completed_at
                ) VALUES (
                    $id, $run_id, $session_id, $tenant_id, $user_id, $agent_id, $workspace_id,
                    $step, $tool_call_id, $tool_id, $arguments_hash, $result_hash, $success,
                    $error, $authorization_decision, $started_at, $completed_at
                );
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n"));
            command.Parameters.AddWithValue("$run_id", record.RunId);
            command.Parameters.AddWithValue("$session_id", record.SessionId);
            command.Parameters.AddWithValue("$tenant_id", (object?)record.TenantId ?? string.Empty);
            command.Parameters.AddWithValue("$user_id", (object?)record.UserId ?? string.Empty);
            command.Parameters.AddWithValue("$agent_id", (object?)record.AgentId ?? string.Empty);
            command.Parameters.AddWithValue("$workspace_id", (object?)record.WorkspaceId ?? string.Empty);
            command.Parameters.AddWithValue("$step", record.Step);
            command.Parameters.AddWithValue("$tool_call_id", (object?)record.ToolCallId ?? DBNull.Value);
            command.Parameters.AddWithValue("$tool_id", record.ToolId);
            command.Parameters.AddWithValue("$arguments_hash", record.ArgumentsHash);
            command.Parameters.AddWithValue("$result_hash", (object?)record.ResultHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$success", record.Success ? 1 : 0);
            command.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$authorization_decision", record.AuthorizationDecision);
            command.Parameters.AddWithValue("$started_at", record.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$completed_at", record.CompletedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _writerLock.Dispose();
        _disposed = true;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS goldfish_skill_sessions (
                tenant_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                workspace_id TEXT NOT NULL DEFAULT '',
                session_id TEXT NOT NULL DEFAULT '',
                skill_name TEXT NOT NULL,
                loaded_at TEXT NOT NULL,
                source TEXT NULL,
                PRIMARY KEY (tenant_id, user_id, agent_id, workspace_id, session_id, skill_name)
            );

            CREATE TABLE IF NOT EXISTS goldfish_tool_executions (
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL DEFAULT '',
                tenant_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                workspace_id TEXT NOT NULL DEFAULT '',
                step INTEGER NOT NULL,
                tool_call_id TEXT NULL,
                tool_id TEXT NOT NULL,
                arguments_hash TEXT NOT NULL,
                result_hash TEXT NULL,
                success INTEGER NOT NULL,
                error TEXT NULL,
                authorization_decision TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_goldfish_tool_executions_session
            ON goldfish_tool_executions (
                tenant_id, user_id, agent_id, workspace_id, session_id, started_at
            );

            CREATE TABLE IF NOT EXISTS memory_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id TEXT NOT NULL DEFAULT '', user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '', workspace_id TEXT NOT NULL DEFAULT '',
                session_id TEXT NOT NULL, turn_id TEXT NULL, message_sequence INTEGER NULL,
                role TEXT NOT NULL, content TEXT NOT NULL, tool_call_id TEXT NULL, created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS goldfish_turns (
                turn_id TEXT PRIMARY KEY, request_id TEXT NOT NULL, run_id TEXT NOT NULL DEFAULT '',
                tenant_id TEXT NOT NULL DEFAULT '', user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '', workspace_id TEXT NOT NULL DEFAULT '',
                session_id TEXT NOT NULL, strategy TEXT NOT NULL, retry_of_turn_id TEXT NULL,
                status TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
                started_at TEXT NULL, completed_at TEXT NULL, heartbeat_at TEXT NULL,
                lease_owner TEXT NULL, lease_expires_at TEXT NULL,
                terminal_reason_code TEXT NULL, terminal_reason TEXT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_goldfish_turns_partition_request
            ON goldfish_turns (tenant_id, user_id, agent_id, workspace_id, request_id);
            CREATE INDEX IF NOT EXISTS ix_goldfish_turns_session_created
            ON goldfish_turns (tenant_id, user_id, agent_id, workspace_id, session_id, created_at);
            CREATE INDEX IF NOT EXISTS ix_goldfish_turns_status_lease
            ON goldfish_turns (status, lease_expires_at);

            CREATE TABLE IF NOT EXISTS goldfish_turn_blobs (
                blob_id TEXT PRIMARY KEY, turn_id TEXT NOT NULL, content_encoding TEXT NOT NULL,
                content BLOB NOT NULL, content_hash TEXT NOT NULL, original_length INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(turn_id) REFERENCES goldfish_turns(turn_id)
            );

            CREATE TABLE IF NOT EXISTS goldfish_turn_events (
                event_id TEXT PRIMARY KEY, turn_id TEXT NOT NULL, session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL, event_kind TEXT NOT NULL, step INTEGER NOT NULL,
                payload_json TEXT NOT NULL, payload_hash TEXT NOT NULL, blob_id TEXT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(turn_id) REFERENCES goldfish_turns(turn_id),
                FOREIGN KEY(blob_id) REFERENCES goldfish_turn_blobs(blob_id),
                UNIQUE(turn_id, sequence)
            );

            CREATE INDEX IF NOT EXISTS ix_goldfish_turn_events_turn_sequence
            ON goldfish_turn_events(turn_id, sequence);

            PRAGMA user_version = 2;
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "memory_messages", "turn_id", "TEXT NULL");
        EnsureColumn(connection, "memory_messages", "message_sequence", "INTEGER NULL");
        EnsureColumn(connection, "goldfish_tool_executions", "turn_id", "TEXT NULL");
        EnsureColumn(connection, "goldfish_tool_executions", "arguments_json", "TEXT NULL");
        EnsureColumn(connection, "goldfish_tool_executions", "result_json", "TEXT NULL");
        EnsureColumn(connection, "goldfish_tool_executions", "structured_content_json", "TEXT NULL");
        EnsureColumn(connection, "goldfish_tool_executions", "is_error", "INTEGER NULL");
        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_messages_turn_sequence
            ON memory_messages(turn_id, message_sequence) WHERE turn_id IS NOT NULL;
            """;
        indexes.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void AddTurnParameters(SqliteCommand command, GoldfishHarnessTurn turn)
    {
        command.Parameters.AddWithValue("$turn_id", turn.TurnId);
        command.Parameters.AddWithValue("$request_id", turn.RequestId);
        command.Parameters.AddWithValue("$run_id", turn.RunId);
        AddPartitionParameters(command, turn.Partition);
        command.Parameters.AddWithValue("$strategy", turn.Strategy);
        command.Parameters.AddWithValue("$retry_of_turn_id", (object?)turn.RetryOfTurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", turn.Status.ToString());
        command.Parameters.AddWithValue("$created_at", turn.CreatedAt.ToString("O"));
    }

    private static void AddPartitionParameters(SqliteCommand command, GoldfishTurnPartition partition)
    {
        command.Parameters.AddWithValue("$tenant_id", partition.TenantId);
        command.Parameters.AddWithValue("$user_id", partition.UserId);
        command.Parameters.AddWithValue("$agent_id", partition.AgentId);
        command.Parameters.AddWithValue("$workspace_id", partition.WorkspaceId);
        command.Parameters.AddWithValue("$session_id", partition.SessionId);
    }

    private static SqliteCommand TurnSelect(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT turn_id, request_id, run_id, tenant_id, user_id, agent_id, workspace_id,
                   session_id, strategy, retry_of_turn_id, status, version, created_at,
                   started_at, completed_at, heartbeat_at, lease_owner, lease_expires_at,
                   terminal_reason_code, terminal_reason
            FROM goldfish_turns
            """;
        return command;
    }

    private static async Task<GoldfishHarnessTurn?> GetByRequestCoreAsync(
        SqliteConnection connection, GoldfishTurnPartition partition, string requestId, CancellationToken ct)
    {
        await using var command = TurnSelect(connection);
        command.CommandText += """
             WHERE tenant_id = $tenant_id AND user_id = $user_id AND agent_id = $agent_id
               AND workspace_id = $workspace_id AND request_id = $request_id;
            """;
        AddPartitionParameters(command, partition);
        command.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTurn(reader) : null;
    }

    private static GoldfishHarnessTurn ReadTurn(SqliteDataReader reader) => new()
    {
        TurnId = reader.GetString(0), RequestId = reader.GetString(1), RunId = reader.GetString(2),
        TenantId = reader.GetString(3), UserId = reader.GetString(4), AgentId = reader.GetString(5),
        WorkspaceId = reader.GetString(6), SessionId = reader.GetString(7), Strategy = reader.GetString(8),
        RetryOfTurnId = reader.IsDBNull(9) ? null : reader.GetString(9),
        Status = Enum.Parse<GoldfishTurnStatus>(reader.GetString(10)), Version = reader.GetInt32(11),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(12)),
        StartedAt = ParseNullableDate(reader, 13), CompletedAt = ParseNullableDate(reader, 14),
        HeartbeatAt = ParseNullableDate(reader, 15), LeaseOwner = reader.IsDBNull(16) ? null : reader.GetString(16),
        LeaseExpiresAt = ParseNullableDate(reader, 17), TerminalReasonCode = reader.IsDBNull(18) ? null : reader.GetString(18),
        TerminalReason = reader.IsDBNull(19) ? null : reader.GetString(19)
    };

    private static DateTimeOffset? ParseNullableDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static async Task<long> NextSequenceAsync(SqliteConnection connection, SqliteTransaction transaction, string turnId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM goldfish_turn_events WHERE turn_id = $turn_id;";
        command.Parameters.AddWithValue("$turn_id", turnId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task InsertBlobAsync(
        SqliteConnection connection, SqliteTransaction transaction, string blobId, string turnId,
        byte[] content, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await gzip.WriteAsync(content, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO goldfish_turn_blobs (
                blob_id, turn_id, content_encoding, content, content_hash, original_length, created_at)
            VALUES ($blob_id, $turn_id, 'gzip', $content, $hash, $length, $created_at);
            """;
        command.Parameters.AddWithValue("$blob_id", blobId);
        command.Parameters.AddWithValue("$turn_id", turnId);
        command.Parameters.AddWithValue("$content", compressed.ToArray());
        command.Parameters.AddWithValue("$hash", Hash(content));
        command.Parameters.AddWithValue("$length", content.Length);
        command.Parameters.AddWithValue("$created_at", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string turnId,
        string sessionId,
        long sequence,
        GoldfishHarnessEvent ev,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(ev, HarnessRuntimeJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        string payload;
        string? blobId = null;
        if (bytes.Length > _options.InlinePayloadBytes)
        {
            blobId = Guid.NewGuid().ToString("n");
            await InsertBlobAsync(connection, transaction, blobId, turnId, bytes, ev.Timestamp, ct);
            payload = JsonSerializer.Serialize(new { blobId, sha256 = Hash(bytes), length = bytes.Length });
        }
        else
        {
            payload = json;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO goldfish_turn_events (
                event_id, turn_id, session_id, sequence, event_kind, step,
                payload_json, payload_hash, blob_id, created_at)
            VALUES ($event_id, $turn_id, $session_id, $sequence, $event_kind, $step,
                $payload_json, $payload_hash, $blob_id, $created_at);
            """;
        command.Parameters.AddWithValue("$event_id", ev.EventId);
        command.Parameters.AddWithValue("$turn_id", turnId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$event_kind", ev.Kind.ToString());
        command.Parameters.AddWithValue("$step", ev.Step);
        command.Parameters.AddWithValue("$payload_json", payload);
        command.Parameters.AddWithValue("$payload_hash", Hash(bytes));
        command.Parameters.AddWithValue("$blob_id", (object?)blobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", ev.Timestamp.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> ReadBlobJsonAsync(SqliteConnection connection, string blobId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content, content_hash FROM goldfish_turn_blobs WHERE blob_id = $blob_id;";
        command.Parameters.AddWithValue("$blob_id", blobId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException($"Missing Harness event blob {blobId}.");
        var compressed = (byte[])reader[0];
        var expectedHash = reader.GetString(1);
        await using var source = new MemoryStream(compressed);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await using var target = new MemoryStream();
        await gzip.CopyToAsync(target, ct);
        var bytes = target.ToArray();
        if (!string.Equals(Hash(bytes), expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Harness event blob {blobId} failed its integrity check.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private void RestrictPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.Exists(_databasePath)) File.SetUnixFileMode(_databasePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void AddSkillKeyParameters(SqliteCommand command, SkillSessionKey key)
    {
        command.Parameters.AddWithValue("$tenant_id", key.TenantId);
        command.Parameters.AddWithValue("$user_id", key.UserId);
        command.Parameters.AddWithValue("$agent_id", key.AgentId);
        command.Parameters.AddWithValue("$workspace_id", key.WorkspaceId);
        command.Parameters.AddWithValue("$session_id", key.SessionId);
    }

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteHarnessStateStore));
        }
    }
}
