# Decisions

**Last updated:** 2026-05-30T16:50:11Z
**Merge cycle:** 2026-05-30T11:48:09-05:00 (Scribe archival + inbox merge)

---

## Active Decisions

### 2026-05-30T11:48:09-05:00: User directive — wire format changes for tool names
**By:** Larry (via Copilot)
**What:** General rule: don't worry too much about wire format changes (tool renames, alias additions) because we encourage agents to make semantic connections, not memorize tool names. Renaming `helix_status` → `helix_workitems` and similar discoverability fixes are fine; the cost is low.
**Why:** Discoverability of the right tool matters more than backward-compatible tool names. Validation: cross-check by searching dotnet org for hard-coded `hlx-*` / `helix_*` tool name references — if few/none, the rule is holding.

---

## Archive

See `archive/` for dated snapshots.

