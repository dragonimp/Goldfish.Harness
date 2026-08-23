# Goldfish.Harness

Goldfish.Harness is a standalone .NET 10 reasoning library for model-driven
ReAct execution, native function calling, streaming events, dynamic skills,
session queuing, layered SQLite vector memory, and a self-owned turn kernel.

The library is designed to be embedded by an agent runtime. It does not contain
gateway credentials, production configuration, or user data.

Skill loading, tool execution audit, and tool authorization are intentionally
kept outside user profile memory. Embedding hosts can provide session stores and
authorization hooks without changing the core memory admission policy. The
library includes a SQLite-backed harness state store for loaded skills and tool
execution audit hashes.

The turn kernel has explicit `Queued` / `Running` / terminal state and an
append-only event-store contract. The ACP host only projects these events; it
does not infer a successful turn from stream EOF.
