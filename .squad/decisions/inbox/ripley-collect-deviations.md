---
date: 2026-10-02T14:15:00-05:00
author: Ripley
status: implemented-deviation
topic: PR2 hlx collect deviations
---

# `hlx collect azdo-build` implementation deviations

1. `--download-helix-files <glob>` is present, but v1 records matching uploaded files as explicit `skipped` attempts (`skip.kind=size_limit`) instead of downloading bytes. The current Helix file-list model exposes name/link but not a trusted byte length before `GetFileAsync`; downloading first would populate the cache before enforcing `--max-file-bytes`. Default behavior remains aligned with Larry's chosen default: no arbitrary Helix uploaded-file bytes are downloaded.

2. Live replay validation for dnceng-public build 1621192 required `AZDO_TOKEN`/`AZDO_TOKEN_TYPE=bearer` to collect and replay AzDO test-run data. Anonymous public Build/Timeline/Log calls replay, but AzDO test APIs returned auth/redirect behavior without a token. The manifest records `auth.azdo.replay=environment_token_required` and does not serialize credential material.
