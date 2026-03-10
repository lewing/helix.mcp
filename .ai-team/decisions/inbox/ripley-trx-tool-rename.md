### 2026-03-10: Renamed helix_test_results to helix_parse_uploaded_trx
**By:** Ripley
**What:** Renamed MCP tool from helix_test_results to helix_parse_uploaded_trx and updated description to steer agents to azdo_test_results first
**Why:** The generic name was a context trap — agents reached for it first on every CI investigation even though 95%+ of dotnet repos publish results to AzDO, not as TRX files in Helix. Wasted tool calls on every investigation.
