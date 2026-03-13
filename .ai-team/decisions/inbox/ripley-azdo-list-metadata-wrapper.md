# Ripley decision note: AzDO list metadata wrapper

## Context
User feedback from steveisok showed agents were hitting list/search limits and treating capped results as complete.

## Decision
For MCP-only list-style AzDO tools that previously returned raw lists (`azdo_builds`, `azdo_artifacts`, `azdo_test_results`, `azdo_test_attachments`), wrap results in a `LimitedResults<T>` type that:
- serializes as an object with `results`, `truncated`, and optional `note`
- still implements `IReadOnlyList<T>` so direct C# callers/tests keep list ergonomics

## Why
A plain top-level object gives agents an explicit truncation signal, but changing to a non-list type would ripple through existing tests and direct method consumers. The custom JSON-converter wrapper keeps runtime/tool output richer while preserving local call-site compatibility.

## Follow-up
If more MCP tools need truncation metadata, prefer reusing this wrapper pattern rather than inventing per-tool shapes.
