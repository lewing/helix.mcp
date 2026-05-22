---
name: "mcp-enum-with-aliases"
description: "Add MCP enum presets while accepting schema-invisible aliases in validation logic."
domain: "mcp-server-design"
confidence: "medium"
source: "earned"
---

## Context
Use this when an MCP tool should expose a small, canonical enum in `AllowedValues`, but callers may realistically send adjacent platform spellings or natural-language synonyms.

## Patterns
- Keep `AllowedValues` limited to the canonical names you want in the schema.
- Normalize silent aliases before validation so confused callers succeed without bloating the public enum.
- Share canonical values, alias normalization, predicate logic, and invalid-value text from one helper so tool and service layers cannot drift.
- Keep error messages canonical-only: list the official values, not the hidden aliases.
- If a new preset expands selection beyond the legacy data-shape assumptions, preserve the existing contract first (for example, use an empty string sentinel instead of widening the model immediately).

## Examples
- Accept `inProgress`, `in-progress`, and `active` as silent aliases for canonical `running`.
- Accept `notStarted` and `not-started` as silent aliases for canonical `pending`.
- Validate after normalization, then run one shared predicate helper across all call sites.

## Anti-Patterns
- Listing both canonical values and aliases in `AllowedValues`, forcing callers to choose between synonyms.
- Duplicating switch logic in each MCP tool or service method.
- Returning alias names in error text, which makes the public contract look larger than it is.
