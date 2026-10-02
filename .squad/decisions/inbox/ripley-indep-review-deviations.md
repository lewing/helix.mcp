---
date: 2026-10-02T16:45:00-05:00
author: Ripley
status: implemented-deviation
topic: Independent pre-release review fixes (findings 1–7)
---

# Independent review fix deviations

Scope: `independent-review.md` findings 1–7 (finding #8, the committed manifest, was already
fixed by the coordinator before this session started). Dallas's separate `--test-scope all`
attachment-fan-out performance gate (PR156 review) is out of scope here and unaffected by these
changes other than sharing the same outcome-list literal.

1. **Finding #1 (transport failures escaping as raw exceptions).** Added a shared
   `ReadResponseBodyAsync` helper in `AzdoApiClient` used by `GetAsync`, `GetListAsync`, and
   `ThrowOnUnexpectedError`; classifies `HttpRequestException`/`IOException` as `transport_error`
   and non-caller `TaskCanceledException` as `timeout`. Did the same inline for
   `GetBuildLogAsync`'s stream read. Added an `IOException` catch to
   `HelixService.GetConsoleLogContentAsync` mapped via the existing `HelixAcquisition.Transport`
   helper. Added a last-resort `catch (Exception)` backstop in `AzdoBuildCollector.RunAsync` and in
   the Helix file-download loop, synthesizing a `transport_error` and recording a failed attempt
   so the manifest is always written. Added a matching last-resort catch in `CollectCommands.cs`
   so the CLI itself never crashes with a bare stack trace.

2. **Finding #2 (Helix failures not recorded).** Classified every `HelixApiClient` method
   (`GetJobDetailsAsync`, `ListWorkItemsAsync`, `GetWorkItemDetailsAsync`,
   `ListWorkItemFilesAsync`, `GetConsoleLogAsync`, `GetFileAsync`) through a shared `ClassifyAsync`
   helper, reusing `ListJobsByBuildAsync`'s existing pattern. Deviation: discovered during live
   testing (via a throwaway `/tmp` probe program referencing `HelixTool.Core`, matching the
   review's own probe methodology) that Azure.Core's pipeline can wrap a raw transport
   `HttpRequestException` as `Azure.RequestFailedException` (not the Helix SDK's own
   `RestApiException`) with `Status == 0`; added `HelixAcquisition.FromRequestFailed` which falls
   back to `(ex.InnerException as HttpRequestException)?.StatusCode` when `ex.Status` is 0. This
   was not called out in the review text but is required for the classification to actually be
   complete at the client boundary. Tell Lambert: the existing `RecordingHelixApiClient` test fake
   in `AzdoBuildCollectorPr2Tests.cs` throws `HlxAcquisitionException` directly and does not
   exercise this real-SDK-exception gap; Lambert's new `IndependentReviewRegressionTests.cs`
   already covers the real client with a live `HelixApi` + fake `HttpMessageHandler`, which is the
   right pattern going forward for this class of bug.

3. **Finding #3 (export leaks whole shared cache).** Chose "isolated per-run temp cache dir"
   over "refuse the combination": `collect azdo-build --export <dir>` without `--cache-dir` now
   collects into `{TempPath}/hlx-collect-cache/{guid}` and records that resolved path in
   `manifest.cache.root` / `command.options.cacheDir`. Verified live against build 1621192: the
   resulting snapshot's `cache.db` contains only `azdo:7af1ee30:...:1621192` keys — no other
   build IDs, no other auth-partition hash.

4. **Finding #4 (snapshot validation failure leaves complete:true).** Implemented exactly as
   described: `!validation.IsValid` now sets `Complete = false` and appends a
   `snapshot_validation_failed` incomplete detail. Needed to add a
   `progress?.Invoke("Validating exported snapshot...")` call immediately before
   `SnapshotValidator.ValidateAsync` to match Lambert's `SnapshotValidationFailure_FailsClosedInBothManifests_IndepReview4`
   test, which hooks that exact progress message to corrupt the exported artifact between
   publication and validation.

5. **Finding #5 (`--test-scope all` outcome list incomplete).** Replaced the outcome list with
   the full `TestOutcome` enum (verified against Microsoft Learn's `TestOutcome` enum page since
   this environment has no AzDO credential to probe live), excluding `NotRunnable` per
   Ripley/Dallas's PR156 finding that AzDO rejects it as an unsupported outcome filter value even
   though it isn't a real `TestOutcome` member and some docs/tools list it anyway.

6. **Finding #6 (optional downloads can evict required evidence).** Chose "clamp optional
   budget to remaining cache capacity" over "suspend LRU eviction": the Helix file download
   budget is now `min(--max-total-bytes, cacheCap - requiredBytesAlreadyCollectedThisRun)`. This
   is a conservative reserve computed once per `CollectHelixSuggestedFetchesAsync` call from
   already-recorded required attempts; it does not account for required bytes collected
   concurrently within the same suggested-fetch batch (work-item/console-log metadata fetched
   alongside file listings for *other* fetches in the same `ForEachCollectAsync` pass), which is
   an acceptable residual risk given `MaxConcurrency` defaults to 6 and those entries are small
   relative to file downloads.

7. **Finding #7 (TTL-filtered verification).** Added `ICacheStore.GetMetadataIgnoringTtlAsync`
   as a C# default-interface method (falls back to the existing TTL-respecting
   `GetMetadataAsync` for any implementer that doesn't override it) so the test fake in
   `ApiKeyScopedRequestIsolationTests.cs` did not need changes. `SqliteCacheStore` overrides it
   with a true TTL-bypassing query. `VerifyCacheEvidenceAsync` now uses it for metadata presence
   checks. Note: `GetArtifactAsync` (binary artifacts) was already TTL-unfiltered at read time —
   only `GetMetadataAsync` had the bug the review described, so the artifact branch of
   `VerifyCacheEvidenceAsync` was left unchanged.

**Also (manifest-in-CWD):** Chose "default next to the cache/export dir" over "require
`--manifest`". `ResolveManifestPath` now defaults to `{EffectiveCacheRoot}/hlx-collect-manifest.json`
instead of `Directory.GetCurrentDirectory()`.

## Test file note (for Lambert, not fixed by me)

While iterating on finding #4, `SnapshotValidationFailure_FailsClosedInBothManifests_IndepReview4`
transiently failed with `Assert.Single() Failure: The collection was empty` before I had added the
`progress?.Invoke("Validating exported snapshot...")` call that the test hooks to corrupt the
published artifact between export and validation. Once that call was added in the right place
(immediately before `SnapshotValidator.ValidateAsync`), the test passes reliably (verified 3x in
isolation plus in the full suite) — no residual issue; noted here only in case the same symptom
resurfaces for a different reason.
