### 2025-07-24: xUnit XML format support added to ParseTrxResultsAsync
**By:** Ripley
**What:** `ParseTrxResultsAsync` now auto-discovers xUnit XML test result files (`<assemblies>/<assembly>/<collection>/<test>`) alongside TRX files. When no `.trx` files exist, it falls back to `*.xml` with format detection. Strict parsing (XXE-safe) for `.trx`, best-effort parsing for `.xml` fallback.
**Why:** ASP.NET Core projects use `--logger xunit` which produces `TestResults.xml` in xUnit format, not `.trx`. Without this fallback, `hlx_test_results` failed on those work items with no useful error. The two-tier parsing strategy preserves the existing XXE security test while being resilient to non-test XML files in the fallback path.

### 2025-07-24: HelixException must be caught and translated to McpException in MCP tool handlers
**By:** Ripley
**What:** MCP tool handlers must catch `HelixException` from service calls and rethrow as `McpException`. Currently only `TestResults` does this; other tools have the same latent issue but aren't fixed yet.
**Why:** The MCP SDK only surfaces `McpException` messages to clients. All other exception types get wrapped as generic "An error occurred invoking '{tool}'" messages, which is what users see as issue #4.
