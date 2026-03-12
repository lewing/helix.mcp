### 2026-03-12: Rename MCP tool to helix_search
**By:** Ripley
**What:** Renamed the MCP-visible Helix text-search tool from `helix_search_log` to `helix_search` across the tool registration, CLI/MCP guidance, tests, and README while keeping the `SearchLog` method and `search-log` CLI command unchanged.
**Why:** The tool now searches both console logs and uploaded files, so the broader name improves agent discoverability and better reflects the actual behavior agents can rely on.
