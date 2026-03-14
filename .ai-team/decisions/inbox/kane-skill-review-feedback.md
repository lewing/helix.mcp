# Kane — helix-cli skill review feedback

- Date: 2026-03-14
- Requested by: Larry Ewing
- Decision: Delete the static `helix-cli-reference.md` doc and keep `.github/skills/helix-cli/SKILL.md` lean, with discovery routed through `hlx llms-txt`, command `--help`, and inline jq field hints.
- Rationale: the static reference doc will go stale, duplicates information already discoverable from shipped surfaces, and incorrectly encouraged documenting unshipped CLI JSON for `hlx search-log`.
- Follow-up: track `hlx <command> --schema` as the long-term fix for per-command JSON field discovery; until then, the skill should only document shipped CLI behavior and point structured Helix log consumers to MCP `helix_search`.
