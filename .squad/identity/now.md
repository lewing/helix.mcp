---
updated_at: 2026-08-26T18:29:40.518-05:00
focus_area: PR #127 final snapshot publication hardening; source/test head cd8e3ba and help-only head 59a4269 validated
status: approved
gate_status: Source/test head cd8e3ba approved after Christie test fix and independent Morse review; help-only head 59a4269 is green on Ubuntu, Windows, and Squad
---

# What We're Focused On

PR #127's original same-path parent-replacement finding was valid. At final source/test head `cd8e3ba44b3e62fa5f3dbf0b26f579112aeffeba`, the supported design requires a trusted destination-parent namespace on every platform. It anchors the parent before callbacks and detects cooperative retargeting, then runs the final callback, validates the actual serialized database, artifacts, exact layout, and sidecars, and immediately publishes with atomic no-overwrite semantics. Windows uses `MoveFileExW` with flags 0, Linux uses `renameat2(RENAME_NOREPLACE)`, and macOS uses `renamex_np(RENAME_EXCL)`. The validator rejects `-journal`, WAL, and SHM sidecars. Linux open flags are architecture-aware for x64, arm64, ARM, and PPC and fail closed for unknown architectures; linux-x64 and linux-arm64 cross-builds are clean.

**Gate Status:** Approved. Independent Morse approval followed Christie's test fix. The full local suite passed with 1,702 passed, 6 expected skips, and 0 failed, including the focused WAL checks. GitHub Actions Ubuntu, Windows, and Squad CI all passed at help-only head `59a4269048bfcbd9a5b4b3a8b0f892ddd025b875`.

**Next:** PR remains draft awaiting final Copilot re-review and human approval.

The following state-only commit changes no code or tests and does not invalidate validation for source/test head `cd8e3ba44b3e62fa5f3dbf0b26f579112aeffeba` or help-only head `59a4269048bfcbd9a5b4b3a8b0f892ddd025b875`.
