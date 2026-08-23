# Goldfish Harness

Goldfish Harness is a standalone C# reasoning runtime. It can be embedded as a
library or launched as an independent ACP process; it does not depend on
Goldfish AgentNode.

It contains the model-driven ReAct loop, native tool/function calling integration,
streaming harness events, prompt/context abstractions, and tool registry contracts.

## Build

```bash
dotnet build Goldfish.Harness.slnx -c Release
```

## Project Layout

- `src/Goldfish.Harness`: class library targeting `.NET 10`
- `src/Goldfish.Harness.AcpHost`: independent stdio ACP host targeting `.NET 10`
- `GoldfishHarnessRunner`: main entry point for non-streaming and streaming runs
- `GoldfishHarnessRuntime`: process-scoped durable turn engine and local partition queue
- `GoldfishHarnessContextAssembler`: persisted history and memory assembly boundary
- `IHarnessRuntimeStore`: durable turn, event, lease, replay, reset, and retention boundary
- `IToolRegistry` / `ITool`: tool loading and invocation contracts
- `GoldfishHarnessEvent`: strongly typed stream event model

AgentFree references this project as a sibling checkout:

```xml
<ProjectReference Include="../../../Goldfish.Harness/src/Goldfish.Harness/Goldfish.Harness.csproj" />
```

## Harness kernel

The runtime owns its harness implementation; it does not wrap Microsoft Agent
Framework or Codex. `GoldfishHarnessRuntime` owns the full-partition local queue,
request idempotency, bounded event stream, lease heartbeat, SQLite persistence,
crash recovery, and immutable terminal state. `GoldfishHarnessSessionExecutor`
remains as a compatibility facade for embedded callers.

The standalone ACP host uses `<stateRoot>/harness-state.db` as the durable source
of truth. `Dual` mode also appends `<stateRoot>/turn-events.jsonl` during the
staged migration and imports recent legacy JSONL entries idempotently. Terminal
events, assistant messages, and terminal state are committed together. Expired
running leases become `Orphaned` and are never replayed automatically.

State behavior is configured through `HarnessState__Mode` (`Jsonl`, `Dual`, or
`Sqlite`), `HarnessState__RetentionDays` (default 30),
`HarnessState__DeltaBatchMilliseconds` (50), `HarnessState__DeltaBatchBytes`
(4096), and `HarnessState__LeaseSeconds` (30). The local deployment starts in
`Dual` mode. Event and tool business payloads remain available for 30 days;
credentials, authorization headers, tokens, passwords, and authorization codes
are redacted before persistence.

## ACP host

Build or publish the independent host without AgentNode:

```bash
dotnet build src/Goldfish.Harness.AcpHost/Goldfish.Harness.AcpHost.csproj -c Release
dotnet publish src/Goldfish.Harness.AcpHost/Goldfish.Harness.AcpHost.csproj -c Release -o artifacts/acp-host
```

The host exchanges one JSON-RPC frame per line over standard input and output.
Diagnostics go to standard error so they cannot corrupt the ACP stream. It
supports `initialize`, `session/new`, `session/prompt`, `session/cancel`, and
`session/reset` plus `shutdown`. Harness text, reasoning, tool lifecycle, attachment, and runtime
error events are projected to ACP `session/update` notifications.

`session/prompt` accepts stable execution metadata under `_meta.agentfree`:
`turnId`, `requestId`, `retryOfTurnId`, `source`, `context`, and nested
`reasoning.strategy`. ReAct remains the default. Auto classification is used
only when the metadata explicitly requests `auto`; normal prompt text does not
change the configured strategy.

`session/new` requires an absolute `cwd` and runtime configuration under
`_meta.agentfree.runtime`:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "session/new",
  "params": {
    "cwd": "/absolute/workspace",
    "_meta": {
      "agentfree": {
        "requestedSessionId": "session-1",
        "runtime": {
          "baseUrl": "https://llm.example/v1",
          "apiKey": "resolved-by-the-host-launcher",
          "model": "model-id",
          "systemPrompt": "You are a coding agent.",
          "stateRoot": "/absolute/state-directory",
          "maxOutputTokens": 4096,
          "memory": {
            "tenantId": "tenant",
            "userId": "user",
            "agentId": "agent",
            "workspaceId": "workspace"
          }
        }
      }
    }
  }
}
```

The ACP launcher owns secret resolution and process isolation. Avoid persisting
API keys in project files or session history. `cwd`, `stateRoot`, and
`skillsRoot` must be absolute paths. Built-in file tools are restricted to
`cwd`; command execution intentionally runs inside that workspace and should be
guarded by the launcher's sandbox and approval policy.

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
using var chatClient = new WhitespacePreservingOpenAiChatClient(
    baseUrl,
    apiKey,
    model);
var runner = new GoldfishHarnessRunner(chatClient, toolRegistry);
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

### Context compression triggers

Medium-term compression can trigger by either message count or estimated request
size. Configure `Goldfish:Memory:MediumTerm` with an estimated model input
budget when the host knows the upstream context limit:

```json
{
  "CompressionThresholdMessages": 24,
  "CompressionThresholdEstimatedTokens": 12000,
  "MaxEstimatedInputTokens": 16000,
  "OutputTokenReserve": 2048,
  "EstimatedCharsPerToken": 4.0,
  "RetainRecentMessages": 8
}
```

`BuildContextAsync` checks the persisted short-term messages before retrieval.
If the message count or estimated input size is over budget, older messages are
compressed into medium-term summaries and the most recent messages are retained.
`GoldfishHarnessRunner` also applies the same estimated input budget while
building the request, trimming oldest short-term history when the caller
provides raw history without first using a memory manager.

## Skills, tool traces, and authorization

Dynamic skills are scoped to the active conversation instead of user profile
memory. Hosts can pass an `ISkillSessionStore` to `GoldfishHarnessRequest` so
skills loaded through `goldfish_load_skill` are associated with the current
tenant/user/agent/workspace/session partition and restored on the next run for
that same session. `SkillOptions.PersistLoadedSkills` is enabled by default.
Use `SqliteHarnessStateStore` when this state should survive process restarts.

Tool execution results are part of the current model loop only. They are not
stored as long-term user profile memory, and medium-term compression defaults to
`user` and `assistant` roles only. Hosts that need replay, debugging, or audit
can pass an `IToolExecutionStore`; the harness records the owning Turn,
run/session partition, tool id, success state, authorization decision,
timestamps, hashes, redacted arguments/results, `structuredContent`, and
`isError`. Raw credentials are never persisted.
`SqliteHarnessStateStore` implements both `ISkillSessionStore` and
`IToolExecutionStore`.

Sandbox or user-approval policy is injected through `IToolAuthorizationHook`.
The hook receives the run/session partition, tool id/name, and raw arguments
before execution and returns `Allow`, `Deny`, or `RequireApproval`. A denied or
approval-required call is returned as a normal tool result event, so the gateway
can surface an approval card and later steer or retry after the user grants
permission.

All system-level context is merged into the single leading `system` message.
The harness must not append additional `system` messages after user, assistant,
or tool messages.
