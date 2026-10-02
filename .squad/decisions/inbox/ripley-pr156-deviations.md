---
date: 2026-10-02T16:00:00-05:00
author: Ripley
status: implemented-deviation
topic: PR156 review fixes
---

# PR156 review-fix deviations

1. The live `--test-scope all` validation for build 1621192 was started to exercise the derived `Failed` key, but was stopped after the AzDO test-results phase made no observable progress for more than 15 minutes. The implementation path is covered by focused collector tests and by required-attempt verification; the requested release-path live validation was completed with default test scope.

2. Live validation exposed an additional provider contract issue: AzDO rejects `NotRunnable` in the test-results `outcomes` query. The collector's `all` scope now omits that unsupported outcome while still deriving and verifying the default `Failed` cache key.

3. Snapshot validation was run with the supported `hlx snapshot validate <snapshot>` CLI form. The attempted `--json` flag is not supported by this command.
