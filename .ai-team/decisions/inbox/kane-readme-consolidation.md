### 2025-02-13: Consolidate MCP config examples in README — one example + file path table

**By:** Kane
**What:** Replaced three duplicate MCP client config JSON blocks (VS Code, Claude Desktop, Claude Code/Cursor) with a single canonical example plus a table of config file locations and key names. Added `--yes` flag to all `dnx` args. Removed stale "not yet published to nuget.org" notes since v0.1.0 is live.
**Why:** The three JSON blocks were nearly identical — only the top-level key (`servers` vs `mcpServers`) and file path differed. Duplicating them made maintenance error-prone (changes had to be made in 3+ places) and made the README unnecessarily long. The consolidated format is easier to maintain and scan. The `--yes` flag is required for MCP server definitions because `dnx` runs non-interactively when launched by an MCP client. Pattern established: when configs differ only by file path and a single key name, use one example + a table rather than repeating the full block.
