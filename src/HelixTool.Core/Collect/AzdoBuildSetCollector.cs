using System.Text.Json;
using System.Text.Json.Serialization;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;

namespace HelixTool.Core.Collect;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AzdoBuildSetQuery
{
    public int SchemaVersion { get; init; } = 1;
    public string Org { get; init; } = "dnceng-public";
    public string Project { get; init; } = "public";
    public string? DefinitionName { get; init; }
    public int? DefinitionId { get; init; }
    public string? Branch { get; init; }
    public string? PrNumber { get; init; }
    public string? BuildNumber { get; init; }
    public string[]? Tags { get; init; }
    public string? Result { get; init; }
    public string? Reason { get; init; }
    public string? RepositoryId { get; init; }
    public string? RepositoryType { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset? MinTime { get; init; }
    public DateTimeOffset? MaxTime { get; init; }
    public string? QueryOrder { get; init; }

    public AzdoBuildFilter ToFilter()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Build query schemaVersion must be 1.");
        if (string.IsNullOrWhiteSpace(Org) || string.IsNullOrWhiteSpace(Project)) throw new ArgumentException("org and project must not be empty.");
        return AzdoBuildQueries.Normalize(new AzdoBuildFilter
        {
            DefinitionName = DefinitionName, DefinitionId = DefinitionId, Branch = Branch, PrNumber = PrNumber,
            BuildNumber = BuildNumber, Tags = Tags, Result = Result, Reason = Reason,
            RepositoryId = RepositoryId, RepositoryType = RepositoryType, StatusFilter = Status,
            MinTime = MinTime, MaxTime = MaxTime, QueryOrder = QueryOrder
        });
    }
}

public sealed record CollectBuildSetPolicy
{
    public required AzdoBuildSetQuery Query { get; init; }
    public string QueryPath { get; init; } = "";
    public string BuildScope { get; init; } = "evidence";
    public int MaxBuilds { get; init; } = 50;
    public int MaxDefinitions { get; init; } = 25;
    public int MaxScanBuilds { get; init; } = 5000;
    public long MaxSetTestResults { get; init; } = 10000;
    public long MaxSetTestAttachments { get; init; } = 1000;
    public CollectPolicy Collection { get; init; } = new();
}

