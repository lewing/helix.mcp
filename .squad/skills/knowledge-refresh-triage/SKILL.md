---
name: "knowledge-refresh-triage"
description: "Refresh a project knowledgebase after code/doc updates by separating resolved findings from remaining follow-up work and durable facts."
domain: "analysis"
confidence: "low"
source: "earned"
---

## Context

Use this when a plan, backlog, or knowledgebase contains earlier findings that may now be stale because code, tests, or documentation changed. The goal is to avoid carrying resolved issues forward as if they were still open.

## Patterns

1. Read the **updated implementation, tests, and docs together** before changing the knowledge artifact.
2. Classify refreshed knowledge into three buckets:
   - **Fixed findings** — issues that are now demonstrably resolved
   - **Still-open follow-up opportunities** — real remaining work or product questions
   - **Durable knowledge worth retaining** — conventions, positioning, or architecture facts likely to stay true
3. Anchor each conclusion to specific files so future readers can re-verify it quickly.
4. Prefer updating the **active knowledge artifact** already in use for the workstream (session plan, backlog doc, etc.) instead of scattering refresh notes across new files.
5. If an earlier finding is fixed, explicitly say it should be treated as **resolved, not backlog**.

## Examples

- A README review item becomes fixed after docs are rewritten and the knowledgebase is updated to move that item from backlog to resolved findings.
- A security hardening note is kept as durable knowledge only after code and regression tests both confirm the new behavior.
- A planning document retains a note that old inventory tables are stale while moving the current truth to a refreshed section at the top.

## Anti-Patterns

- Updating a backlog from code alone without checking whether tests and docs also reflect the change
- Mixing resolved findings and future work in one undifferentiated bullet list
- Creating a brand-new notes file when an active plan or knowledge artifact already exists
- Treating stale historical analysis as current truth without a refresh pass
