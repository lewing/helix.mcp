---
date: 2026-10-02
author: Ash
status: proposed
topic: hlx usage audit from local Copilot sessions
---

# hlx usage audit: offline snapshot gaps

Audited 372 hlx pain-point findings from 199 local Copilot sessions against current code, changelog, and recent commits #153-#156. Larry's "bundle = offline snapshot" directive is central: every direct AzDO/Helix/GitHub fallback in the findings becomes an offline hard failure unless `hlx collect azdo-build` collects the evidence into the cache snapshot first.

Current branch fixes the error/collector foundation: structured acquisition errors, `not_in_snapshot`, recorded provider failures, CLI list paging for several AzDO list commands, `hlx collect azdo-build`, manifest verification, credential-free replay, Retry-After handling, and NUL raw-log cache corruption detection. No current-code regression was verified for those fixes.

Still-open product gaps are concentrated in build-set discovery, AzDO artifact bytes/binlogs, test attachment/dump bytes, timeline/log/search cursors and selectors, cross-build history, and Helix bulk/file collection beyond selected globs. Recommended first PR groups: (A) build discovery + collector driver, (B) AzDO artifact/attachment byte downloads with caps, (C) timeline/search complete projections, (D) cross-build history, and (E) Helix bulk offline collection.

Full artifact: `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/hlx-audit/hlx-usage-audit.md`.
