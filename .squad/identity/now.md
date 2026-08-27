---
updated_at: 2026-08-27T12:29:29.574-05:00
focus_area: PR #127 snapshot publication hardening; validated source/test head fe875b1
status: approved
gate_status: Source/test head fe875b1 approved by Vickers (GPT-5.6 Sol) and Morse (Claude Opus 5, holistic); clean local rebuild green and GitHub Actions rerun 33095792077 green on Ubuntu, Windows, and Squad
validated_source_head: fe875b1cb93e36eedc9ba9febafcfbb74ef98b58
---

# What We're Focused On

PR #127's original same-path parent-replacement finding was valid. At validated source/test head `fe875b1cb93e36eedc9ba9febafcfbb74ef98b58` (short `fe875b1`), the supported design requires a trusted destination-parent namespace on every platform. It anchors the parent before callbacks and detects cooperative retargeting, then runs the final callback, validates the actual serialized database, artifacts, exact layout, and sidecars, and immediately publishes with atomic no-overwrite semantics. Windows uses `MoveFileExW` with flags 0, Linux uses `renameat2(RENAME_NOREPLACE)`, and macOS uses `renamex_np(RENAME_EXCL)`. The validator rejects `-journal`, WAL, and SHM sidecars. Linux open flags are architecture-aware for x64, arm64, ARM, and PPC and fail closed for unknown architectures. Snapshot link checks are runtime portable.

**Gate Status:** Approved at `fe875b1`.

- Clean local rebuild: 1,712 passed, 8 expected platform skips, 0 failed, 0 warnings, 0 errors.
- Fresh GitHub Actions rerun `33095792077`: Ubuntu passed, Windows passed, Squad test check passed.
- Vickers (GPT-5.6 Sol) approved at `fe875b1`.
- Morse (Claude Opus 5) gave holistic approval at `fe875b1`.

**Copilot review at current head:** Two open threads remain.

1. **Memory amplification / `Array.MaxLength`.** Accepted as an accurate residual observation, and independently adjudicated non-blocking.
2. **Direct staged `BackupDatabase`.** Unsafe as proposed because it preserves the WAL header as 2/2. A bounded `VACUUM INTO` is the deferred follow-up design, not a change for this PR.

The stale-`now.md` thread is addressed by this documentation update. All earlier identity, callback, and hardlink threads are resolved.

**PR State:** PR #127 is open and no longer a draft, targeting `main`.

**Remaining Risks**

- The optional writable ReFS runtime probe may not execute in all environments.
- Non-x64 Linux syscall paths are build-verified and UAPI-verified, but not exercised on hardware here.
- The design depends on a trusted destination-parent boundary.
- Bounded-memory `VACUUM INTO` remains a follow-up.

This documentation-only update changes no source or tests. Doc-only status changes do not invalidate the source/test validation recorded above for `validated_source_head`, and this commit is not itself source-validated.
