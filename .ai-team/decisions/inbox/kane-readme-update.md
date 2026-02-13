### 2026-02-15: README v0.1.3 comprehensive update

**By:** Kane
**Requested by:** Larry Ewing

---

**What changed:**
- MCP Tools table expanded from 9 → 12 tools (full inventory from HelixMcpTools.cs)
- CLI Commands reference table added (15 commands with exact signatures)
- Security section added (XML parsing, path traversal, URL validation, file search toggle, batch limits)
- Authentication updated with HLX_API_KEY documentation
- Test count updated 298 → 340
- Quick Start examples updated for current CLI signatures

**Note for Ripley:** The llmstxt output in Program.cs is now out of sync with the README — it's missing `hlx_search_file`, `hlx_test_results`, and the `search-file`/`test-results` CLI commands. It should be updated to match.
