### 2026-03-08: AzDO documentation uses subsections within existing README structure
**By:** Kane
**What:** AzDO tools are documented as a `### AzDO Tools` subsection under `## MCP Tools`, AzDO auth as `### Azure DevOps` under `## Authentication`, and AzDO TTLs as inline additions under `## Caching` — rather than creating separate top-level sections.
**Why:** Keeps the README scannable and reinforces that Helix and AzDO are parts of the same tool. The MCP Configuration section needed no changes because the same MCP server serves both tool sets. This pattern should be followed for any future API domains added to hlx.

### 2026-03-08: llmstxt updated with AzDO tools under a separate "AzDO MCP Tools" subsection
**By:** Kane
**What:** The `llmstxt` command output now includes all 9 AzDO MCP tools in a dedicated subsection, plus AzDO auth chain and AzDO-specific caching TTLs.
**Why:** LLM agents reading `llmstxt` need to know about AzDO tools to use them. Keeping Helix and AzDO tool lists visually separated makes it clear which tools work with which system.
