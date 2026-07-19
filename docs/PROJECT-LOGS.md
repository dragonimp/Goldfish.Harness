# Project Log

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
