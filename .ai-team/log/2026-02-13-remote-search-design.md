# Session: 2026-02-13-remote-search-design

**Requested by:** Larry Ewing

## Participants
- **Dallas** — Feature design (remote search/grep, structured TRX parsing)
- **Ash** — Security analysis (structured file parsing)

## Outcomes

### Feature Design (Dallas)
- Designed 2 new MCP tools: `hlx_search_file` (remote text search on uploaded artifacts) and `hlx_test_results` (structured TRX test result parsing)
- `hlx_search_file`: download-and-search pattern (proven by `SearchConsoleLogAsync`), substring matching only (no regex — ReDoS prevention), binary file detection via null-byte check, 50MB soft file size cap
- `hlx_test_results`: parse .trx XML into structured pass/fail/error data, auto-discover TRX files per work item, error message truncation (500 char default for LLM context budgets)
- Binlog parsing explicitly delegated to external `mcp-binlog-tool` — hlx role is discovery/download only
- 8 design decisions for Larry to review (file size limits, search_log deprecation, regex policy, binary detection, TRX file selection, content extraction scope, error truncation, binlog delegation)
- 4-phase implementation plan: Phase 1 (hlx_search_file, ~100 lines), Phase 2 (hlx_test_results, ~200 lines), Phase 3/4 deferred

### Security Analysis (Ash)
- 5 security considerations addressed: XXE injection, entity expansion DoS, TRX trust boundary, text search pattern safety, file size limits
- Concrete `XmlReaderSettings` recommendation: `DtdProcessing.Prohibit`, `XmlResolver = null`, `MaxCharactersFromEntities = 0`, `MaxCharactersInDocument = 50_000_000`
- TRX files assessed as same trust level as console logs — no new disclosure surface
- Text search: maintain no-regex invariant from threat model finding E2
- Binlog parsing: delegate to external tool — avoids heavy dependency and binary deserialization risk

### New Backlog Stories
- **US-31** — Remote file text search (`hlx_search_file` MCP + `hlx search-file` CLI), P2
- **US-32** — TRX test results parsing (`hlx_test_results` MCP + `hlx test-results` CLI), P2. Supersedes US-14 and structured portion of US-22.

### Decisions Pending
- 8 design decisions in `.ai-team/decisions/inbox/dallas-remote-search-design.md` awaiting Larry's review
