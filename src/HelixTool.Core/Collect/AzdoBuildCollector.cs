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
        await CollectTestsAsync(attempts, priorAttempts, policy, org, project, buildId, ct);

        if (evidencePlan is not null)
        {
            progress?.Invoke("Collecting Helix suggested fetches...");
            await CollectHelixSuggestedFetchesAsync(attempts, priorAttempts, policy, evidencePlan, ct);
        }

        var requiredAttempts = attempts.Where(a => a.Required).ToList();
        foreach (var attempt in requiredAttempts)
        {
            if (attempt.Outcome is "failed" or "recorded_failure")
            {
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = "fetch_failed",
                    Message = attempt.Error?.Message ?? $"{attempt.Operation} failed.",
                    Operation = attempt.Operation,
                    Resource = attempt.Resource
                });
            }
            else if (attempt.Outcome == "skipped" && attempt.Skip?.Kind != "policy_excluded")
            {
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = "fetch_skipped",
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
                var validation = await SnapshotValidator.ValidateAsync(export.Destination, ct);
                manifest = manifest with
                {
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
                merged = currentPlan with
                {
                    HelixFailures = allHelixFailures,
                    HelixFailureOffset = 0,
                    HelixFailuresTruncated = allHelixFailures.Count < currentPlan.HelixFailureTotal
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
        await ForEachAsync(selectedLogIds, policy.MaxConcurrency, async logId =>
        {
            await RunAsync(
                attempts,
                priorAttempts,
                CreateAttempt($"azdo.log:{buildId}:{logId}", "azdo.logs", true, "azdo", "get_build_log",
                    Resource(("org", org), ("project", project), ("buildId", buildId), ("logId", logId)),
                    AzdoCacheKeys.MetadataKey(_cacheOptions, org, project, $"log:{buildId}:{logId}")),
                policy,
                async token => await _azdoService.GetBuildLogAsync(policy.BuildIdOrUrl, logId, tailLines: null, token),
                metadata: true,
                ct);
        }, ct);
    }

    private async Task CollectTestsAsync(
        List<CollectFetchAttempt> attempts,
        IReadOnlyDictionary<string, CollectFetchAttempt> priorAttempts,
        CollectPolicy policy,
        string org,
        string project,
        int buildId,
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
            async token => await _azdoService.GetTestRunsPageAsync(
                policy.BuildIdOrUrl,
                new HlxPageRequest { All = true, Offset = 0, Limit = null },
                ct: token),
            metadata: true,
            ct: ct,
            pagingSelector: value => value is HlxListEnvelope<AzdoTestRun> envelope ? PagingFromEnvelope(envelope) : null);

        if (runsEnvelope is null)
            return;

        var outcomes = policy.TestScope.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? "Passed,Failed,NotExecuted,Inconclusive,Timeout,Aborted,Error,NotRunnable,NotApplicable"
            : "Failed";

        await ForEachAsync(runsEnvelope.Results, policy.MaxConcurrency, async run =>
        {
            var resultsEnvelope = await RunAsync(
                attempts,
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
                pagingSelector: value => value is HlxListEnvelope<AzdoTestResult> envelope ? PagingFromEnvelope(envelope) : null);

            if (resultsEnvelope is null)
                return;

            foreach (var result in resultsEnvelope.Results)
            {
                await RunAsync(
                    attempts,
                    priorAttempts,
                    CreateAttempt($"azdo.test-attachments:{run.Id}:{result.Id}", "azdo.tests", false, "azdo", "list_test_attachments",
                        Resource(("org", org), ("project", project), ("runId", run.Id), ("resultId", result.Id)),
                        AzdoListCacheKeys.TestAttachmentsComplete(_cacheOptions, org, project, run.Id, result.Id)),
                    policy,
                    async token => await _azdoService.GetTestAttachmentsPageAsync(
                        org,
                        project,
                        run.Id,
                        result.Id,
                        new HlxPageRequest { All = true, Offset = 0, Limit = null },
                        ct: token),
                    metadata: true,
                    ct: ct,
                    pagingSelector: value => value is HlxListEnvelope<AzdoTestAttachment> envelope ? PagingFromEnvelope(envelope) : null);
            }
        }, ct);
    }

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
            .SelectMany(failure => failure.SuggestedFetches)
            .Where(fetch => !string.IsNullOrWhiteSpace(fetch.WorkItem))
            .GroupBy(fetch => $"{fetch.Tool}:{fetch.HelixJobId}:{fetch.WorkItem}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        await ForEachAsync(fetches, policy.MaxConcurrency, async fetch =>
        {
            var jobId = HelixIdResolver.ResolveJobId(fetch.HelixJobId);
            var workItem = fetch.WorkItem!;
            switch (fetch.Tool)
            {
                case "helix_work_item":
                    await RunAsync(
                        attempts,
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
                        attempts,
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
                        attempts,
                        priorAttempts,
                        CreateAttempt($"helix.files:{jobId}:{workItem}", "helix.suggested", true, "helix", "list_helix_work_item_files",
                            Resource(("jobId", jobId), ("workItem", workItem)),
                            HelixFilesKey(jobId, workItem)),
                        policy,
                        async token => await _helixService.GetWorkItemFilesAsync(jobId, workItem, token),
                        metadata: true,
                        ct);
                    RecordHelixFileDownloadPolicy(attempts, policy, jobId, workItem, files ?? []);
                    break;
            }
        }, ct);
    }

    private void RecordHelixFileDownloadPolicy(
        List<CollectFetchAttempt> attempts,
        CollectPolicy policy,
        string jobId,
        string workItem,
        IReadOnlyList<HelixService.FileEntry> files)
    {
        foreach (var file in files)
        {
            var selected = policy.DownloadHelixFiles is not null &&
                StringHelpers.MatchesPattern(file.Name, policy.DownloadHelixFiles);
            var skipKind = selected ? "size_limit" : "policy_excluded";
            var message = selected
                ? $"Helix uploaded-file byte download was skipped by --max-file-bytes {policy.MaxFileBytes}."
                : "Helix uploaded-file byte downloads are excluded by default; metadata was collected.";
            AddSkip(attempts, $"helix.file-download:{jobId}:{workItem}:{file.Name}", "helix_files", false, "helix", "download_helix_file",
                Resource(("jobId", jobId), ("workItem", workItem), ("fileName", file.Name)),
                skipKind,
                message,
                cacheKey: HelixFileKey(jobId, workItem, file.Name));
        }
    }

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

        if (policy.Resume && priorAttempts.TryGetValue(template.Id, out var prior))
        {
            if (prior.Outcome is "ok" or "cached" && await CacheEntryExistsAsync(prior.CacheKey, metadata, ct))
            {
                attempts.Add(prior with
                {
                    Outcome = "cached",
                    StartedAt = DateTimeOffset.UtcNow,
                    FinishedAt = DateTimeOffset.UtcNow,
                    DurationMs = 0
                });
                return default;
            }

            if (prior.Error is not null && !policy.RetryKinds.Contains(prior.Error.Kind))
            {
                attempts.Add(prior with
                {
                    Outcome = "recorded_failure",
                    StartedAt = DateTimeOffset.UtcNow,
                    FinishedAt = DateTimeOffset.UtcNow,
                    DurationMs = 0
                });
                return default;
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
                var bytes = Encoding.UTF8.GetByteCount(serialized);
                attempts.Add(template with
                {
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.UtcNow,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    AttemptCount = attemptCount,
                    Outcome = "ok",
                    Bytes = bytes,
                    Sha256 = Sha256Hex(Encoding.UTF8.GetBytes(serialized)),
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
        }

        stopwatch.Stop();
        var error = acquisitionFailure?.Error;
        attempts.Add(template with
        {
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            DurationMs = stopwatch.ElapsedMilliseconds,
            AttemptCount = attemptCount,
            Outcome = error is not null && policy.RetryKinds.Contains(error.Kind) ? "failed" : error is null ? "failed" : "recorded_failure",
            Error = error is not null && policy.RetryKinds.Contains(error.Kind)
                ? error with { Source = $"transient-after-{attemptCount}" }
                : error
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
        attempts.Add(new CollectFetchAttempt
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

    private static async Task<IReadOnlyDictionary<string, CollectFetchAttempt>> LoadPriorAttemptsAsync(
        CollectPolicy policy,
        string manifestPath,
        CancellationToken ct)
    {
        if (!policy.Resume || !File.Exists(manifestPath))
            return new Dictionary<string, CollectFetchAttempt>(StringComparer.Ordinal);

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<CollectManifest>(stream, s_jsonOptions, ct);
        return manifest?.Attempts.ToDictionary(a => a.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, CollectFetchAttempt>(StringComparer.Ordinal);
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
                Argv = policy.Argv,
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
                    MaxTotalBytes = policy.MaxTotalBytes
                },
                LogScope = policy.LogScope,
                TestScope = policy.TestScope,
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
            : status.Path.Contains("environment", StringComparison.OrdinalIgnoreCase)
                ? "environment_token_required"
                : "not_replayable_az_cli";
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

    private static string ResolveManifestPath(CollectPolicy policy)
    {
        if (!string.IsNullOrWhiteSpace(policy.ManifestPath))
            return Path.GetFullPath(policy.ManifestPath);

        var root = Directory.GetCurrentDirectory();
        return Path.Combine(root, "hlx-collect-manifest.json");
    }

    private static void ValidatePolicy(CollectPolicy policy)
    {
        if (policy.MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "max concurrency must be greater than 0.");
        if (policy.RetryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "retry count must be greater than 0.");
        if (!IsOneOf(policy.LogScope, "failed", "all", "none"))
            throw new ArgumentException("Invalid --log-scope. Must be failed, all, or none.", nameof(policy));
        if (!IsOneOf(policy.TestScope, "failed", "all", "none"))
            throw new ArgumentException("Invalid --test-scope. Must be failed, all, or none.", nameof(policy));
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
        => detail.Code is "fetch_skipped" && detail.Resource is not null;

    private static TimeSpan GetRetryDelay(CollectPolicy policy, AcquisitionError error, int attemptCount)
    {
        if (error.RetryAfterSeconds is > 0 and var retryAfter)
            return TimeSpan.FromSeconds(Math.Min(retryAfter, (int)policy.RetryMaxDelay.TotalSeconds));

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
        });
        await Task.WhenAll(tasks);
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
}
