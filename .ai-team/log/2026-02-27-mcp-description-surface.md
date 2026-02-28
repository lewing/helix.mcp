# Session: 2026-02-27-mcp-description-surface

- **Requested by:** Larry Ewing
- **Agent:** Dallas (analysis), Scribe (logging)
- **Date:** 2026-02-27

## Summary

Dallas analyzed whether MCP tool `[Description]` attributes should expose local processing details (e.g., "parses TRX locally", "results are cached", "searches without downloading").

## Decision

**No** — local processing is an implementation detail. MCP descriptions describe **what/inputs/outputs**, not **how**.

### Key points

1. **Caching** — agents never need to know results are cached. No behavioral change. Omit.
2. **TRX parsing** — agents care about structured output, not parsing mechanism. Omit.
3. **Remote search** — "searches without downloading" is an optimization, not a contract. Omit.
4. **Failure classification** — `hlx_status` description should mention `failureCategory` as an output field. This is a **completeness fix**, not an implementation disclosure.
5. **URL resolution** — already correctly described in tool descriptions (input contract).

### Principle

Tool descriptions answer: What does this tool do? What inputs? What outputs?
They do NOT answer: How does it work internally? What makes it fast?

### Where "how" belongs

The README "How hlx Enhances the Helix API" section is the correct home for implementation detail documentation — it serves human readers evaluating the tool, not LLM agents invoking it.

## Actions

- Do NOT modify `[Description]` attributes to add implementation details
- DO fix `hlx_status` description to include `failureCategory` in the output field list
- README enhancements section covers the "how" for human readers
- This principle applies to all future MCP tools

## Cross-agent impact

- **Kane:** Resolves P1 question about whether to update MCP descriptions with local-enhancement flags. Answer: no.
