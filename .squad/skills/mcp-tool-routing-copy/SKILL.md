---
name: "mcp-tool-routing-copy"
description: "Write MCP tool descriptions and failure messages that route callers to the right adjacent tools when workflows vary by repo, environment, or artifact availability."
domain: "mcp-server-design"
confidence: "medium"
source: "earned"
---

## Context
Use this when an MCP surface already has the needed tools, but callers still waste rounds because they cannot tell which tool to start with or what to do after a predictable failure. The goal is discoverability through copy, not a bigger tool surface.

## Patterns
- Lead with the behavioral contract: what the tool is for, and when it is the right first move.
- If the tool only works for a subset of repos/pipelines/artifact layouts, say that explicitly in the description.
- In failure messages, route to the next concrete tool sequence instead of stopping at “not found” or “unsupported”.
- Keep the routing concise: “use X when / otherwise use Y” beats long implementation detail.
- Mirror the same routing story across related surfaces such as tool descriptions, guide tools, and built-in help text.
- Add regression tests at the copy seam: reflect method-level `DescriptionAttribute` text for MCP tools, and assert guide/help section order so routing guidance stays ahead of deep detail.
- For false-confidence risks, test both the success-selection copy and the failure-path copy; a warning without the next tool sequence is not enough.

## Examples
- A structured-results tool says it parses Helix-hosted results when present, but directs callers to a build-system results API when those files are usually absent.
- A log-search tool says it is the remote-first path for console investigation and points callers to a guide tool for repo-specific search patterns.
- A repo guide adds a short “Start Here” section before deeper details so callers can choose the correct workflow quickly.
- A regression test reflects an MCP method description and asserts it still says “substring search, not regex” plus the repo-specific fallback/selection wording.
- A guide-rendering test verifies the “use AzDO results here” line appears before search-pattern inventories and that the recommended order pivots to structured results before log scraping.

## Anti-Patterns
- Warning that a tool “may fail” without naming the fallback tool or workflow
- Burying the workflow choice after long inventories or implementation notes
- Adding a composite tool before tightening the routing copy on the existing tools
- Letting descriptions, guide output, and built-in help drift into different stories
