---
date: 2026-10-02T13:45:00-05:00
author: Ripley
status: implemented
topic: PR 1 paging/cache-key deviations
---

# Deviations

No implementation deviations from Dallas's PR 1 paging/cache-key compatibility design.

Validation note: the dnceng-public test APIs used for live validation returned 302 under anonymous auth on this machine. Snapshot replay was therefore validated with an environment bearer token (`AZDO_TOKEN`/`AZDO_TOKEN_TYPE=bearer`) so live and eval mode used the same reproducible auth cache partition.
