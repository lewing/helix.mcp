---
updated_at: 2026-08-26T18:57:36-05:00
focus_area: PR #127 re-review reopened — macOS containment and regression coverage revisions required.
active_issues:
  - macOS case-sensitive spelling can bypass source-tree containment on a case-insensitive volume
  - exporter and validator negative-path coverage must be restored
  - deterministic database-corruption coverage is missing
---

# What We're Focused On

PR #127 is **not currently approved**. The previous revised head completed its gates successfully: 1,661 local tests passed with two pre-existing skips and no failures, and the refreshed Ubuntu, Windows, and Squad CI checks passed.

A fresh Copilot re-review reopened the gate. Frost must correct macOS case containment without weakening separator-aware checks or case-sensitive filesystem behavior. Hudson must restore the removed exporter and validator rejection scenarios, add deterministic corruption coverage, and ensure the case-only containment test runs meaningfully on macOS.

**Next:** Frost and Hudson complete their revisions, then Dallas reviews and approves the resulting artifacts. Only after that approval may the fresh full suite and fresh Ubuntu and Windows CI runs establish the final gate.
