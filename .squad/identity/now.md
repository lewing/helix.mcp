---
updated_at: 2026-08-26T18:29:40.518-05:00
focus_area: PR #127 final snapshot publication hardening; source/test head 8363dc3 validated
status: approved
gate_status: Source/test head 8363dc3 approved and validated by independent review, Ubuntu, Windows, Squad, and local checks
---

# What We're Focused On

PR #127's original same-path parent-replacement finding was valid. At final source/test head `8363dc36dab1702c7a5cb688a3fdbe7b2448ede1`, publication anchors the destination parent before callbacks, runs the final callback before Windows staging freeze and final validation of the actual serialized database, artifacts, exact tree, and sidecars, and permits no callback after validation. Windows uses handle-relative `NtSetInformationFile` native no-overwrite rename and link-safe cleanup. Linux uses `renameat2(RENAME_NOREPLACE)` and macOS uses `renamex_np(RENAME_EXCL)` under the documented trusted destination-parent namespace boundary.

**Gate Status:** Approved. Independent Morse approval is recorded. The full local suite passed with 1,697 passed, 6 expected skips, and 0 failed; mutation/export stress passed 225 runs and WAL stress passed 25 runs. GitHub Actions Ubuntu, Windows, and Squad CI all passed on `8363dc3`.

**Next:** PR remains draft awaiting final Copilot re-review and human approval.

The following docs-only commit changes no source or tests and does not invalidate validation for source/test head `8363dc36dab1702c7a5cb688a3fdbe7b2448ede1`.
