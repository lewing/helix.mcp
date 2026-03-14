### 2026-03-14: helix-cli docs must describe shipped CLI behavior, not aspirational parity
**By:** Kane
**What:** The new `helix-cli` skill and reference doc explicitly route CLI-only repo guidance through `hlx llms-txt`, call out that there is no `hlx ci-guide` command yet, and document `hlx search-log` as text-only today while still capturing its underlying structured field shape separately.
**Why:** Agents need trustworthy docs more than symmetry. Teaching a nonexistent CLI command or implying JSON support where the shipped CLI only prints text would create broken workflows, while the explicit parity notes still preserve the intended discovery path and make future CLI/MCP convergence easy to document.
