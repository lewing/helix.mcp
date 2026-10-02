---
date: 2026-10-02T14:45:00-05:00
author: Dallas
status: rejected
topic: PR2 hlx collect azdo-build gate review
reviewed_head: 7a36ac4
---

# Verdict: REJECT

Ripley's `hlx collect azdo-build` implementation is close and the reported full suite/live validation are strong, but PR2 is not mergeable until the scanner handoff contract is fixed. I accept the overall command shape, manifest/snapshot copy flow, exit-code model, retry kinds, concurrency bounds, secret redaction coverage, Windows-safe exporter baseline, and the scoped `--cache-dir`/`download --json` adjacent changes.

## Explicit rulings

1. **`--download-helix-files <glob>`:** choose option **(a)**. The explicit flag must actually download matching uploaded files in v1. Implement a collector/caching path that streams each selected file to a temp file while counting bytes, aborts and deletes the temp as soon as `--max-file-bytes` is exceeded, checks `--max-total-bytes` before accepting the file, and only then populates the Helix file cache key. Do not use an existing cache-populating `GetFileAsync` path until the cap is proven. Oversize files should be `skip.kind="size_limit"`; total-cap rejections should be `skip.kind="total_size_limit"`; successful small files should be `outcome="ok"` with bytes/hash and replayable from the exported snapshot.

2. **Auth-partitioned replay:** eval mode must not require live `AZDO_TOKEN` just to read an offline snapshot. The snapshot/manifest may record the non-secret auth partition id (`public` or `cache-xxxxxxxx`), never credential material. Eval startup should select the recorded AzDO partition without invoking auth, and all AzDO cache-key builders should use that selected hash. Safeguard against mixing partitions: a snapshot with multiple AzDO partitions must require an explicit partition selection or fail closed with a clear error; do not probe multiple partitions opportunistically. Add coverage for an auth-scoped collected snapshot replaying with no `AZDO_TOKEN`.

## Required fixes

1. `src/HelixTool/CollectCommands.cs:48,73` + `src/HelixTool.Core/Collect/AzdoBuildCollector.cs:577-587`: the CLI exposes `--download-helix-files`, but every selected file is recorded as a misleading `size_limit` skip. Replace this with the streaming cap behavior above and revise `AzdoBuildCollectorPr2Tests.SizeCapSkips...` so it proves both a small selected file is cached/replayed and an over-cap selected file is not cached.

2. `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs:186-207`, `src/HelixTool/Program.cs:51-56,1049-1055`, `src/HelixTool.Core/Collect/AzdoBuildCollector.cs:870-880`, and `src/HelixTool.Tests/Collect/AzdoBuildCollectorPr2Tests.cs:366-372`: eval replay is still environment-token-derived, and the collect test only exercises public keys. Add snapshot partition metadata/selection as ruled above, update stale messaging that says the same token is required, and add a regression where collection writes auth-scoped AzDO keys and `HLX_EVAL_SNAPSHOT` replay succeeds without `AZDO_TOKEN`.

3. `src/HelixTool.Core/Collect/AzdoBuildCollector.cs:603-614` with callers at `:105-145`: `--resume` verifies a cached prior attempt, appends `outcome="cached"`, then returns `default`. That drops cached `timeline`, `logsList`, `evidencePlan`, and test-run values needed to drive dependent phases, so a resumed run can exit 0 with most evidence absent from the new manifest. Resume must rehydrate the cached value through the same service/client path or otherwise preserve/re-evaluate the complete prior operation graph; add a test that a resumed complete run still contains the selected log, test, Helix work-item/log/files attempts and can export/replay them.

---

date: 2026-10-02T15:20:00-05:00
author: Dallas
status: rejected
topic: PR2 hlx collect azdo-build re-review
reviewed_head: 12d1b8a

# Re-review verdict: REJECT

Lambert's revision fixes the three implementation defects from the first gate, but HEAD is still not mergeable because required stale user-facing messaging remains in source. `src/HelixTool/SnapshotCommands.cs:48` still tells users auth-scoped entries must replay with an identical `AZDO_TOKEN` and that Azure CLI / `az` partitions are not reproducible in eval mode, which directly contradicts the new credential-free snapshot partition selector. `src/HelixTool/CollectCommands.cs:48` also still says `--download-helix-files` records selected files as unsupported skips, despite the flag now downloading in-cap files. Ripley and Lambert are both locked out, so escalate this final required fix to Larry.

Accepted on re-review:

1. `--download-helix-files` now streams selected uploads through a temp file, enforces per-file and total caps before cache publication, deletes temp files on cap breach, records `size_limit` / `total_size_limit`, and caches only in-cap files.
2. Eval replay now selects a non-secret AzDO cache partition from `HLX_EVAL_AZDO_PARTITION`, a single discovered snapshot partition, or the collect manifest, fails closed for multi-partition snapshots, and does not invoke live AzDO auth or serialize credentials. Live mode still partitions AzDO cache keys by resolved credential through `CachingAzdoApiClient.EnsureAuthTokenHashAsync`.
3. `--resume` now rehydrates cached prior attempts through the same service/client path instead of returning `default`, preserving downstream log/test/Helix coverage.

Validation: `DOTNET_ROLL_FORWARD=Major dotnet test src/HelixTool.Tests/HelixTool.Tests.csproj --no-restore --verbosity minimal` passed with 2222 passed / 9 skipped / 0 failed. A plain run without roll-forward still cannot start testhost on this machine because `Microsoft.NETCore.App 10.0.0` is not installed.

---
date: 2026-10-02T15:45:00-05:00
author: Dallas
status: approved
topic: PR2 hlx collect final text/doc gate
reviewed_head: c10caff

# Final verdict: APPROVE

Kane's HEAD fixes the only remaining stale help text from the 15:20 rejection. `src/HelixTool/CollectCommands.cs` now describes `--download-helix-files` as streaming selected uploads through byte caps, skipping over-cap files with `size_limit` / `total_size_limit`, and replaying in-cap files offline. `src/HelixTool/SnapshotCommands.cs` now states that snapshots record the non-secret AzDO partition, eval replay needs no credentials, automatic partition selection is used, and multi-partition snapshots require `HLX_EVAL_AZDO_PARTITION=public` or `cache-xxxxxxxx`.

I skimmed the added README, CLI reference, and CHANGELOG against the source contracts for flags/defaults, exit codes, manifest path, and `HLX_EVAL_AZDO_PARTITION`. The new `hlx collect azdo-build` documentation matches the implemented defaults (`maxConcurrency=6`, retry defaults, scopes, byte caps, `hlx-collect-manifest.json`, exported `manifest/hlx-collect-manifest.json`, exit `0/1/2`) and the credential-free eval partition selector. I did not find a PR-blocking factual error in the current `hlx collect` documentation. The older v0.10.2 changelog paragraph still documents the prior snapshot behavior historically; the new Unreleased entry supersedes it and does not block this PR.

No new validation was run in this final text-only gate; I relied on the reported full suite result of 2222 passed / 9 skipped / 0 warnings plus static review of `git log origin/main..HEAD`.
