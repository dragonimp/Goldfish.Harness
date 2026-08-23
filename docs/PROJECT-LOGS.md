# Project Log

## 2026-08-23

- Introduced the self-owned `GoldfishHarnessKernel` turn lifecycle and explicit terminal states.
- Separated history/memory assembly into `GoldfishHarnessContextAssembler`.
- Added an append-only turn-event store with JSONL persistence for the standalone ACP host.
- Preserved ACP as a projection layer and added kernel/ledger regression coverage.

## 2026-07-19

- Prepared the standalone project for its initial public GitHub release.
- Removed a private network address from the README and example configuration.
- Added project metadata and version `0.1.0`.
- Verified all 20 automated tests pass.
- Added session-scoped skill persistence interfaces.
- Added tool execution audit records and tool authorization hook support.
- Kept tool results out of default medium-term compression to avoid polluting memory.
- Updated project metadata to version `0.1.1`.
- Added SQLite harness state storage for session skills and tool execution audit hashes.
- Enforced a single leading system message across Runner and legacy PromptBuilder paths.
- Verified all 24 automated tests pass.

## 2026-07-22

- Added estimated-token context compression triggers for in-memory and SQLite memory managers.
- Added runner-side prompt budget trimming for callers that pass raw history without prebuilt memory context.
- Updated project metadata to version `0.1.2`.
- Verified all 27 automated tests pass.
- Added reasoning strategy design document for ReAct, Plan-and-Execute, ReWOO, and Reflexion.
- Started T24 P0 implementation with `ReasoningOptions`, strategy selection, leading-system prompt injection, and `ReasoningStrategySelected` events.
- Added reasoning strategy unit tests and saved the test report in `docs/REASONING-STRATEGY-TEST-REPORT.md`.
- Verified all 31 automated tests pass.
