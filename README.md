# Goldfish Harness

Goldfish Harness is the standalone C# reasoning library used by Goldfish AgentNode.

It contains the model-driven ReAct loop, native tool/function calling integration,
streaming harness events, prompt/context abstractions, and tool registry contracts.

## Build

```bash
dotnet build Goldfish.Harness.slnx -c Release
```

## Project Layout

- `src/Goldfish.Harness`: class library targeting `.NET 10`
- `GoldfishHarnessRunner`: main entry point for non-streaming and streaming runs
- `IToolRegistry` / `ITool`: tool loading and invocation contracts
- `GoldfishHarnessEvent`: strongly typed stream event model

AgentFree references this project as a sibling checkout:

```xml
<ProjectReference Include="../../../Goldfish.Harness/src/Goldfish.Harness/Goldfish.Harness.csproj" />
```

## Vector memory

The memory subsystem supports short-, medium-, and long-term memory. Medium-term
conversation chunks and long-term memory entries can be indexed with any
OpenAI-compatible `/v1/embeddings` endpoint. Query vectors and document vectors
are sent with the `x-embedding-input-type` header so asymmetric embedding models
such as EmbeddingGemma can apply the correct formatting.

For example, a locally hosted `qmd-embeddinggemma-300m` model (768 dimensions)
can be exposed at `http://127.0.0.1:18790/v1/embeddings`. A complete configuration
example is available in
[`examples/memory.appsettings.json`](examples/memory.appsettings.json).

Bind the `Goldfish:Memory` section in the host application and use the same
options for the manager and agent loop:

```csharp
var memoryOptions = configuration
    .GetSection("Goldfish:Memory")
    .Get<MemoryOptions>() ?? MemoryOptions.Default;

IMemoryManager memoryManager = memoryOptions.Sqlite.Enabled
    ? SqliteMemoryManager.FromOptions(memoryOptions, httpClient)
    : InMemoryMemoryManager.FromOptions(memoryOptions, httpClient);
var agent = new AgenticLoopEngine(
    agentInfo,
    chatClient,
    toolRegistry,
    memoryManager,
    promptBuilder,
    memoryOptions: memoryOptions);
```

When the endpoint cannot be reached, `FallbackToLexicalSearch=true` keeps the
existing keyword/recency behavior. Set it to `false` when embedding failures
should fail the run.

### SQLite persistence

Set `Goldfish:Memory:Sqlite:Enabled=true` to persist all three layers in SQLite.
The default database path is `~/.goldfish/memory.db`; `~` and environment
variables are expanded by the library. SQLite mode stores:

- raw short-term messages in `memory_messages`;
- medium- and long-term records in `memory_entries`;
- float32 embeddings as compact BLOBs;
- type, category, importance, timestamps, and metadata alongside each memory.

The database and current schema are initialized on manager construction. WAL mode and a
30-second busy timeout are enabled by default for safe concurrent readers and a
single serialized writer. `DeleteSessionAsync` deletes short- and medium-term
state for that session while preserving cross-session long-term memories.

### SQLite Vec1

The official SQLite Vec1 v0.7 extension is bundled for macOS arm64 at
`runtimes/osx-arm64/native/vec1.dylib`. Set `Sqlite:Vector:Enabled=true` to load
it automatically from the application output directory. The bundled binary is
compiled with Apple Clang using `-O3`, uses ARM NEON, supports multiple threads,
and is ad-hoc code signed for local macOS loading.

Vec1 mode creates a `memory_vectors` virtual table and a stable
`memory_vector_map` row-id mapping. Memory writes update both the ordinary
`memory_entries` table and the Vec1 table in the same SQLite transaction. Query
embeddings are searched inside SQLite with cosine distance; the managed C# scan
remains available when `FallbackToManagedSearch=true`.

`IndexMode=flat` is the production default. It provides exact nearest-neighbor
results and avoids ANN training overhead for small memory collections. Vec1 ANN
uses a trained IVF/OPQ model and should be enabled only after enough
representative vectors have accumulated (the Vec1 documentation suggests exact
search below roughly 5,000 vectors).

### Memory isolation and admission

Use `MemoryPartition` for every multi-user operation. Tenant, user, agent, and
workspace identifiers are hard filters applied before Vec1 similarity search;
the session identifier additionally isolates short- and medium-term memory.

```csharp
var partition = new MemoryPartition
{
    TenantId = tenantId,
    UserId = userId,
    AgentId = agentId,
    WorkspaceId = workspaceId,
    SessionId = sessionId
};

await memoryManager.AddMessageAsync(partition, message);
await memoryManager.AddMemoryAsync(partition, new MemoryEntry
{
    Content = "User prefers PostgreSQL.",
    Type = "UserPreference",
    Importance = 0.8,
    Confidence = 1.0,
    ExpiresAt = null
});
var context = await memoryManager.BuildContextAsync(partition, query, memoryOptions);
```

The original string-only methods remain for legacy single-user callers and map
to an empty legacy partition. Multi-user hosts must use the partition overloads.
Long-term entries are user/agent/workspace scoped across sessions, while
`SourceSessionId` records where the memory originated.

Long-term admission rejects empty, oversized, expired, secret-classified, and
credential-looking content. Content hashes deduplicate equivalent memories
within a partition, and explicit IDs cannot overwrite another partition.
Retrieval excludes expired or low-confidence entries and reranks candidates with
semantic similarity, importance, freshness, and confidence.
