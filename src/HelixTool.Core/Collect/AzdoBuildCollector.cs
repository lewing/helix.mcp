using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Core.Paging;

namespace HelixTool.Core.Collect;

public sealed class AzdoBuildCollector
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new AcquisitionErrorKindJsonConverter() }
    };
    private static readonly TimeSpan RecordedFailureTtl = TimeSpan.FromHours(4);
    private static readonly TimeSpan TestMetadataTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterSafetyCeiling = TimeSpan.FromHours(1);
    private static readonly HashSet<string> s_diagnosticTestOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Failed", "Error", "Timeout", "Aborted", "Inconclusive", "Blocked", "Warning"
    };

    private readonly AzdoService _azdoService;
    private readonly IAzdoApiClient _azdoClient;
    private readonly HelixService _helixService;
    private readonly IHelixApiClient _helixClient;
    private readonly IAzdoTokenAccessor _azdoTokenAccessor;
    private readonly IHelixTokenAccessor _helixTokenAccessor;
    private readonly CacheOptions _cacheOptions;
    private readonly ICacheStore _cacheStore;

    public AzdoBuildCollector(
        AzdoService azdoService,
        IAzdoApiClient azdoClient,
        HelixService helixService,
        IHelixApiClient helixClient,
        IAzdoTokenAccessor azdoTokenAccessor,
        IHelixTokenAccessor helixTokenAccessor,
        CacheOptions cacheOptions,
        ICacheStore cacheStore)
    {
        _azdoService = azdoService;
        _azdoClient = azdoClient;
        _helixService = helixService;
        _helixClient = helixClient;
        _azdoTokenAccessor = azdoTokenAccessor;
        _helixTokenAccessor = helixTokenAccessor;
        _cacheOptions = cacheOptions;
        _cacheStore = cacheStore;
    }

    public async Task<CollectResult> CollectAzdoBuildAsync(
        CollectPolicy policy,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePolicy(policy);
        if (_cacheOptions.MaxSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                "Collection requires caching, but caching is disabled (HLX_CACHE_MAX_SIZE_MB=0). " +
                "Use a positive cache size and an isolated --cache-dir before running collect.");
        }
        await ResolveAzdoAuthContextAsync(ct);

        var startedAt = DateTimeOffset.UtcNow;
        var manifestPath = ResolveManifestPath(policy);
        var priorAttempts = await LoadPriorAttemptsAsync(policy, manifestPath, ct);
        var attempts = new List<CollectFetchAttempt>();
        var incomplete = new List<CollectIncompleteDetail>();
        var (org, project, buildId) = AzdoIdResolver.Resolve(policy.BuildIdOrUrl);
        var buildUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_build/results?buildId={buildId}";

        var helixAuth = GetHelixAuthInfo();
        var source = new CollectSourceInfo
        {
            Org = org,
            Project = project,
            BuildId = buildId,
            BuildUrl = buildUrl
        };

        AzdoBuildSummary? buildSummary = null;
        AzdoTimeline? timeline = null;
        IReadOnlyList<AzdoBuildLogEntry> logsList = [];
        AzdoEvidencePlan? evidencePlan = null;

        progress?.Invoke("Collecting root AzDO evidence...");
        buildSummary = await RunAsync(
            attempts,
            priorAttempts,
            CreateAttempt("azdo.build", "azdo_root", true, "azdo", "get_build",
                Resource(("org", org), ("project", project), ("buildId", buildId)),
                AzdoCacheKeys.MetadataKey(_cacheOptions, org, project, $"build:{buildId}")),
            policy,
            async token => await _azdoService.GetBuildSummaryAsync(policy.BuildIdOrUrl, token),
            metadata: true,
            ct);
        if (buildSummary is not null)
        {
            source = source with
            {
                DefinitionId = buildSummary.DefinitionId,
                DefinitionName = buildSummary.DefinitionName,
                Status = buildSummary.Status,
                Result = buildSummary.Result,
                SourceBranch = buildSummary.SourceBranch,
                SourceVersion = buildSummary.SourceVersion
            };
        }

        timeline = await RunAsync(
            attempts,
            priorAttempts,
            CreateAttempt("azdo.timeline", "azdo_root", true, "azdo", "get_timeline",
                Resource(("org", org), ("project", project), ("buildId", buildId)),
                AzdoCacheKeys.MetadataKey(_cacheOptions, org, project, $"timeline:{buildId}")),
            policy,
            async token => await _azdoService.GetTimelineAsync(policy.BuildIdOrUrl, token),
            metadata: true,
            ct);

        await RunAsync(
            attempts,
            priorAttempts,
            CreateAttempt("azdo.artifacts", "azdo_root", true, "azdo", "list_artifacts",
                Resource(("org", org), ("project", project), ("buildId", buildId)),
                AzdoListCacheKeys.ArtifactsComplete(_cacheOptions, org, project, buildId)),
            policy,
            async token => await _azdoService.GetBuildArtifactsPageAsync(
                policy.BuildIdOrUrl,
                policy.ArtifactPattern,
                new HlxPageRequest { All = true, Offset = 0, Limit = null },
                ct: token),
            metadata: true,
            ct: ct,
            pagingSelector: value => value is HlxListEnvelope<AzdoBuildArtifact> envelope
                ? PagingFromEnvelope(envelope)
                : null);

        logsList = await RunAsync(
            attempts,
            priorAttempts,
            CreateAttempt("azdo.logs-list", "azdo_root", true, "azdo", "list_build_logs",
                Resource(("org", org), ("project", project), ("buildId", buildId)),
                AzdoCacheKeys.MetadataKey(_cacheOptions, org, project, $"logslist:{buildId}")),
            policy,
            async token => await _azdoClient.GetBuildLogsListAsync(org, project, buildId, token),
            metadata: true,
            ct) ?? [];

        evidencePlan = await CollectEvidencePlanPagesAsync(
            attempts,
            priorAttempts,
            incomplete,
            policy,
            org,
            project,
            buildId,
            ct);

        if (evidencePlan is not null)
        {
            foreach (var detail in evidencePlan.IncompleteDetails)
            {
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = detail.Code,
                    Message = detail.Message,
                    Operation = "azdo_evidence_plan",
                    Resource = Resource(("buildId", buildId), ("jobId", detail.JobId), ("jobName", detail.JobName))
                });
            }
        }

        if (timeline is not null)
        {
            progress?.Invoke("Collecting selected AzDO logs...");
            await CollectLogsAsync(attempts, priorAttempts, policy, org, project, buildId, timeline, logsList, evidencePlan, ct);
        }
        else if (IsNone(policy.LogScope))
        {
            AddSkip(attempts, "azdo.logs", "azdo_logs", true, "azdo", "get_build_log",
                Resource(("buildId", buildId)), "not_selected", "AzDO log collection was disabled by --log-scope none.");
        }

        progress?.Invoke("Collecting selected AzDO tests...");
        await CollectTestsAsync(attempts, priorAttempts, policy, org, project, buildId, progress, ct);

        if (evidencePlan is not null)
        {
            progress?.Invoke("Collecting Helix suggested fetches...");
            await CollectHelixSuggestedFetchesAsync(attempts, priorAttempts, policy, evidencePlan, ct);
        }

        attempts = await VerifyCollectedEvidenceAsync(attempts, ct);
        attempts = attempts.OrderBy(a => a.Phase, StringComparer.Ordinal)
            .ThenBy(a => a.ParentId, StringComparer.Ordinal)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        var requiredAttempts = attempts.Where(a => a.Required).ToList();
        foreach (var attempt in requiredAttempts)
        {
            if (attempt.Outcome is "failed" or "recorded_failure")
            {
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = attempt.Error?.Kind == AcquisitionErrorKind.NotInSnapshot &&
                           IsArtifactOperation(attempt.Operation)
                        ? "artifact_missing"
                        : "fetch_failed",
                    Message = attempt.Error?.Message ?? $"{attempt.Operation} failed.",
                    Operation = attempt.Operation,
                    Resource = attempt.Resource
                });
            }
            else if (attempt.Outcome == "skipped" && attempt.Skip?.Kind != "policy_excluded")
            {
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = attempt.Skip?.Kind is "test_result_limit" or "test_attachment_limit"
                        ? attempt.Skip.Kind
                        : "fetch_skipped",
                    Message = attempt.Skip?.Message ?? $"{attempt.Operation} skipped.",
                    Operation = attempt.Operation,
                    Resource = attempt.Resource
                });
            }
        }

        var complete = incomplete.Count == 0;
        var exitCode = complete || (policy.AllowIncomplete && incomplete.All(IsPolicyAllowedIncomplete)) ? 0 : 2;
        var azdoAuth = await GetAzdoAuthInfoAsync(ct);
        var manifest = BuildManifest(
            policy,
            startedAt,
            source,
            azdoAuth,
            helixAuth,
            attempts,
            incomplete,
            complete,
            exitCode,
            snapshot: new CollectSnapshotInfo());

        await WriteManifestAsync(manifestPath, manifest, ct);

        string? snapshotPath = null;
        if (!string.IsNullOrWhiteSpace(policy.ExportPath))
        {
            progress?.Invoke("Exporting snapshot...");
            snapshotPath = Path.GetFullPath(policy.ExportPath);
            try
            {
                var export = await SnapshotExporter.ExportAsync(
                    _cacheOptions.GetEffectiveCacheRoot(),
                    snapshotPath,
                    progress,
                    ct);
                var manifestRelativePath = Path.Combine("manifest", "hlx-collect-manifest.json");
                var snapshotManifestPath = Path.Combine(export.Destination, manifestRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotManifestPath)!);
                File.Copy(manifestPath, snapshotManifestPath, overwrite: false);
                progress?.Invoke("Validating exported snapshot...");
                var validation = await SnapshotValidator.ValidateAsync(export.Destination, ct);
                if (!validation.IsValid)
                {
                    incomplete.Add(new CollectIncompleteDetail
                    {
                        Code = "snapshot_validation_failed",
                        Message = validation.Errors.Count > 0
                            ? string.Join("; ", validation.Errors)
                            : "Exported snapshot failed validation.",
                        Operation = "snapshot_validate",
                        Resource = Resource(("path", export.Destination))
                    });
                }
                manifest = manifest with
                {
                    Complete = complete && validation.IsValid,
                    IncompleteDetails = incomplete,
                    Snapshot = new CollectSnapshotInfo
                    {
                        Exported = true,
                        Path = export.Destination,
                        ManifestPath = manifestRelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                        Validated = validation.IsValid,
                        ValidationErrors = validation.Errors,
                        ValidationWarnings = validation.Warnings
                    },
                    ExitCode = validation.IsValid ? exitCode : 1
                };
                await WriteManifestAsync(manifestPath, manifest, ct);
                File.Copy(manifestPath, snapshotManifestPath, overwrite: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = "snapshot_export_failed",
                    Message = ex.Message,
                    Operation = "snapshot_export",
                    Resource = Resource(("path", snapshotPath))
                });
                manifest = manifest with
                {
                    Complete = false,
                    ExitCode = 1,
                    IncompleteDetails = incomplete,
                    Snapshot = new CollectSnapshotInfo
                    {
                        Exported = false,
                        Path = snapshotPath,
                        Validated = false,
                        ValidationErrors = [ex.Message]
                    }
                };
                await WriteManifestAsync(manifestPath, manifest, ct);
            }
        }

        return new CollectResult(manifest, manifestPath, snapshotPath);
    }

    private async Task<AzdoEvidencePlan?> CollectEvidencePlanPagesAsync(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        List<CollectIncompleteDetail> incomplete,
        CollectPolicy policy,
        string org,
        string project,
        int buildId,
        CancellationToken ct)
    {
        var allHelixFailures = new List<AzdoHelixEvidenceFailure>();
        AzdoEvidencePlan? merged = null;
        var offset = 0;

        do
        {
            var currentOffset = offset;
            var attemptId = $"azdo.evidence-plan:{currentOffset}";
            var plan = await RunAsync(
                attempts,
                priorAttempts,
                CreateAttempt(attemptId, "azdo.evidence-plan", true, "azdo", "azdo_evidence_plan",
                    Resource(("org", org), ("project", project), ("buildId", buildId), ("helixFailureOffset", currentOffset)),
                    null),
                policy,
                async token => await _azdoService.GetEvidencePlanAsync(
                    policy.BuildIdOrUrl,
                    new AzdoEvidencePlanOptions
                    {
                        ArtifactPattern = policy.ArtifactPattern,
                        ArtifactJobPrefix = policy.ArtifactJobPrefix,
                        StripAttemptPrefix = !policy.KeepAttemptPrefix,
                        Match = policy.Match,
                        JobResults = ParseCsv(policy.JobResults, ["failed", "canceled"]),
                        HelixFailureOffset = currentOffset,
                        HelixFailureLimit = AzdoEvidencePlan.MaxHelixFailureLimit
                    },
                    token),
                metadata: true,
                ct: ct,
                pagingSelector: value => value is AzdoEvidencePlan planValue
                    ? new CollectPagingInfo
                    {
                        Returned = planValue.HelixFailures.Count,
                        Total = planValue.HelixFailureTotal,
                        Offset = planValue.HelixFailureOffset,
                        Limit = planValue.HelixFailureLimit,
                        Complete = !planValue.HelixFailuresTruncated,
                        Truncated = planValue.HelixFailuresTruncated,
                        Next = planValue.HelixFailuresTruncated
                            ? new { offset = planValue.HelixFailureOffset + planValue.HelixFailures.Count, limit = planValue.HelixFailureLimit }
                            : null
                    }
                    : null);

            if (plan is null)
                break;

            var currentPlan = plan;
            merged ??= currentPlan;
            allHelixFailures.AddRange(currentPlan.HelixFailures);
            offset += currentPlan.HelixFailures.Count;
            if (currentPlan.HelixFailures.Count == 0 || allHelixFailures.Count >= currentPlan.HelixFailureTotal)
            {
                var mergedDetails = currentPlan.IncompleteDetails
                    .Where(detail => !string.Equals(detail.Code, "helix_failures_truncated", StringComparison.Ordinal))
                    .ToList();
                var mergedReasons = currentPlan.IncompleteReasons
                    .Where(reason => !reason.Contains("Helix failure", StringComparison.OrdinalIgnoreCase) ||
                                     !reason.Contains("truncated", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                merged = currentPlan with
                {
                    HelixFailures = allHelixFailures,
                    HelixFailureOffset = 0,
                    HelixFailuresTruncated = allHelixFailures.Count < currentPlan.HelixFailureTotal,
                    IncompleteDetails = allHelixFailures.Count >= currentPlan.HelixFailureTotal
                        ? mergedDetails
                        : currentPlan.IncompleteDetails,
                    IncompleteReasons = allHelixFailures.Count >= currentPlan.HelixFailureTotal
                        ? mergedReasons
                        : currentPlan.IncompleteReasons,
                    Complete = allHelixFailures.Count >= currentPlan.HelixFailureTotal &&
                               mergedDetails.Count == 0 &&
                               mergedReasons.Count == 0
                        ? true
                        : currentPlan.Complete
                };
                break;
            }
        }
        while (true);

        if (merged is not null && merged.HelixFailures.Count < merged.HelixFailureTotal)
        {
            incomplete.Add(new CollectIncompleteDetail
            {
                Code = "helix_failures_truncated",
                Message = $"Collected {merged.HelixFailures.Count} of {merged.HelixFailureTotal} Helix failure rows.",
                Operation = "azdo_evidence_plan",
                Resource = Resource(("buildId", buildId))
            });
        }

        return merged;
    }

    private async Task CollectLogsAsync(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        CollectPolicy policy,
        string org,
        string project,
        int buildId,
        AzdoTimeline timeline,
        IReadOnlyList<AzdoBuildLogEntry> logsList,
        AzdoEvidencePlan? evidencePlan,
        CancellationToken ct)
    {
        if (IsNone(policy.LogScope))
        {
            AddSkip(attempts, "azdo.logs", "azdo_logs", true, "azdo", "get_build_log",
                Resource(("buildId", buildId)), "policy_excluded", "AzDO log collection was disabled by --log-scope none.");
            return;
        }

        var selectedLogIds = SelectLogIds(policy.LogScope, timeline, logsList, evidencePlan);
        var logAttempts = await ForEachCollectAsync(selectedLogIds, policy.MaxConcurrency, async (logId, localAttempts) =>
        {
            await RunAsync(
                localAttempts,
                priorAttempts,
                CreateAttempt($"azdo.log:{buildId}:{logId}", "azdo.logs", true, "azdo", "get_build_log",
                    Resource(("org", org), ("project", project), ("buildId", buildId), ("logId", logId)),
                    AzdoCacheKeys.MetadataKey(_cacheOptions, org, project, $"log:{buildId}:{logId}")),
                policy,
                async token => await _azdoService.GetBuildLogAsync(policy.BuildIdOrUrl, logId, tailLines: null, token),
                metadata: true,
                ct);
        }, ct);
        attempts.AddRange(logAttempts);
    }

    private async Task CollectTestsAsync(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        CollectPolicy policy,
        string org,
        string project,
        int buildId,
        Action<string>? progress,
        CancellationToken ct)
    {
        if (IsNone(policy.TestScope))
        {
            AddSkip(attempts, "azdo.tests", "azdo_tests", true, "azdo", "list_test_runs",
                Resource(("buildId", buildId)), "policy_excluded", "AzDO test collection was disabled by --test-scope none.");
            return;
        }

        var runsEnvelope = await RunAsync(
            attempts,
            priorAttempts,
            CreateAttempt("azdo.test-runs", "azdo_tests", true, "azdo", "list_test_runs",
                Resource(("org", org), ("project", project), ("buildId", buildId)),
                AzdoListCacheKeys.TestRunsComplete(_cacheOptions, org, project, buildId)),
            policy,
            async token =>
            {
                var envelope = await _azdoService.GetTestRunsPageAsync(
                    policy.BuildIdOrUrl,
                    new HlxPageRequest { All = true, Offset = 0, Limit = null },
                    ct: token);
                var invalidRun = envelope.Results.FirstOrDefault(run => run.TotalTests < 0);
                if (invalidRun is not null)
                {
                    throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                        AcquisitionErrorKind.InvalidResponse, "azdo", "list_test_runs",
                        Resource(("org", org), ("project", project), ("buildId", buildId),
                            ("runId", invalidRun.Id), ("totalTests", invalidRun.TotalTests)),
                        "AzDO test-run metadata contains a negative totalTests; cannot safely estimate result volume."));
                }
                return envelope;
            },
            metadata: true,
            ct: ct,
            pagingSelector: value => value is HlxListEnvelope<AzdoTestRun> envelope ? PagingFromEnvelope(envelope) : null);

        if (runsEnvelope is null)
            return;

        var testProgress = new CollectProgressReporter(progress);
        var estimatedResults = runsEnvelope.Results.Sum(run => (long)run.TotalTests);
        var maxResults = policy.MaxTestResults ?? CollectPolicy.DefaultMaxTestResults;
        var maxAttachments = policy.MaxTestAttachments ?? CollectPolicy.DefaultMaxTestAttachments;
        var allResults = policy.TestScope.Equals("all", StringComparison.OrdinalIgnoreCase);
        testProgress.Report(
            $"Tests: {runsEnvelope.Results.Count} run(s), {estimatedResults} estimated result(s); " +
            $"scope={policy.TestScope}, result budget={maxResults}, attachments={policy.TestAttachmentScope}, attachment budget={maxAttachments}.",
            force: true);
        if (allResults && estimatedResults > maxResults)
        {
            var message = $"All-result collection skipped: {estimatedResults} estimated results exceed " +
                $"{(policy.MaxTestResults.HasValue ? "explicit" : "default")} --max-test-results {maxResults}. " +
                $"Rerun with --max-test-results {estimatedResults} or larger, or use --test-scope failed.";
            AddSkip(attempts, "azdo.test-results-budget", "azdo.tests", true, "azdo", "list_test_results",
                Resource(("buildId", buildId), ("requestedCount", estimatedResults),
                    ("maxTestResults", maxResults), ("budgetExplicit", policy.MaxTestResults.HasValue)),
                "test_result_limit", message);
            testProgress.Report(message, force: true);
            return;
        }
        testProgress.Report(allResults
            ? $"Tests: all-result budget accepted ({estimatedResults}/{maxResults}); attachments remain {policy.TestAttachmentScope}."
            : "Tests: failed-only result collection; all-result volume guard does not apply.", force: true);
        var attachmentPlans = new ConcurrentBag<TestAttachmentPlan>();
        long acquiredResults = 0;
        var outcomes = policy.TestScope.Equals("all", StringComparison.OrdinalIgnoreCase)
            // Full AzDO TestOutcome enum (Microsoft.TeamFoundation.TestManagement.WebApi.TestOutcome),
            // excluding "NotRunnable" which the AzDO test-results API rejects as an invalid
            // outcome filter value even though some docs/tools list it as a scope keyword.
            ? "Unspecified,None,Passed,Failed,Inconclusive,Timeout,Aborted,Blocked,NotExecuted,Warning,Error,NotApplicable,Paused,InProgress,NotImpacted"
            : "Failed";

        var testAttempts = await ForEachCollectAsync(runsEnvelope.Results, policy.MaxConcurrency, async (run, localAttempts) =>
        {
            testProgress.Report($"Run {run.Id}: acquiring test results ({run.TotalTests} estimated total).", force: true);
            var resultsEnvelope = await testProgress.WithHeartbeatAsync(
                $"Run {run.Id} test results",
                () => RunAsync(
                localAttempts,
                priorAttempts,
                CreateAttempt($"azdo.test-results:{run.Id}:{outcomes}", "azdo.tests", true, "azdo", "list_test_results",
                    Resource(("org", org), ("project", project), ("runId", run.Id), ("outcomes", outcomes)),
                    AzdoListCacheKeys.TestResultsComplete(_cacheOptions, org, project, run.Id, outcomes)),
                policy,
                async token => await _azdoService.GetTestResultsPageAsync(
                    policy.BuildIdOrUrl,
                    run.Id,
                    new HlxPageRequest { All = true, Offset = 0, Limit = null },
                    outcomes,
                    ct: token),
                metadata: true,
                ct: ct,
                pagingSelector: value => value is HlxListEnvelope<AzdoTestResult> envelope ? PagingFromEnvelope(envelope) : null),
                ct);

            if (resultsEnvelope is null)
            {
                testProgress.Report($"Run {run.Id}: test-result acquisition failed; see manifest.", force: true);
                return;
            }

            var totalAcquired = Interlocked.Add(ref acquiredResults, resultsEnvelope.Results.Count);
            testProgress.Report(
                $"Run {run.Id}: acquired {resultsEnvelope.Results.Count} result(s); build acquired {totalAcquired}.", force: true);
            if (allResults && totalAcquired > maxResults)
            {
                AddSkip(localAttempts, $"azdo.test-results-budget:{run.Id}", "azdo.tests", true, "azdo", "list_test_results",
                    Resource(("runId", run.Id), ("requestedCount", totalAcquired), ("maxTestResults", maxResults)),
                    "test_result_limit",
                    $"Provider results exceeded the build-wide estimate and --max-test-results {maxResults}. " +
                    $"Rerun with --max-test-results {totalAcquired} or larger.");
            }

            if (allResults)
            {
                var failedResults = resultsEnvelope.Results
                    .Where(result => string.Equals(result.Outcome, "Failed", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var failedCacheKey = AzdoListCacheKeys.TestResultsComplete(_cacheOptions, org, project, run.Id, "Failed");
                var failedSerialized = JsonSerializer.Serialize(failedResults, s_jsonOptions);
                await _cacheStore.SetMetadataAsync(
                    failedCacheKey,
                    failedSerialized,
                    TestMetadataTtl,
                    ct);
                var now = DateTimeOffset.UtcNow;
                AppendAttempt(localAttempts, new CollectFetchAttempt
                {
                    Id = $"azdo.test-results-derived:{run.Id}:Failed",
                    ParentId = $"azdo.test-results:{run.Id}:{outcomes}",
                    Phase = "azdo.tests",
                    Required = true,
                    Provider = "azdo",
                    Operation = "list_test_results",
                    Resource = Resource(("org", org), ("project", project), ("runId", run.Id), ("outcomes", "Failed"), ("derivedFromOutcomes", outcomes)),
                    CacheKey = failedCacheKey,
                    CompleteCacheKey = failedCacheKey,
                    StartedAt = now,
                    FinishedAt = now,
                    DurationMs = 0,
                    AttemptCount = 1,
                    Outcome = "ok",
                    Bytes = Encoding.UTF8.GetByteCount(failedSerialized),
                    Sha256 = Sha256Hex(Encoding.UTF8.GetBytes(failedSerialized))
                });
            }

            var eligible = resultsEnvelope.Results.Where(result => SelectTestAttachments(policy.TestAttachmentScope, result));
            var eligibleCount = eligible.Count();
            attachmentPlans.Add(new TestAttachmentPlan(run.Id, eligibleCount,
                resultsEnvelope.Results.Count - eligibleCount,
                eligible.OrderBy(result => result.Id).Take((int)Math.Min(maxAttachments, int.MaxValue)).ToList()));
        }, ct);
        attempts.AddRange(testAttempts);

        var selected = new List<(int RunId, int ResultId)>();
        long excludedCount = 0;
        long limitedCount = 0;
        foreach (var plan in attachmentPlans.OrderBy(plan => plan.RunId))
        {
            excludedCount += plan.Excluded;
            if (plan.Excluded > 0)
            {
                AddSkip(attempts, $"azdo.test-attachment-exclusions:{plan.RunId}", "azdo.tests", false, "azdo", "list_test_attachments",
                    Resource(("runId", plan.RunId), ("requestedCount", plan.Excluded), ("testAttachmentScope", policy.TestAttachmentScope)),
                    "policy_excluded", $"{plan.Excluded} result(s) excluded by --test-attachment-scope {policy.TestAttachmentScope}.");
            }
            var available = Math.Max(0, maxAttachments - selected.Count);
            var take = (int)Math.Min(plan.Candidates.Count, available);
            selected.AddRange(plan.Candidates.Take(take).Select(result => (plan.RunId, result.Id)));
            var skipped = plan.Eligible - take;
            limitedCount += skipped;
            if (skipped > 0)
            {
                AddSkip(attempts, $"azdo.test-attachment-limit:{plan.RunId}", "azdo.tests", true, "azdo", "list_test_attachments",
                    Resource(("runId", plan.RunId), ("requestedCount", plan.Eligible), ("selectedCount", take),
                        ("skippedCount", skipped), ("maxTestAttachments", maxAttachments)),
                    "test_attachment_limit",
                    $"{skipped} eligible attachment-list request(s) skipped by build-wide --max-test-attachments {maxAttachments}. " +
                    "Rerun with a larger --max-test-attachments to collect the remaining selected coverage.");
            }
        }
        testProgress.Report(
            $"Attachments: selected {selected.Count}, excluded {excludedCount}, skipped by limit {limitedCount}; " +
            $"scope={policy.TestAttachmentScope}, budget={maxAttachments}.", force: true);
        long completedAttachments = 0;
        var attachmentAttempts = await testProgress.WithHeartbeatAsync(
            "Test attachments",
            () => ForEachCollectAsync(selected, policy.MaxConcurrency, async (item, localAttempts) =>
            {
                await RunAsync(localAttempts, priorAttempts,
                    CreateAttempt($"azdo.test-attachments:{item.RunId}:{item.ResultId}", "azdo.tests", false, "azdo", "list_test_attachments",
                        Resource(("org", org), ("project", project), ("runId", item.RunId), ("resultId", item.ResultId)),
                        AzdoListCacheKeys.TestAttachmentsComplete(_cacheOptions, org, project, item.RunId, item.ResultId)),
                    policy,
                    token => _azdoService.GetTestAttachmentsPageAsync(org, project, item.RunId, item.ResultId,
                        new HlxPageRequest { All = true, Offset = 0, Limit = null }, ct: token),
                    metadata: true, ct: ct,
                    pagingSelector: value => value is HlxListEnvelope<AzdoTestAttachment> envelope ? PagingFromEnvelope(envelope) : null);
                var done = Interlocked.Increment(ref completedAttachments);
                testProgress.Report($"Attachments: completed {done}/{selected.Count}; excluded {excludedCount}, skipped by limit {limitedCount}.");
            }, ct),
            ct);
        attempts.AddRange(attachmentAttempts);
        testProgress.Report(
            $"Attachments: completed {completedAttachments}/{selected.Count}; excluded {excludedCount}, skipped by limit {limitedCount}.",
            force: true);
    }

    private sealed record TestAttachmentPlan(int RunId, int Eligible, int Excluded, IReadOnlyList<AzdoTestResult> Candidates);

    private static bool SelectTestAttachments(string scope, AzdoTestResult result)
        => scope.Equals("all", StringComparison.OrdinalIgnoreCase) ||
           scope.Equals("diagnostic", StringComparison.OrdinalIgnoreCase) &&
           result.Outcome is not null && s_diagnosticTestOutcomes.Contains(result.Outcome);

    private async Task CollectHelixSuggestedFetchesAsync(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        CollectPolicy policy,
        AzdoEvidencePlan evidencePlan,
        CancellationToken ct)
    {
        if (IsNone(policy.HelixScope))
        {
            AddSkip(attempts, "helix.suggested", "helix", true, "helix", "helix_suggested_fetches",
                Resource(("buildId", evidencePlan.BuildId)), "policy_excluded", "Helix collection was disabled by --helix-scope none.");
            return;
        }

        var fetches = evidencePlan.HelixFailures
            .SelectMany(DefaultHelixFetches)
            .GroupBy(fetch => HelixFetchDedupeKey(fetch), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        // Optional Helix file downloads must never be able to evict required evidence already
        // collected in this run via the cache's LRU cap. Reserve the bytes required attempts have
        // already consumed and clamp the optional download budget to whatever cache capacity
        // remains, in addition to the user-requested --max-total-bytes.
        var requiredBytesSoFar = attempts.Where(a => a.Required).Sum(a => a.Bytes ?? 0);
        var remainingCacheCapacity = Math.Max(0, _cacheOptions.MaxSizeBytes - requiredBytesSoFar);
        var effectiveMaxTotalBytes = Math.Min(policy.MaxTotalBytes, remainingCacheCapacity);
        var downloadBudget = new HelixFileDownloadBudget(effectiveMaxTotalBytes);

        var helixAttempts = await ForEachCollectAsync(fetches, policy.MaxConcurrency, async (fetch, localAttempts) =>
        {
            var jobId = HelixIdResolver.ResolveJobId(fetch.HelixJobId);
            var workItem = fetch.WorkItem!;
            switch (fetch.Tool)
            {
                case "helix_work_item":
                    await RunAsync(
                        localAttempts,
                        priorAttempts,
                        CreateAttempt($"helix.work-item:{jobId}:{workItem}", "helix.suggested", true, "helix", "get_helix_work_item",
                            Resource(("jobId", jobId), ("workItem", workItem)),
                            HelixWorkItemDetailsKey(jobId, workItem)),
                        policy,
                        async token => await _helixService.GetWorkItemDetailAsync(jobId, workItem, token),
                        metadata: true,
                        ct);
                    break;
                case "helix_logs":
                    await RunAsync(
                        localAttempts,
                        priorAttempts,
                        CreateAttempt($"helix.logs:{jobId}:{workItem}", "helix.suggested", true, "helix", "get_helix_console_log",
                            Resource(("jobId", jobId), ("workItem", workItem)),
                            HelixConsoleKey(jobId, workItem)),
                        policy,
                        async token => await _helixService.GetConsoleLogContentAsync(jobId, workItem, tailLines: null, token),
                        metadata: false,
                        ct);
                    break;
                case "helix_files":
                    var files = await RunAsync(
                        localAttempts,
                        priorAttempts,
                        CreateAttempt($"helix.files:{jobId}:{workItem}", "helix.suggested", true, "helix", "list_helix_work_item_files",
                            Resource(("jobId", jobId), ("workItem", workItem)),
                            HelixFilesKey(jobId, workItem)),
                        policy,
                        async token => await _helixService.GetWorkItemFilesAsync(jobId, workItem, token),
                        metadata: true,
                        ct);
                    await CollectSelectedHelixFilesAsync(localAttempts, priorAttempts, policy, downloadBudget, jobId, workItem, files ?? [], ct);
                    break;
            }
        }, ct);
        attempts.AddRange(helixAttempts);
    }

    private async Task CollectSelectedHelixFilesAsync(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        CollectPolicy policy,
        HelixFileDownloadBudget downloadBudget,
        string jobId,
        string workItem,
        IReadOnlyList<HelixService.FileEntry> files,
        CancellationToken ct)
    {
        foreach (var file in files)
        {
            var selected = policy.DownloadHelixFiles is not null &&
                StringHelpers.MatchesPattern(file.Name, policy.DownloadHelixFiles);
            var attemptId = $"helix.file-download:{jobId}:{workItem}:{file.Name}";
            var resource = Resource(("jobId", jobId), ("workItem", workItem), ("fileName", file.Name));
            var cacheKey = HelixFileKey(jobId, workItem, file.Name);
            if (!selected)
            {
                AddSkip(attempts, attemptId, "helix_files", false, "helix", "download_helix_file",
                    resource,
                    "policy_excluded",
                    "Helix uploaded-file byte downloads are excluded by default; metadata was collected.",
                    cacheKey: cacheKey);
                continue;
            }

            await DownloadHelixFileWithCapsAsync(
                attempts,
                priorAttempts,
                policy,
                downloadBudget,
                attemptId,
                jobId,
                workItem,
                file.Name,
                cacheKey,
                resource,
                ct);
        }
    }

    private async Task DownloadHelixFileWithCapsAsync(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        CollectPolicy policy,
        HelixFileDownloadBudget downloadBudget,
        string attemptId,
        string jobId,
        string workItem,
        string fileName,
        string cacheKey,
        IReadOnlyDictionary<string, object?> resource,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        if (policy.Resume &&
            priorAttempts.TryGetValue(attemptId, out var prior) &&
            string.Equals(prior.CacheKey, cacheKey, StringComparison.Ordinal) &&
            prior.Outcome is "ok" or "cached")
        {
            await using var cached = await _cacheStore.GetArtifactAsync(cacheKey, ct);
            if (cached is not null)
            {
                var cachedBytes = prior.Bytes ?? (cached.CanSeek ? cached.Length : 0);
                if (cachedBytes <= policy.MaxFileBytes && downloadBudget.TryConsume(cachedBytes))
                {
                    stopwatch.Stop();
                    AppendAttempt(attempts, prior with
                    {
                        Outcome = "cached",
                        StartedAt = started,
                        FinishedAt = DateTimeOffset.UtcNow,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        Bytes = cachedBytes
                    });
                    return;
                }

                stopwatch.Stop();
                AppendAttempt(attempts, SkipAttempt(
                    attemptId,
                    "helix_files",
                    false,
                    "helix",
                    "download_helix_file",
                    resource,
                    cacheKey,
                    started,
                    stopwatch.ElapsedMilliseconds,
                    cachedBytes > policy.MaxFileBytes ? "size_limit" : "total_size_limit",
                    cachedBytes > policy.MaxFileBytes
                        ? $"Cached Helix uploaded file '{fileName}' exceeds --max-file-bytes {policy.MaxFileBytes}."
                        : $"Cached Helix uploaded file '{fileName}' would exceed the effective download budget of {downloadBudget.MaxTotalBytes} bytes (--max-total-bytes {policy.MaxTotalBytes}, clamped to remaining cache capacity)."));
                return;
            }
        }

        if (policy.MaxFileBytes == 0)
        {
            stopwatch.Stop();
            AppendAttempt(attempts, SkipAttempt(
                attemptId,
                "helix_files",
                false,
                "helix",
                "download_helix_file",
                resource,
                cacheKey,
                started,
                stopwatch.ElapsedMilliseconds,
                "size_limit",
                $"Helix uploaded file '{fileName}' was not opened because --max-file-bytes is 0."));
            return;
        }

        if (downloadBudget.Remaining <= 0)
        {
            stopwatch.Stop();
            AppendAttempt(attempts, SkipAttempt(
                attemptId,
                "helix_files",
                false,
                "helix",
                "download_helix_file",
                resource,
                cacheKey,
                started,
                stopwatch.ElapsedMilliseconds,
                "total_size_limit",
                $"Helix uploaded file '{fileName}' was not opened because --max-total-bytes is exhausted."));
            return;
        }

        var attemptCount = 0;
        HlxAcquisitionException? acquisitionFailure = null;
        while (attemptCount < policy.RetryCount)
        {
            attemptCount++;
            var tempPath = Path.Combine(Path.GetTempPath(), $"hlx-collect-{Guid.NewGuid():N}.tmp");
            long reservedBytes = 0;
            try
            {
                await using var source = await OpenUncachedHelixFileAsync(fileName, workItem, jobId, ct);
                await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[64 * 1024];
                    long bytes = 0;
                    while (true)
                    {
                        var read = await ReadHelixFileChunkAsync(
                            source,
                            buffer.AsMemory(0, buffer.Length),
                            resource,
                            ct);
                        if (read == 0)
                            break;

                        if (bytes + read > policy.MaxFileBytes)
                        {
                            downloadBudget.Release(reservedBytes);
                            stopwatch.Stop();
                            AppendAttempt(attempts, SkipAttempt(
                                attemptId,
                                "helix_files",
                                false,
                                "helix",
                                "download_helix_file",
                                resource,
                                cacheKey,
                                started,
                                stopwatch.ElapsedMilliseconds,
                                "size_limit",
                                $"Helix uploaded file '{fileName}' exceeded --max-file-bytes {policy.MaxFileBytes}."));
                            return;
                        }

                        if (!downloadBudget.TryConsume(read))
                        {
                            downloadBudget.Release(reservedBytes);
                            stopwatch.Stop();
                            AppendAttempt(attempts, SkipAttempt(
                                attemptId,
                                "helix_files",
                                false,
                                "helix",
                                "download_helix_file",
                                resource,
                                cacheKey,
                                started,
                                stopwatch.ElapsedMilliseconds,
                                "total_size_limit",
                                $"Helix uploaded file '{fileName}' would exceed the effective download budget of {downloadBudget.MaxTotalBytes} bytes (--max-total-bytes {policy.MaxTotalBytes}, clamped to remaining cache capacity)."));
                            return;
                        }

                        reservedBytes += read;
                        bytes += read;
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                        hash.AppendData(buffer.AsSpan(0, read));
                    }

                    destination.Position = 0;
                    await _cacheStore.SetArtifactAsync(cacheKey, destination, ct);
                    stopwatch.Stop();
                    AppendAttempt(attempts, new CollectFetchAttempt
                    {
                        Id = attemptId,
                        Phase = "helix_files",
                        Required = false,
                        Provider = "helix",
                        Operation = "download_helix_file",
                        Resource = resource,
                        CacheKey = cacheKey,
                        CompleteCacheKey = cacheKey,
                        StartedAt = started,
                        FinishedAt = DateTimeOffset.UtcNow,
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        AttemptCount = attemptCount,
                        Outcome = "ok",
                        Bytes = bytes,
                        Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
                    });
                    return;
                }
            }
            catch (HlxAcquisitionException ex) when (attemptCount < policy.RetryCount && policy.RetryKinds.Contains(ex.Error.Kind))
            {
                downloadBudget.Release(reservedBytes);
                acquisitionFailure = ex;
                await Task.Delay(GetRetryDelay(policy, ex.Error, attemptCount), ct);
            }
            catch (HlxAcquisitionException ex)
            {
                downloadBudget.Release(reservedBytes);
                acquisitionFailure = ex;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                downloadBudget.Release(reservedBytes);
                throw;
            }
            catch (Exception ex)
            {
                // Last-resort net: an unclassified failure (e.g. local disk I/O while caching the
                // download) must still produce a recorded failed attempt, not an unhandled crash.
                downloadBudget.Release(reservedBytes);
                acquisitionFailure = new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.TransportError,
                    "helix",
                    "download_helix_file",
                    resource,
                    $"Unexpected error during download_helix_file: {ex.Message}"),
                    ex);
                break;
            }
            finally
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        stopwatch.Stop();
        var error = acquisitionFailure?.Error;
        var recorded = error is not null && await TryRecordAcquisitionFailureAsync(cacheKey, error, ct);
        AppendAttempt(attempts, new CollectFetchAttempt
        {
            Id = attemptId,
            Phase = "helix_files",
            Required = false,
            Provider = "helix",
            Operation = "download_helix_file",
            Resource = resource,
            CacheKey = cacheKey,
            CompleteCacheKey = cacheKey,
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            DurationMs = stopwatch.ElapsedMilliseconds,
            AttemptCount = attemptCount,
            Outcome = recorded ? "recorded_failure" : "failed",
            Error = error
        });
    }

    private async Task<Stream> OpenUncachedHelixFileAsync(
        string fileName,
        string workItem,
        string jobId,
        CancellationToken ct)
    {
        try
        {
            return _helixClient is IUncachedHelixFileClient uncached
                ? await uncached.GetFileUncachedAsync(fileName, workItem, jobId, ct)
                : await _helixClient.GetFileAsync(fileName, workItem, jobId, ct);
        }
        catch (HttpRequestException ex)
        {
            throw HelixAcquisition.FromHttp(ex, "download_helix_file", HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItem), ("fileName", fileName)));
        }
        catch (Microsoft.DotNet.Helix.Client.RestApiException ex)
        {
            throw HelixAcquisition.FromRestApi(ex, "download_helix_file", HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItem), ("fileName", fileName)));
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw HelixAcquisition.Timeout(ex, "download_helix_file", HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItem), ("fileName", fileName)));
        }
    }

    private static async ValueTask<int> ReadHelixFileChunkAsync(
        Stream source,
        Memory<byte> buffer,
        IReadOnlyDictionary<string, object?> resource,
        CancellationToken ct)
    {
        try
        {
            return await source.ReadAsync(buffer, ct);
        }
        catch (HttpRequestException ex)
        {
            throw HelixAcquisition.FromHttp(ex, "download_helix_file", resource);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw HelixAcquisition.Timeout(ex, "download_helix_file", resource);
        }
        catch (IOException ex)
        {
            throw HelixAcquisition.Transport(ex, "download_helix_file", resource);
        }
    }

    private static CollectFetchAttempt SkipAttempt(
        string id,
        string phase,
        bool required,
        string provider,
        string operation,
        IReadOnlyDictionary<string, object?> resource,
        string? cacheKey,
        DateTimeOffset started,
        long durationMs,
        string skipKind,
        string message)
        => new()
        {
            Id = id,
            Phase = phase,
            Required = required,
            Provider = provider,
            Operation = operation,
            Resource = resource,
            CacheKey = cacheKey,
            CompleteCacheKey = cacheKey,
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            AttemptCount = 1,
            Outcome = "skipped",
            Skip = new CollectSkipInfo { Kind = skipKind, Message = message }
        };

    private async Task<T?> RunAsync<T>(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        CollectFetchAttempt template,
        CollectPolicy policy,
        Func<CancellationToken, Task<T>> action,
        bool metadata,
        CancellationToken ct,
        Func<object?, CollectPagingInfo?>? pagingSelector = null)
    {
        ct.ThrowIfCancellationRequested();
        var resumeAsCached = false;

        if (policy.Resume && priorAttempts.TryGetValue(template.Id, out var prior))
        {
            var sameKey = string.Equals(prior.CacheKey, template.CacheKey, StringComparison.Ordinal);
            if (sameKey &&
                prior.Outcome is "ok" or "cached" &&
                await CacheEntryExistsAsync(template.CacheKey, metadata, ct))
            {
                resumeAsCached = true;
            }
            else if (sameKey && prior.Error is not null)
            {
                var storedError = string.IsNullOrWhiteSpace(template.CacheKey)
                    ? null
                    : await _cacheStore.GetAcquisitionErrorAsync(template.CacheKey, ct);
                if (storedError is not null && !policy.RetryKinds.Contains(storedError.Kind))
                {
                    AppendAttempt(attempts, template with
                    {
                        Outcome = "recorded_failure",
                        StartedAt = DateTimeOffset.UtcNow,
                        FinishedAt = DateTimeOffset.UtcNow,
                        DurationMs = 0,
                        AttemptCount = 1,
                        Error = storedError
                    });
                    return default;
                }
            }
        }

        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var attemptCount = 0;
        HlxAcquisitionException? acquisitionFailure = null;

        while (attemptCount < policy.RetryCount)
        {
            attemptCount++;
            try
            {
                var value = await action(ct);
                stopwatch.Stop();
                var serialized = JsonSerializer.Serialize(value, s_jsonOptions);
                var cacheBytes = template.CacheKey is null
                    ? null
                    : await TryGetCacheByteCountAsync(template.CacheKey, metadata, template.Operation, ct);
                var bytes = cacheBytes ?? Encoding.UTF8.GetByteCount(serialized);
                AppendAttempt(attempts, template with
                {
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.UtcNow,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    AttemptCount = attemptCount,
                    Outcome = resumeAsCached ? "cached" : "ok",
                    Bytes = bytes,
                    Sha256 = cacheBytes is null ? Sha256Hex(Encoding.UTF8.GetBytes(serialized)) : null,
                    Paging = pagingSelector?.Invoke(value)
                });
                return value;
            }
            catch (HlxAcquisitionException ex) when (attemptCount < policy.RetryCount && policy.RetryKinds.Contains(ex.Error.Kind))
            {
                acquisitionFailure = ex;
                await Task.Delay(GetRetryDelay(policy, ex.Error, attemptCount), ct);
            }
            catch (HlxAcquisitionException ex)
            {
                acquisitionFailure = ex;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Last-resort net: any unclassified failure (e.g. a body-read/stream error that
                // escaped client-boundary classification) must still produce a recorded failed
                // attempt so the manifest is always written, never an unhandled crash with no manifest.
                acquisitionFailure = new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.TransportError,
                    template.Provider,
                    template.Operation,
                    template.Resource,
                    $"Unexpected error during {template.Operation}: {ex.Message}"),
                    ex);
                break;
            }
        }

        stopwatch.Stop();
        var error = acquisitionFailure?.Error;
        var recorded = error is not null && await AcquisitionFailureWasRecordedAsync(template.CacheKey, error, ct);
        AppendAttempt(attempts, template with
        {
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            DurationMs = stopwatch.ElapsedMilliseconds,
            AttemptCount = attemptCount,
            Outcome = recorded ? "recorded_failure" : "failed",
            Error = error
        });
        return default;
    }

    private static CollectFetchAttempt CreateAttempt(
        string id,
        string phase,
        bool required,
        string provider,
        string operation,
        IReadOnlyDictionary<string, object?> resource,
        string? cacheKey,
        string? parentId = null)
        => new()
        {
            Id = id,
            ParentId = parentId,
            Phase = phase,
            Required = required,
            Provider = provider,
            Operation = operation,
            Resource = resource,
            CacheKey = cacheKey,
            CompleteCacheKey = cacheKey
        };

    private static void AddSkip(
        List<CollectFetchAttempt> attempts,
        string id,
        string phase,
        bool required,
        string provider,
        string operation,
        IReadOnlyDictionary<string, object?> resource,
        string kind,
        string message,
        string? cacheKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        AppendAttempt(attempts, new CollectFetchAttempt
        {
            Id = id,
            Phase = phase,
            Required = required,
            Provider = provider,
            Operation = operation,
            Resource = resource,
            CacheKey = cacheKey,
            CompleteCacheKey = cacheKey,
            StartedAt = now,
            FinishedAt = now,
            DurationMs = 0,
            AttemptCount = 1,
            Outcome = "skipped",
            Skip = new CollectSkipInfo { Kind = kind, Message = message }
        });
    }

    private static void AppendAttempt(List<CollectFetchAttempt> attempts, CollectFetchAttempt attempt)
    {
        lock (attempts)
        {
            attempts.Add(attempt);
        }
    }

    private static IReadOnlyDictionary<string, object?> Resource(params (string, object?)[] values)
    {
        var resource = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var valuePair in values)
        {
            if (valuePair.Item2 is not null)
                resource[valuePair.Item1] = valuePair.Item2;
        }

        return resource;
    }

    private async Task<bool> CacheEntryExistsAsync(string? cacheKey, bool metadata, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return false;

        if (metadata)
            return await _cacheStore.GetMetadataAsync(cacheKey, ct) is not null;

        await using var stream = await _cacheStore.GetArtifactAsync(cacheKey, ct);
        return stream is not null;
    }

    private async Task<long?> TryGetCacheByteCountAsync(
        string cacheKey,
        bool metadata,
        string operation,
        CancellationToken ct)
    {
        if (metadata)
        {
            var cached = await _cacheStore.GetMetadataAsync(cacheKey, ct);
            return cached is null ? null : MetadataCacheByteCount(operation, cached);
        }

        await using var stream = await _cacheStore.GetArtifactAsync(cacheKey, ct);
        return stream is { CanSeek: true } ? stream.Length : null;
    }

    private async Task ResolveAzdoAuthContextAsync(CancellationToken ct)
    {
        var credential = await _azdoTokenAccessor.GetAccessTokenAsync(ct);
        var authContext = credential is null
            ? null
            : credential.CacheIdentity ?? AzdoCredential.BuildCacheIdentity(credential.Source, credential.DisplayToken);
        _cacheOptions.UpdateAuthContext(authContext);
    }

    private async Task<bool> TryRecordAcquisitionFailureAsync(
        string? cacheKey,
        AcquisitionError error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) ||
            !AcquisitionFailureRecorderPolicy.IsRecordable(error))
        {
            return false;
        }

        await _cacheStore.SetAcquisitionErrorAsync(cacheKey, error, RecordedFailureTtl, ct);
        return await AcquisitionFailureWasRecordedAsync(cacheKey, error, ct);
    }

    private async Task<bool> AcquisitionFailureWasRecordedAsync(
        string? cacheKey,
        AcquisitionError error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) ||
            !AcquisitionFailureRecorderPolicy.IsRecordable(error))
        {
            return false;
        }

        var stored = await _cacheStore.GetAcquisitionErrorAsync(cacheKey, ct);
        return stored is not null &&
            stored.Kind == error.Kind &&
            string.Equals(stored.Provider, error.Provider, StringComparison.Ordinal) &&
            string.Equals(stored.Operation, error.Operation, StringComparison.Ordinal);
    }

    private async Task<List<CollectFetchAttempt>> VerifyCollectedEvidenceAsync(
        IReadOnlyList<CollectFetchAttempt> attempts,
        CancellationToken ct)
    {
        var verified = new List<CollectFetchAttempt>(attempts.Count);
        foreach (var attempt in attempts)
        {
            if (attempt.Outcome is not ("ok" or "cached") ||
                string.IsNullOrWhiteSpace(attempt.CacheKey))
            {
                verified.Add(attempt);
                continue;
            }

            var verification = await VerifyCacheEvidenceAsync(attempt, ct);
            if (verification.Ok)
            {
                verified.Add(attempt);
                continue;
            }

            verified.Add(attempt with
            {
                Outcome = "failed",
                Error = AcquisitionErrorFactory.Create(
                verification.Kind,
                    "cache",
                    "verify_collected_evidence",
                    Resource(("key", attempt.CacheKey), ("operation", attempt.Operation)),
                verification.Message)
            });
        }

        return verified;
    }

    private async Task<(bool Ok, AcquisitionErrorKind Kind, string Message)> VerifyCacheEvidenceAsync(
        CollectFetchAttempt attempt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(attempt.CacheKey))
        {
            return (false, AcquisitionErrorKind.NotInSnapshot,
                $"Collected evidence for {attempt.Operation} did not report a cache key.");
        }

        if (IsArtifactOperation(attempt.Operation))
        {
            await using var stream = await _cacheStore.GetArtifactAsync(attempt.CacheKey, ct);
            if (stream is null)
            {
                return (false, AcquisitionErrorKind.NotInSnapshot,
                $"Collected evidence for {attempt.Operation} was missing from the cache before export.");
            }

            if (attempt.Bytes is { } expectedBytes &&
                stream.CanSeek &&
                stream.Length != expectedBytes)
            {
                return (false, AcquisitionErrorKind.InvalidResponse,
                $"Collected artifact evidence for {attempt.Operation} changed size before export: expected {expectedBytes} byte(s), found {stream.Length}.");
            }

            return (true, AcquisitionErrorKind.NotInSnapshot, "");
        }

        // Ignore TTL here: this verifies the row is still physically present after collection,
        // which can outlast a short metadata TTL on a long-running collect. Export and offline
        // eval replay both ignore expiry too, so a TTL-expired-but-present row is not actually
        // missing evidence.
        var cached = await _cacheStore.GetMetadataIgnoringTtlAsync(attempt.CacheKey, ct);
        if (cached is null)
        {
            return (false, AcquisitionErrorKind.NotInSnapshot,
                $"Collected evidence for {attempt.Operation} was missing from the cache before export.");
        }

        if (attempt.Operation == "get_build_log" &&
            (cached.Length == 0 || string.Equals(cached, CachingAzdoApiClient.RawTextPrefix, StringComparison.Ordinal)))
        {
            return (false, AcquisitionErrorKind.InvalidResponse,
                $"Collected AzDO log evidence for {attempt.Operation} was empty/corrupt before export.");
        }

        if (attempt.Bytes is { } expectedMetadataBytes)
        {
            var actualMetadataBytes = MetadataCacheByteCount(attempt.Operation, cached);
            if (actualMetadataBytes != expectedMetadataBytes)
            {
                return (false, AcquisitionErrorKind.InvalidResponse,
                $"Collected metadata evidence for {attempt.Operation} changed size before export: expected {expectedMetadataBytes} byte(s), found {actualMetadataBytes}.");
            }
        }

        return (true, AcquisitionErrorKind.NotInSnapshot, "");
    }

    private static int MetadataCacheByteCount(string operation, string cached)
    {
        if (operation == "get_build_log" &&
            cached.StartsWith(CachingAzdoApiClient.RawTextPrefix, StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetByteCount(cached[CachingAzdoApiClient.RawTextPrefix.Length..]);
        }

        return Encoding.UTF8.GetByteCount(cached);
    }

    private static bool IsArtifactOperation(string operation)
        => operation is "get_helix_console_log" or "download_helix_file";

    private static IEnumerable<AzdoHelixEvidenceFetch> DefaultHelixFetches(AzdoHelixEvidenceFailure failure)
    {
        if (string.IsNullOrWhiteSpace(failure.HelixJobId) || string.IsNullOrWhiteSpace(failure.WorkItem))
            yield break;

        yield return new AzdoHelixEvidenceFetch { Tool = "helix_work_item", HelixJobId = failure.HelixJobId, WorkItem = failure.WorkItem, Purpose = "work_item_details" };
        yield return new AzdoHelixEvidenceFetch { Tool = "helix_logs", HelixJobId = failure.HelixJobId, WorkItem = failure.WorkItem, Purpose = "console_log" };
        yield return new AzdoHelixEvidenceFetch { Tool = "helix_files", HelixJobId = failure.HelixJobId, WorkItem = failure.WorkItem, Purpose = "uploaded_files" };
    }

    private static string HelixFetchDedupeKey(AzdoHelixEvidenceFetch fetch)
        => string.Join('\0',
            fetch.Tool,
            HelixIdResolver.ResolveJobId(fetch.HelixJobId),
            fetch.WorkItem ?? "");

    private static IEnumerable<AzdoHelixEvidenceFetch> CompanionHelixFetches(AzdoHelixEvidenceFetch fetch)
    {
        if (string.IsNullOrWhiteSpace(fetch.HelixJobId) || string.IsNullOrWhiteSpace(fetch.WorkItem))
            yield break;

        yield return fetch with { Tool = "helix_work_item", Purpose = "work_item_details" };
        yield return fetch with { Tool = "helix_logs", Purpose = "console_log" };
        yield return fetch with { Tool = "helix_files", Purpose = "uploaded_files" };
    }

    private static async Task<IReadOnlyDictionary<string, CollectFetchAttempt>> LoadPriorAttemptsAsync(
        CollectPolicy policy,
        string manifestPath,
        CancellationToken ct)
    {
        if (!policy.Resume || !File.Exists(manifestPath))
            return new Dictionary<string, CollectFetchAttempt>(StringComparer.Ordinal);

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<CollectManifest>(stream, s_jsonOptions, ct);
        if (manifest is null)
            return new Dictionary<string, CollectFetchAttempt>(StringComparer.Ordinal);

        var attempts = new Dictionary<string, CollectFetchAttempt>(StringComparer.Ordinal);
        foreach (var attempt in manifest.Attempts)
            attempts[attempt.Id] = attempt;

        return attempts;
    }

    private static async Task WriteManifestAsync(string path, CollectManifest manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, s_jsonOptions, ct);
            await stream.FlushAsync(ct);
        }

        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }

    private CollectManifest BuildManifest(
        CollectPolicy policy,
        DateTimeOffset startedAt,
        CollectSourceInfo source,
        CollectAzdoAuthInfo azdoAuth,
        CollectHelixAuthInfo helixAuth,
        IReadOnlyList<CollectFetchAttempt> attempts,
        IReadOnlyList<CollectIncompleteDetail> incomplete,
        bool complete,
        int exitCode,
        CollectSnapshotInfo snapshot)
    {
        var summary = new CollectSummary
        {
            Attempted = attempts.Count(a => a.Outcome != "skipped"),
            Ok = attempts.Count(a => a.Outcome == "ok"),
            Cached = attempts.Count(a => a.Outcome == "cached"),
            RecordedFailure = attempts.Count(a => a.Outcome == "recorded_failure"),
            Failed = attempts.Count(a => a.Outcome == "failed"),
            Skipped = attempts.Count(a => a.Outcome == "skipped"),
            Bytes = attempts.Sum(a => a.Bytes ?? 0)
        };

        return new CollectManifest
        {
            ManifestId = UlidLikeId(startedAt),
            HlxVersion = typeof(AzdoBuildCollector).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AzdoBuildCollector).Assembly.GetName().Version?.ToString()
                ?? "unknown",
            GeneratedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Command = new CollectCommandInfo
            {
                Argv = policy.Argv.Select(AcquisitionRedaction.RedactSensitiveUrls).ToList(),
                Options = policy.Options
            },
            Source = source,
            Auth = new CollectAuthInfo
            {
                Azdo = azdoAuth,
                Helix = helixAuth
            },
            Policy = new CollectPolicyInfo
            {
                RequiredOperations = attempts.Where(a => a.Required).Select(a => a.Operation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                MaxConcurrency = policy.MaxConcurrency,
                Retry = new CollectRetryPolicyInfo
                {
                    MaxAttempts = policy.RetryCount,
                    Kinds = policy.RetryKinds.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList(),
                    InitialDelay = XmlConvertDuration(policy.RetryInitialDelay),
                    MaxDelay = XmlConvertDuration(policy.RetryMaxDelay)
                },
                Caps = new CollectCapsInfo
                {
                    MaxFileBytes = policy.MaxFileBytes,
                    MaxTotalBytes = policy.MaxTotalBytes,
                    MaxTestResults = policy.MaxTestResults ?? CollectPolicy.DefaultMaxTestResults,
                    MaxTestAttachments = policy.MaxTestAttachments ?? CollectPolicy.DefaultMaxTestAttachments
                },
                LogScope = policy.LogScope,
                TestScope = policy.TestScope,
                MaxTestResultsExplicit = policy.MaxTestResults.HasValue,
                TestAttachmentScope = policy.TestAttachmentScope,
                HelixScope = policy.HelixScope
            },
            Cache = new CollectCacheInfo
            {
                Root = _cacheOptions.GetEffectiveCacheRoot(),
                EvalMode = _cacheOptions.EvalMode
            },
            Snapshot = snapshot,
            Complete = complete,
            ExitCode = exitCode,
            IncompleteDetails = incomplete,
            Summary = summary,
            Attempts = attempts
        };
    }

    private async Task<CollectAzdoAuthInfo> GetAzdoAuthInfoAsync(CancellationToken ct)
    {
        var status = await _azdoTokenAccessor.AuthStatusAsync(ct);
        var partition = string.IsNullOrEmpty(_cacheOptions.AuthTokenHash)
            ? "public"
            : $"cache-{_cacheOptions.AuthTokenHash}";
        var replay = partition == "public"
            ? "public"
            : "snapshot_partition";
        return new CollectAzdoAuthInfo
        {
            Path = status.Path,
            CachePartition = partition,
            Replay = replay,
            Warnings = status.Warnings
        };
    }

    private CollectHelixAuthInfo GetHelixAuthInfo()
    {
        var token = _helixTokenAccessor.GetAccessToken();
        var path = string.IsNullOrEmpty(token)
            ? "anonymous"
            : string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN"))
                ? "stored-credential"
                : "environment";
        return new CollectHelixAuthInfo { Path = path };
    }

    private string ResolveManifestPath(CollectPolicy policy)
    {
        if (!string.IsNullOrWhiteSpace(policy.ManifestPath))
            return Path.GetFullPath(policy.ManifestPath);

        // Default next to the cache/export data this run populates, not the current working
        // directory: a CWD default risks an accidental `git add .`/commit of a manifest that
        // records the local auth partition hash and absolute cache paths.
        var root = _cacheOptions.GetEffectiveCacheRoot();
        Directory.CreateDirectory(root);
        return Path.Combine(root, "hlx-collect-manifest.json");
    }

    private static void ValidatePolicy(CollectPolicy policy)
    {
        if (policy.MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "max concurrency must be greater than 0.");
        if (policy.RetryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "retry count must be greater than 0.");
        if (policy.RetryInitialDelay < TimeSpan.Zero || policy.RetryMaxDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(policy), "retry delays must be non-negative.");
        if (!IsOneOf(policy.LogScope, "failed", "all", "none"))
            throw new ArgumentException("Invalid --log-scope. Must be failed, all, or none.", nameof(policy));
        if (!IsOneOf(policy.TestScope, "failed", "all", "none"))
            throw new ArgumentException("Invalid --test-scope. Must be failed, all, or none.", nameof(policy));
        if (policy.MaxTestResults is <= 0 || policy.MaxTestAttachments is <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Test result and attachment budgets must be greater than 0.");
        if (!IsOneOf(policy.TestAttachmentScope, "diagnostic", "all", "none"))
            throw new ArgumentException("Invalid --test-attachment-scope. Must be diagnostic, all, or none.", nameof(policy));
        if (policy.TestAttachmentScope.Equals("all", StringComparison.OrdinalIgnoreCase) && policy.MaxTestAttachments is null)
            throw new ArgumentException("--test-attachment-scope all requires an explicit positive --max-test-attachments.", nameof(policy));
        if (!IsOneOf(policy.HelixScope, "suggested", "none"))
            throw new ArgumentException("Invalid --helix-scope. Must be suggested or none.", nameof(policy));
        if (policy.MaxFileBytes < 0 || policy.MaxTotalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "byte caps must be non-negative.");
    }

    private static bool IsOneOf(string value, params string[] allowed)
        => allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool IsNone(string value)
        => value.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ParseCsv(string value, IReadOnlyList<string> fallback)
    {
        var parsed = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parsed.Length == 0 ? fallback : parsed;
    }

    private static IReadOnlyList<int> SelectLogIds(
        string logScope,
        AzdoTimeline timeline,
        IReadOnlyList<AzdoBuildLogEntry> logsList,
        AzdoEvidencePlan? evidencePlan)
    {
        if (logScope.Equals("all", StringComparison.OrdinalIgnoreCase))
            return logsList.Select(log => log.Id).Distinct().Order().ToList();

        var monitorRecordIds = new HashSet<string>(
            evidencePlan?.HelixFailures
                .SelectMany(f => new[] { f.MonitorJobId, f.MonitorTaskId })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
            ?? [],
            StringComparer.OrdinalIgnoreCase);

        return timeline.Records
            .Where(record =>
                record.Log is not null &&
                (IsFailedOrIssue(record) ||
                 (record.Id is not null && monitorRecordIds.Contains(record.Id))))
            .Select(record => record.Log!.Id)
            .Distinct()
            .Order()
            .ToList();
    }

    private static bool IsFailedOrIssue(AzdoTimelineRecord record)
        => record.Result is not null && !record.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
           || record.Issues is { Count: > 0 };

    private static CollectPagingInfo PagingFromEnvelope<T>(HlxListEnvelope<T> envelope)
        => new()
        {
            Returned = envelope.Returned,
            Total = envelope.Total,
            Offset = envelope.Offset,
            Limit = envelope.Limit,
            Complete = envelope.Complete,
            Truncated = envelope.Truncated,
            Next = envelope.Next
        };

    private static bool IsPolicyAllowedIncomplete(CollectIncompleteDetail detail)
        => detail.Code is "fetch_skipped" or "test_result_limit" or "test_attachment_limit" && detail.Resource is not null;

    private static TimeSpan GetRetryDelay(CollectPolicy policy, AcquisitionError error, int attemptCount)
    {
        if (error.RetryAfterSeconds is > 0 and var retryAfter)
            return TimeSpan.FromSeconds(Math.Min(retryAfter, (int)RetryAfterSafetyCeiling.TotalSeconds));

        var exponentialSeconds = policy.RetryInitialDelay.TotalSeconds * Math.Pow(2, Math.Max(0, attemptCount - 1));
        var jitterMilliseconds = Random.Shared.Next(0, 250);
        var bounded = TimeSpan.FromSeconds(Math.Min(exponentialSeconds, policy.RetryMaxDelay.TotalSeconds));
        return bounded + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    private static async Task ForEachAsync<T>(
        IReadOnlyList<T> items,
        int maxConcurrency,
        Func<T, Task> action,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await action(item);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
    }

    private static async Task<IReadOnlyList<CollectFetchAttempt>> ForEachCollectAsync<T>(
        IReadOnlyList<T> items,
        int maxConcurrency,
        Func<T, List<CollectFetchAttempt>, Task> action,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var indexedTasks = items.Select((item, index) => RunOneAsync(item, index)).ToList();
        var results = await Task.WhenAll(indexedTasks);
        return results
            .OrderBy(result => result.Index)
            .SelectMany(result => result.Attempts)
            .ToList();

        async Task<(int Index, List<CollectFetchAttempt> Attempts)> RunOneAsync(T item, int index)
        {
            await semaphore.WaitAsync(ct);
            var localAttempts = new List<CollectFetchAttempt>();
            try
            {
                await action(item, localAttempts);
                return (index, localAttempts);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private static string HelixWorkItemDetailsKey(string jobId, string workItem)
        => $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItem)}:details";

    private static string HelixFilesKey(string jobId, string workItem)
        => $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItem)}:files";

    private static string HelixConsoleKey(string jobId, string workItem)
        => $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItem)}:console";

    private static string HelixFileKey(string jobId, string workItem, string fileName)
        => $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItem)}:file:{CacheSecurity.SanitizeCacheKeySegment(fileName)}";

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string UlidLikeId(DateTimeOffset timestamp)
        => $"{timestamp:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

    private static string XmlConvertDuration(TimeSpan duration)
        => System.Xml.XmlConvert.ToString(duration);

    private sealed class HelixFileDownloadBudget(long maxTotalBytes)
    {
        private readonly object _gate = new();
        private long _usedBytes;

        public long MaxTotalBytes => maxTotalBytes;

        public long Remaining
        {
            get
            {
                lock (_gate)
                    return Math.Max(0, maxTotalBytes - _usedBytes);
            }
        }

        public bool TryConsume(long bytes)
        {
            lock (_gate)
            {
                if (_usedBytes + bytes > maxTotalBytes)
                    return false;

                _usedBytes += bytes;
                return true;
            }
        }

        public void Release(long bytes)
        {
            if (bytes <= 0)
                return;

            lock (_gate)
            {
                _usedBytes = Math.Max(0, _usedBytes - bytes);
            }
        }
    }
}
