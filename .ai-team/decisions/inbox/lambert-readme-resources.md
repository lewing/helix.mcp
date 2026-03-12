### README: Document MCP Resources and Idempotent annotations

**By:** Lambert
**Requested by:** Larry Ewing

**Changes:**

1. **Added "## MCP Resources" section** after the MCP Tools section. Lists the two `ci://` resources (`ci://profiles` and `ci://profiles/{repo}`) in a table matching the tools table style. One-sentence explainer of resources vs tools, plus a note that these mirror `helix_ci_guide` as client-discoverable resources.

2. **Added "Idempotent annotations" row** to the Context-Efficient Design table. This documents the `Idempotent = true` annotation sweep (all read-only tools) as a design technique — it fits the table's theme of client-side optimization and complements the existing entries about reducing token waste.

**Judgment call on Idempotent:** Included it. The Context-Efficient Design table is about design choices that help clients work better — idempotent annotations let clients retry and cache tool calls safely, which is the same class of optimization as tail limits and failure-first defaults. One row, no jargon.
