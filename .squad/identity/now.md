---
updated_at: 2026-08-27T15:04:23.241-05:00
focus_area: PR #127 snapshot publication hardening; validated source/test head f7053a6
status: approved
gate_status: Source/test head f7053a6 approved by Morse (Claude Opus 5) as a complete FIFO fix; clean local build 0 warnings/0 errors, full local suite 1,715 passed / 8 skipped / 0 failed, and fresh GitHub Actions run 33119989586 green on Ubuntu and Windows with Squad test check 33119989587 green
validated_source_head: f7053a6acbe8831b238e27b901981b1c058632fd
---

# What We're Focused On

PR #127's original same-path parent-replacement finding was valid. At validated source/test head `f7053a6acbe8831b238e27b901981b1c058632fd` (short `f7053a6`), the supported design requires a trusted destination-parent namespace on every platform. It anchors the parent before callbacks and detects cooperative retargeting, then runs the final callback, validates the actual serialized database, artifacts, exact layout, and sidecars, and immediately publishes with atomic no-overwrite semantics. Windows uses `MoveFileExW` with flags 0, Linux uses `renameat2(RENAME_NOREPLACE)`, and macOS uses `renamex_np(RENAME_EXCL)`. The validator rejects `-journal`, WAL, and SHM sidecars. Linux open flags are architecture-aware for x64, arm64, ARM, and PPC and fail closed for unknown architectures. Snapshot link checks are runtime portable.

`f7053a6` adds the FIFO fix. A FIFO planted at `cache.db` or at an artifact path could previously block the publishing process indefinitely on open, a denial of service. Snapshot files are now proven to be regular files by a nonblocking handle-first check: the path is opened without blocking, and the resulting handle is then proven to be a regular file with a single link before any content is read. Both the `cache.db` FIFO case and the artifact FIFO case are covered by bounded regression tests that fail fast instead of hanging.

**Gate Status:** Approved at `f7053a6`.

- Clean local build: 0 warnings, 0 errors.
- Full local suite: 1,715 passed, 8 skipped (expected platform skips), 0 failed.
- Fresh GitHub Actions run `33119989586`: Ubuntu passed, Windows passed.
- Squad test check `33119989587`: passed.
- Morse (Claude Opus 5) approved `f7053a6` as a complete FIFO fix.

The previously recorded head `fe875b1` and its 1,712-test total are superseded and are no longer the current validated source head or current test total. They remain accurate only as history for that earlier commit.

**Copilot review at current head:** Copilot thread `PRRT_kwDOROAS4c6c6tt0` is fixed in `f7053a6` and is awaiting its final reply and resolution, which happen after this status commit lands.

Two prior observations remain accurate and non-blocking.

1. **Memory amplification / `Array.MaxLength`.** Accepted as an accurate residual observation, and independently adjudicated non-blocking.
2. **Direct staged `BackupDatabase`.** Unsafe as proposed because it preserves the WAL header as 2/2. A bounded `VACUUM INTO` is the deferred follow-up design, not a change for this PR.

All earlier identity, callback, and hardlink threads are resolved.

**PR State:** PR #127 is open and no longer a draft, targeting `main`.

**Remaining Risks**

- The optional writable ReFS runtime probe may not execute in all environments.
- Non-x64 Linux syscall paths are build-verified and UAPI-verified, but not exercised on hardware here.
- The design depends on a trusted destination-parent boundary.
- Bounded-memory `VACUUM INTO` remains a follow-up.

This documentation-only update changes no source or tests. A later doc-only status commit does not invalidate the source/test validation recorded above for `f7053a6`; `validated_source_head` stays `f7053a6` until source or tests change again, and this status commit is not itself source-validated.