public sealed partial class AzdoBuildCollector
{
    public async Task<CollectResult> CollectAzdoBuildsAsync(CollectBuildSetPolicy set,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var policy = set.Collection;
        ValidatePolicy(policy);
        if (_cacheOptions.EvalMode) throw new InvalidOperationException("Collection requires live mode; unset HLX_EVAL_SNAPSHOT and export a new snapshot.");
        if (_cacheOptions.MaxSizeBytes <= 0) throw new InvalidOperationException("Build-set collection requires caching.");
        if (set.MaxBuilds <= 0 || set.MaxDefinitions <= 0 || set.MaxScanBuilds <= 0 || set.MaxSetTestResults <= 0 || set.MaxSetTestAttachments <= 0)
            throw new ArgumentException("Collection thresholds must be positive.");
        if (set.BuildScope is not "evidence" and not "metadata") throw new ArgumentException("build-scope must be evidence or metadata.");
        var filter = set.Query.ToFilter();
        var started = DateTimeOffset.UtcNow;
        await ResolveAzdoAuthContextAsync(ct);
        var path = ResolveManifestPath(policy);
        if (policy.Argv.Count == 0)
        {
            var queryPath = string.IsNullOrEmpty(set.QueryPath) ? Path.ChangeExtension(path, "query.json") : Path.GetFullPath(set.QueryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(queryPath)!);
            if (string.IsNullOrEmpty(set.QueryPath)) await File.WriteAllTextAsync(queryPath, JsonSerializer.Serialize(set.Query, AzdoBuildQueries.JsonOptions), ct);
            var argv = new List<string> { "collect", "azdo-builds", "--query", queryPath, "--build-scope", set.BuildScope,
                "--test-scope", policy.TestScope, "--test-attachment-scope", policy.TestAttachmentScope,
                "--log-scope", policy.LogScope, "--helix-scope", policy.HelixScope };
            void Add(string name, object? value) { if (value is not null) { argv.Add(name); argv.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!); } }
            Add("--max-builds", set.MaxBuilds); Add("--max-definitions", set.MaxDefinitions); Add("--max-scan-builds", set.MaxScanBuilds);
            Add("--max-set-test-results", set.MaxSetTestResults); Add("--max-set-test-attachments", set.MaxSetTestAttachments);
            Add("--max-test-results", policy.MaxTestResults); Add("--max-test-attachments", policy.MaxTestAttachments);
            Add("--max-concurrency", policy.MaxConcurrency); Add("--retry-count", policy.RetryCount);
            Add("--retry-kinds", string.Join(",", policy.RetryKinds.Select(k => JsonSerializer.Serialize(k, s_jsonOptions).Trim('"'))));
            Add("--retry-initial-delay", policy.RetryInitialDelay); Add("--retry-max-delay", policy.RetryMaxDelay);
            Add("--artifact-pattern", policy.ArtifactPattern); Add("--artifact-job-prefix", policy.ArtifactJobPrefix);
            Add("--match", policy.Match); Add("--job-results", policy.JobResults);
            if (policy.KeepAttemptPrefix) argv.Add("--keep-attempt-prefix");
            if (policy.AllowIncomplete) argv.Add("--allow-incomplete");
            if (policy.AllowLarge) argv.Add("--allow-large");
            Add("--download-helix-files", policy.DownloadHelixFiles); Add("--download-azdo-artifacts", policy.DownloadAzdoArtifacts);
            Add("--download-artifact-members", policy.DownloadArtifactMembers); Add("--download-test-attachments", policy.DownloadTestAttachments);
            Add("--download-attachment-members", policy.DownloadAttachmentMembers); Add("--artifact-byte-scope", policy.ArtifactByteScope);
            Add("--artifact-byte-mode", policy.ArtifactByteMode); Add("--attachment-content-kind", policy.AttachmentContentKind);
            Add("--max-file-bytes", policy.MaxFileBytes); Add("--max-total-bytes", policy.MaxTotalBytes); Add("--max-total-stream-bytes", policy.MaxTotalStreamBytes);
            Add("--export", policy.ExportPath);
            policy = policy with { Argv = argv };
        }
        CollectManifest? previous = null;
        if (policy.Resume)
        {
            if (!File.Exists(path))
                throw new ArgumentException($"Resume manifest '{path}' is missing. Omit --resume to start a fresh capture.");
            previous = JsonSerializer.Deserialize<CollectManifest>(await File.ReadAllTextAsync(path, ct), s_jsonOptions);
            AzdoBuildSetQuery? priorSemantic = null;
            if (previous?.Command.Options.TryGetValue("semanticQuery", out var priorQuery) == true)
                priorSemantic = priorQuery is JsonElement element ? element.Deserialize<AzdoBuildSetQuery>(AzdoBuildQueries.JsonOptions) : priorQuery as AzdoBuildSetQuery;
            if (previous?.Kind != "hlx.collect.azdo-builds" || previous.Source.Org != set.Query.Org || previous.Source.Project != set.Query.Project ||
                priorSemantic is null || JsonSerializer.Serialize(priorSemantic, AzdoBuildQueries.JsonOptions) != JsonSerializer.Serialize(set.Query, AzdoBuildQueries.JsonOptions))
                throw new ArgumentException("Resume requires the original semantic query and build-set manifest.");
        }
        var attempts = new List<CollectFetchAttempt>();
        var incomplete = new List<CollectIncompleteDetail>();
        var members = new List<CollectBuildMember>();
        var reports = new List<AzdoDownloadReport>();
        var source = previous?.Source ?? new CollectSourceInfo { Org = set.Query.Org, Project = set.Query.Project, Query = filter };
        var finished = false;
        var command = new CollectCommandInfo
        {
            Name = "hlx collect azdo-builds", Argv = policy.Argv,
            Options = new Dictionary<string, object?>(policy.Options) { ["semanticQuery"] = set.Query, ["buildScope"] = set.BuildScope }
        };
        var manifest = new CollectManifest
        {
            SchemaVersion = 2, Kind = "hlx.collect.azdo-builds", ManifestId = previous?.ManifestId ?? Guid.NewGuid().ToString("N"),
            GeneratedAt = started, Command = command, Source = source,
            Auth = new CollectAuthInfo { Azdo = await GetAzdoAuthInfoAsync(ct), Helix = GetHelixAuthInfo() }
        };
        async Task SaveAsync()
        {
            var complete = finished && incomplete.Count == 0;
            var exitCode = !finished ? 2 :
                members.Any(m => m.ExitCode == 1) || incomplete.Any(d => d.Code is "collector_hard_error" or "snapshot_validation_failed") ? 1 :
                complete || policy.AllowIncomplete && incomplete.All(IsPolicyAllowedIncomplete) ? 0 : 2;
            var framework = BuildManifest(policy, started, source, manifest.Auth.Azdo, manifest.Auth.Helix, attempts, incomplete,
                complete, exitCode, manifest.Snapshot);
            manifest = manifest with
            {
                HlxVersion = framework.HlxVersion, Summary = framework.Summary, Cache = framework.Cache,
                Policy = framework.Policy with { Caps = framework.Policy.Caps with
                {
                    MaxBuilds = set.MaxBuilds, MaxDefinitions = set.MaxDefinitions, MaxScanBuilds = set.MaxScanBuilds,
                    MaxSetTestResults = set.MaxSetTestResults, MaxSetTestAttachments = set.MaxSetTestAttachments,
                    MaxTestAttachments = policy.MaxTestAttachments ?? set.MaxSetTestAttachments
                } },
                Source = source, Attempts = attempts.OrderBy(a => a.Phase, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal).ToArray(),
                Builds = members.ToArray(), ByteDownloads = reports.Count == 0 ? null : reports.ToArray(),
                IncompleteDetails = finished ? incomplete.ToArray() : incomplete.Concat([new CollectIncompleteDetail
                {
                    Code = "collection_in_progress", Message = "Checkpoint retained; resume the original query: " + string.Join(" ", RecoveryArgv(policy).Select(QuoteArgument))
                }]).ToArray(),
                Complete = complete,
                ExitCode = exitCode, CompletedAt = finished ? DateTimeOffset.UtcNow : null
            };
            await WriteManifestAsync(path, manifest, ct);
        }
        try
        {
            AzdoBuildQueryResult? discovery = null;
            string? discoveryError = null;
            Exception? discoveryInternalError = null;
            await RunAsync(attempts, new Dictionary<string, CollectFetchAttempt>(),
                CreateAttempt("azdo.builds", "discovery", true, "azdo", "list_builds",
                    Resource(("org", set.Query.Org), ("project", set.Query.Project)), null),
                policy, async acquisitionToken =>
                {
                    try
                    {
                        discovery = await _azdoClient.QueryBuildsAsync(set.Query.Org, set.Query.Project,
                            new AzdoBuildQueryRequest
                            {
                                Filter = filter, All = true, Frozen = policy.Resume || source.Discovery is not null, ResumeCacheKey = source.Discovery?.CacheKey,
                                ResumeCapturedAt = source.Discovery?.CapturedAt, ResumeViewId = source.Discovery?.ViewId,
                                ResumeDescriptorSha256 = source.Discovery?.Sha256 ??
                                    previous?.Attempts.FirstOrDefault(a => a.CacheKey == source.Discovery?.CacheKey && a.Sha256 is not null)?.Sha256,
                                FreshCapture = !policy.Resume && source.Discovery is null,
                                MaxDiscoveryBuilds = policy.AllowLarge ? null : set.MaxBuilds,
                                MaxScanBuilds = policy.AllowLarge || set.MaxBuilds > 50 && set.MaxScanBuilds == 5000 ? null : set.MaxScanBuilds,
                                MaxDefinitions = policy.AllowLarge || set.MaxBuilds > 50 && set.MaxDefinitions == 25 ? null : set.MaxDefinitions,
                                FrozenDefinitions = source.Discovery is not null ? source.ResolvedDefinitions : null,
                                Checkpoint = async (checkpoint, key, token) =>
                                {
                                    source = source with
                                    {
                                        Query = checkpoint.Query, EffectiveQuery = checkpoint.EffectiveQuery, QueryHash = checkpoint.QueryHash,
                                        ResolvedDefinitions = checkpoint.ResolvedDefinitions,
                                        Discovery = new CollectBuildDiscovery(key, checkpoint.SourceComplete, checkpoint.SourceComplete ? checkpoint.SelectedCount : null,
                                            checkpoint.SelectedCount, checkpoint.Scanned, checkpoint.PageKeys.Count, checkpoint.CapturedAt,
                                            AzdoBuildQueries.Digest(checkpoint.QueryHash + ":" + checkpoint.ViewId + ":full"),
                                            AzdoBuildQueries.Digest(JsonSerializer.Serialize(checkpoint, AzdoBuildQueries.JsonOptions)))
                                    };
                                    await SaveAsync();
                                }
                            }, progress, acquisitionToken);
                        return new CollectBuildDiscovery(discovery.CacheKey!, discovery.SourceComplete, discovery.Total,
                            discovery.Count, discovery.Scanned, discovery.Pages, discovery.CapturedAt, discovery.ViewId);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
                    {
                        discoveryError = ex.Message;
                        throw;
                    }
                    catch (Exception ex) when (ex is not Acquisition.HlxAcquisitionException && ex is not OperationCanceledException)
                    {
                        discoveryInternalError = ex;
                        throw;
                    }
                }, metadata: true, ct);
            if (discovery is null)
            {
                if (discoveryError is not null)
                {
                    var argv = RecoveryArgv(policy).ToList();
                    for (var i = argv.Count - 1; i >= 0; i--)
                    {
                        if (argv[i].StartsWith("--resume=", StringComparison.Ordinal)) argv.RemoveAt(i);
                        else if (argv[i] == "--resume")
                        {
                            if (i + 1 < argv.Count && bool.TryParse(argv[i + 1], out _)) argv.RemoveAt(i + 1);
                            argv.RemoveAt(i);
                        }
                    }
                    var manifestFlag = argv.IndexOf("--manifest");
                    var freshPath = Path.ChangeExtension(path, $"recaptured-{Guid.NewGuid():N}.json");
                    if (manifestFlag >= 0) argv[manifestFlag + 1] = freshPath;
                    else
                    {
                        var assignedManifest = argv.FindIndex(arg => arg.StartsWith("--manifest=", StringComparison.Ordinal));
                        argv[assignedManifest] = "--manifest=" + freshPath;
                    }
                    var recoveryCommand = string.Join(" ", argv.Select(QuoteArgument));
                    incomplete.Add(new CollectIncompleteDetail
                    {
                        Code = "discovery_recapture_required",
                        Message = discoveryError + " Start a fresh capture (omit --resume): " + recoveryCommand,
                        Resource = new Dictionary<string, object?>
                        {
                            ["argv"] = argv.ToArray(),
                            ["command"] = recoveryCommand,
                            ["manifestPath"] = freshPath,
                            ["executionHost"] = OperatingSystem.IsWindows() ? "powershell" : "posix"
                        }
                    });
                    finished = true;
                    await SaveAsync();
                    return new CollectResult(manifest, path, manifest.Snapshot.Path);
                }
                if (discoveryInternalError is not null) throw discoveryInternalError;
                var failure = attempts.LastOrDefault(a => a.Id == "azdo.builds")?.Error;
                if (failure is not null) throw new Acquisition.HlxAcquisitionException(failure);
                throw new InvalidOperationException("Build discovery did not complete; see the discovery attempt.");
            }
            var discoveryJson = await _cacheStore.GetMetadataIgnoringTtlAsync(discovery.CacheKey!, ct)
                ?? throw new InvalidDataException("Discovery checkpoint is missing from the cache.");
            source = source with
            {
                Query = discovery.Query, EffectiveQuery = discovery.EffectiveQuery, QueryHash = discovery.QueryHash,
                ResolvedDefinitions = discovery.ResolvedDefinitions,
                Discovery = new CollectBuildDiscovery(discovery.CacheKey!, discovery.SourceComplete, discovery.Total,
                    discovery.Count, discovery.Scanned, discovery.Pages, discovery.CapturedAt, discovery.ViewId, AzdoBuildQueries.Digest(discoveryJson))
            };
            attempts.Add(new CollectFetchAttempt
            {
                Id = "azdo.builds:" + discovery.QueryHash, Phase = "discovery", Required = true, Provider = "azdo", Operation = "list_builds",
                CacheKey = discovery.CacheKey, CompleteCacheKey = discovery.SourceComplete ? discovery.CacheKey : null,
                Resource = Resource(("org", set.Query.Org), ("project", set.Query.Project)), Outcome = "ok",
                Bytes = System.Text.Encoding.UTF8.GetByteCount(discoveryJson), Sha256 = AzdoBuildQueries.Digest(discoveryJson),
                Paging = new CollectPagingInfo { Returned = discovery.Count, Total = discovery.Total, Complete = discovery.SourceComplete, Truncated = !discovery.SourceComplete }
            });
            var planned = policy.AllowLarge ? discovery.Count : Math.Min(discovery.Count, set.MaxBuilds);
            var deferred = !discovery.SourceComplete || planned < discovery.Count;
            if (deferred)
            {
                var argv = RecoveryArgv(policy).ToArray();
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = "build_set_threshold", Message = "Discovery membership retained. Continue the same query: " + string.Join(" ", argv.Select(QuoteArgument)),
                    Resource = new Dictionary<string, object?>
                    {
                        ["argv"] = argv,
                        ["manifestPath"] = path,
                        ["executionHost"] = OperatingSystem.IsWindows() ? "powershell" : "posix",
                        ["command"] = string.Join(" ", argv.Select(QuoteArgument))
                    }
                });
                if (!discovery.SourceComplete && discovery.Count == 0) planned = 0;
            }
            var selected = discovery.Results.Take(planned).Select(build => build.Id).ToArray();
            var setBudget = new CollectSetBudget(set.MaxSetTestResults, set.MaxSetTestAttachments);
            // One build at a time; the existing phase semaphore bounds total run concurrency, never N builds times N phases.
            foreach (var buildId in selected)
            {
                ct.ThrowIfCancellationRequested();
                var url = $"https://dev.azure.com/{Uri.EscapeDataString(set.Query.Org)}/{Uri.EscapeDataString(set.Query.Project)}/_build/results?buildId={buildId}";
                progress?.Invoke($"Collecting build {buildId} ({members.Count + 1}/{selected.Length})...");
                if (set.BuildScope == "metadata")
                {
                    var fetched = await RunAsync(attempts, previous?.Attempts.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal) ?? new Dictionary<string, CollectFetchAttempt>(),
                        CreateAttempt($"build:{buildId}:azdo.build", "azdo_root", true, "azdo", "get_build",
                            Resource(("org", set.Query.Org), ("project", set.Query.Project), ("buildId", buildId)),
                            AzdoCacheKeys.MetadataKey(_cacheOptions, set.Query.Org, set.Query.Project, $"build:{buildId}")),
                        policy, token => _azdoClient.GetBuildAsync(set.Query.Org, set.Query.Project, buildId, token), metadata: true, ct);
                    members.Add(new CollectBuildMember(buildId, url, fetched is not null, fetched is not null ? 0 : 2, $"build:{buildId}:"));
                }
                else
                {
                    var prefix = $"build:{buildId}:";
                    var prior = previous?.Attempts.Where(a => a.Id.StartsWith(prefix, StringComparison.Ordinal))
                        .ToDictionary(a => a.Id[prefix.Length..], a => a, StringComparer.Ordinal);
                    var memberPolicy = policy with
                    {
                        BuildIdOrUrl = url, ManifestPath = path, PersistManifest = false, ExportPath = null, PriorAttempts = prior, SetBudget = setBudget,
                        MaxTestAttachments = policy.MaxTestAttachments ?? set.MaxSetTestAttachments,
                        DownloadAzdoArtifacts = null, DownloadArtifactMembers = null, DownloadTestAttachments = null, DownloadHelixFiles = null,
                        ArtifactByteScope = "auto", ArtifactByteMode = "auto"
                    };
                    var result = await CollectAzdoBuildAsync(memberPolicy, progress, ct);
                    foreach (var attempt in result.Manifest.Attempts)
                        attempts.Add(attempt with
                        {
                            Id = prefix + attempt.Id, ParentId = attempt.ParentId is null ? null : prefix + attempt.ParentId,
                            Resource = new Dictionary<string, object?>(attempt.Resource) { ["buildId"] = buildId }
                        });
                    incomplete.AddRange(result.Manifest.IncompleteDetails.Select(detail => detail with
                    {
                        Resource = new Dictionary<string, object?>(detail.Resource ?? new Dictionary<string, object?>()) { ["buildId"] = buildId }
                    }));
                    members.Add(new CollectBuildMember(buildId, url, result.Manifest.Complete, result.Manifest.ExitCode, $"build:{buildId}:"));
                }
                await SaveAsync();
            }
            long byteCount = 0;
            long streamCount = 0;
            if (set.BuildScope == "evidence")
            {
                foreach (var buildId in selected)
                {
                    var bytePolicy = policy with
                    {
                        BuildIdOrUrl = $"https://dev.azure.com/{Uri.EscapeDataString(set.Query.Org)}/{Uri.EscapeDataString(set.Query.Project)}/_build/results?buildId={buildId}",
                        MaxTotalBytes = Math.Max(0, policy.MaxTotalBytes - byteCount),
                        MaxTotalStreamBytes = Math.Max(0, policy.MaxTotalStreamBytes - streamCount)
                    };
                    var byteAttempts = new List<CollectFetchAttempt>();
                    var downloaded = new List<AzdoDownloadReport>();
                    try
                    {
                        AzdoEvidencePlan? plan = null;
                        if (policy.DownloadHelixFiles is not null || policy.ArtifactByteScope != "auto" || policy.ArtifactByteMode != "auto")
                        {
                            var preparation = new List<CollectFetchAttempt>();
                            plan = await CollectEvidencePlanPagesAsync(preparation, new Dictionary<string, CollectFetchAttempt>(), incomplete,
                                bytePolicy, set.Query.Org, set.Query.Project, buildId, ct);
                            if (policy.DownloadHelixFiles is not null && plan is not null)
                            {
                                await CollectHelixSuggestedFetchesAsync(preparation, new Dictionary<string, CollectFetchAttempt>(), bytePolicy, plan, ct);
                                var helixBytes = preparation.Where(a => a.Operation == "download_helix_file").ToArray();
                                byteAttempts.AddRange(helixBytes);
                                var used = helixBytes.Where(a => a.Outcome is "ok" or "cached").Sum(a => a.Bytes ?? 0);
                                byteCount += used;
                            }
                        }
                        await CollectAzdoBytesAsync(byteAttempts, bytePolicy, set.Query.Org, set.Query.Project, buildId, plan, progress, ct, downloaded);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        var failure = BuildHardErrorManifest(bytePolicy, started, source, [], [], ex);
                        byteAttempts.AddRange(failure.Attempts.Select(a => a with { Id = "azdo.byte-phase", Phase = "azdo_bytes" }));
                        incomplete.AddRange(failure.IncompleteDetails.Select(detail => detail with
                        {
                            Code = ex is Acquisition.HlxAcquisitionException ? "fetch_failed" : detail.Code,
                            Resource = new Dictionary<string, object?>(detail.Resource ?? new Dictionary<string, object?>()) { ["buildId"] = buildId }
                        }));
                        var memberIndex = members.FindIndex(m => m.BuildId == buildId);
                        members[memberIndex] = members[memberIndex] with { Complete = false,
                            ExitCode = members[memberIndex].ExitCode == 1 || ex is not Acquisition.HlxAcquisitionException ? 1 : 2 };
                    }
                    finally
                    {
                        byteCount += downloaded.Sum(r => r.Bytes);
                        streamCount += downloaded.Sum(r => r.StreamedBytes);
                        reports.AddRange(downloaded);
                    }
                    foreach (var attempt in byteAttempts)
                    {
                        var id = $"build:{buildId}:" + attempt.Id;
                        attempts.RemoveAll(a => a.Id == id);
                        attempts.Add(attempt with { Id = id, ParentId = attempt.ParentId is null ? null : $"build:{buildId}:" + attempt.ParentId,
                            Resource = new Dictionary<string, object?>(attempt.Resource) { ["buildId"] = buildId } });
                    }
                    await SaveAsync();
                }
            }
            attempts = await VerifyCollectedEvidenceAsync(attempts, ct);
            foreach (var attempt in attempts.Where(a => a.Required && a.Outcome is not "ok" and not "cached" &&
                !(a.Outcome == "skipped" && a.Skip?.Kind == "policy_excluded")))
            {
                var code = attempt.Outcome == "skipped" ? attempt.Skip?.Kind ?? "fetch_skipped" : "fetch_failed";
                if (incomplete.Any(d => d.Code == code && d.Operation == attempt.Operation && d.Resource is not null &&
                    d.Resource.TryGetValue("buildId", out var id) && attempt.Resource.TryGetValue("buildId", out var buildId) && Equals(id, buildId)))
                    continue;
                incomplete.Add(new CollectIncompleteDetail
                {
                    Code = code,
                    Message = attempt.Error?.Message ?? attempt.Skip?.Message ?? "Required evidence failed verification.",
                    Operation = attempt.Operation, Resource = attempt.Resource
                });
            }
            for (var i = 0; i < members.Count; i++)
            {
                var prefix = members[i].AttemptIdPrefix;
                if (attempts.Any(a => a.Id.StartsWith(prefix, StringComparison.Ordinal) && a.Required && a.Outcome is not "ok" and not "cached" &&
                    !(a.Outcome == "skipped" && a.Skip?.Kind == "policy_excluded")))
                    members[i] = members[i] with { Complete = false, ExitCode = members[i].ExitCode == 1 ? 1 :
                        policy.AllowIncomplete && incomplete.Where(d => d.Resource?.TryGetValue("buildId", out var id) == true && Equals(id, members[i].BuildId)).All(IsPolicyAllowedIncomplete) ? 0 : 2 };
            }
            finished = true;
            await SaveAsync();
            if (policy.ExportPath is not null)
            {
                await RefreshDiscoveryAsync(source, ct);
                var exported = await SnapshotExporter.ExportAsync(_cacheOptions.GetEffectiveCacheRoot(), Path.GetFullPath(policy.ExportPath), progress, ct);
                var target = Path.Combine(exported.Destination, "manifest", "hlx-collect-manifest.json");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target, overwrite: true);
                var validation = await SnapshotValidator.ValidateAsync(exported.Destination, ct);
                manifest = manifest with { Snapshot = new CollectSnapshotInfo
                {
                    Exported = true, Path = exported.Destination, ManifestPath = "manifest/hlx-collect-manifest.json",
                    Validated = validation.IsValid, ValidationErrors = validation.Errors, ValidationWarnings = validation.Warnings
                } };
                if (!validation.IsValid) incomplete.Add(new CollectIncompleteDetail { Code = "snapshot_validation_failed", Message = string.Join("; ", validation.Errors) });
                await SaveAsync(); File.Copy(path, target, overwrite: true);
            }
            return new CollectResult(manifest, path, manifest.Snapshot.Path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            incomplete.Add(new CollectIncompleteDetail { Code = "cancelled", Message = "Collection interrupted. Resume the same query: " + string.Join(" ", RecoveryArgv(policy).Select(QuoteArgument)) });
            // Preserve successfully acquired work even when the caller token is cancelled.
            manifest = manifest with { Source = source, Attempts = attempts, Builds = members, Complete = false, ExitCode = 2, IncompleteDetails = incomplete };
            await WriteManifestAsync(path, manifest, CancellationToken.None);
            throw;
        }
        catch (Acquisition.HlxAcquisitionException ex)
        {
            incomplete.Add(new CollectIncompleteDetail { Code = "fetch_failed", Message = ex.Message });
            finished = true;
            await SaveAsync();
            return new CollectResult(manifest, path, manifest.Snapshot.Path);
        }
        catch (Exception ex)
        {
            incomplete.Add(new CollectIncompleteDetail { Code = "collector_hard_error", Message = ex.Message });
            finished = true;
            await SaveAsync();
            manifest = manifest with { ExitCode = 1 }; await WriteManifestAsync(path, manifest, ct);
            return new CollectResult(manifest, path, manifest.Snapshot.Path);
        }
    }
    private async Task RefreshDiscoveryAsync(CollectSourceInfo source, CancellationToken ct)
    {
        var key = source.Discovery?.CacheKey ?? throw new InvalidDataException("Missing discovery key.");
        var json = await _cacheStore.GetMetadataIgnoringTtlAsync(key, ct) ?? throw new InvalidDataException("Missing discovery evidence.");
        var index = JsonSerializer.Deserialize<AzdoBuildQuerySource>(json, AzdoBuildQueries.JsonOptions) ?? throw new InvalidDataException("Invalid discovery index.");
        if (index.QueryHash != source.QueryHash || index.QueryHash != AzdoBuildQueries.Hash(index.Query)) throw new InvalidDataException("Discovery membership query hash mismatch.");
        foreach (var pageKey in index.PageKeys)
        {
            if (!pageKey.StartsWith(AzdoCacheKeys.MetadataKey(_cacheOptions, source.Org!, source.Project!, $"builds:v2:{index.QueryHash}:page:"), StringComparison.Ordinal)) throw new InvalidDataException("Discovery page partition mismatch.");
            var page = await _cacheStore.GetMetadataIgnoringTtlAsync(pageKey, ct) ?? throw new InvalidDataException("Missing discovery page.");
            if (!index.PageHashes.TryGetValue(pageKey, out var hash) || AzdoBuildQueries.Digest(page) != hash) throw new InvalidDataException("Discovery page hash mismatch.");
            await _cacheStore.SetMetadataAsync(pageKey, page, TimeSpan.FromHours(4), ct);
        }
        await _cacheStore.SetMetadataAsync(key, json, TimeSpan.FromHours(4), ct);
        if (index.Query.DefinitionName is not null)
        {
            var definitionKey = AzdoCacheKeys.MetadataKey(_cacheOptions, source.Org!, source.Project!, $"definitions:v1:{AzdoBuildQueries.DefinitionHash(index.Query)}:all");
            await _cacheStore.SetMetadataAsync(definitionKey, JsonSerializer.Serialize(new AzdoDefinitionQuerySource(index.ResolvedDefinitions, index.CapturedAt, true), AzdoBuildQueries.JsonOptions), TimeSpan.FromHours(4), ct);
        }
    }

}
