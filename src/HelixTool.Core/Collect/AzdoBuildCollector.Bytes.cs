using HelixTool.Core.AzDO;
using HelixTool.Core.Delivery;
using System.Text.Json;

namespace HelixTool.Core.Collect;

public sealed partial class AzdoBuildCollector
{
    private async Task<List<AzdoDownloadReport>> CollectAzdoBytesAsync(List<CollectFetchAttempt> attempts,
        CollectPolicy policy, string org, string project, int buildId, AzdoEvidencePlan? evidencePlan,
        Action<string>? progress, CancellationToken ct)
    {
        var reports = new List<AzdoDownloadReport>();
        var artifactsRequested = policy.DownloadAzdoArtifacts is not null || policy.DownloadArtifactMembers is not null ||
            policy.ArtifactByteScope != "auto" || policy.ArtifactByteMode != "auto";
        if (!artifactsRequested && policy.DownloadTestAttachments is null) return reports;
        await _cacheStore.RetainArtifactsAsync(TimeSpan.FromHours(24), ct);
        var service = new AzdoDownloadService(_azdoClient, _cacheStore, _cacheOptions, new FileEvidenceDeliveryStore(_cacheOptions));
        long retained = attempts.Where(a => a.Operation == "download_helix_file" && a.Outcome is "ok" or "cached").Sum(a => a.Bytes ?? 0);
        long streamed = 0;
        AzdoDownloadOptions Options() => new()
        {
            All = true,
            AllMembers = true,
            Delivery = "file",
            Resume = true,
            AllowLarge = policy.AllowLarge,
            MaxTotalBytes = policy.AllowLarge ? null : Math.Max(1, policy.MaxTotalBytes - retained),
            MaxTotalStreamBytes = policy.AllowLarge ? null : Math.Max(1, policy.MaxTotalStreamBytes - streamed)
        };

        async Task RecordAsync(AzdoDownloadReport report, bool requireMatch = false)
        {
            if (report.Total != 0 || requireMatch) reports.Add(report);
            retained += report.Bytes;
            streamed += report.StreamedBytes;
            foreach (var result in report.Results)
            {
                var resource = Resource(("buildId", buildId), ("runId", result.RunId), ("resultId", result.ResultId),
                    ("artifactId", result.ArtifactId), ("attachmentId", result.AttachmentId), ("name", result.Name));
                // Keep completed members even when a later selected member failed/deferred.
                foreach (var member in result.Members.Where(m => m.Outcome is "ok" or "cached"))
                    AddByteAttempt(member.CacheKey, member.Bytes, member.Sha256, member.Outcome,
                        new Dictionary<string, object?>(resource) { ["memberPath"] = member.MemberPath });
                if (result.Outcome is not ("ok" or "cached"))
                {
                    var recovery = RecoveryArgv(policy);
                    attempts.Add(new CollectFetchAttempt
                    {
                        Id = $"azdo.bytes:{reports.Count}:{result.Name}",
                        Phase = "azdo_bytes",
                        Required = true,
                        Provider = "azdo",
                        Operation = "download_azdo_bytes",
                        Resource = resource,
                        StartedAt = DateTimeOffset.UtcNow,
                        FinishedAt = DateTimeOffset.UtcNow,
                        AttemptCount = 1,
                        Outcome = result.Outcome,
                        Error = result.Skip is null ? result.Error : null,
                        Skip = result.Skip is null ? null : new CollectSkipInfo
                        {
                            Kind = result.Skip,
                            Message = $"Byte acquisition deferred. Resume the original scope: {string.Join(" ", recovery.Select(QuoteArgument))}",
                            Recovery = new { kind = result.Skip, argv = recovery, command = string.Join(" ", recovery.Select(QuoteArgument)) }
                        }
                    });
                    continue;
                }
                if (result.CacheKey is not null)
                    AddByteAttempt(result.CacheKey, result.Bytes, result.Sha256, result.Outcome, resource);
                if (result.Source?.TransferMode == "zip_selective")
                {
                    var suffix = result.ArtifactId is int artifactId ? $"artifactmembers:v1:{buildId}:{artifactId}:all"
                        : $"testattachmentmembers:v1:{result.RunId}:{result.Scope}:{result.ResultId ?? 0}:{result.AttachmentId}:all";
                    var catalogKey = AzdoCacheKeys.MetadataKey(_cacheOptions, org, project, suffix);
                    var json = await _cacheStore.GetMetadataAsync(catalogKey, ct);
                    if (json is not null)
                        attempts.Add(new CollectFetchAttempt
                        {
                            Id = catalogKey,
                            Phase = "azdo_bytes",
                            Required = true,
                            Provider = "azdo",
                            Operation = "catalog_azdo_members",
                            Resource = resource,
                            CacheKey = catalogKey,
                            CompleteCacheKey = catalogKey,
                            Outcome = "ok",
                            AttemptCount = 1,
                            StartedAt = DateTimeOffset.UtcNow,
                            FinishedAt = DateTimeOffset.UtcNow
                        });
                }
            }
            if (requireMatch && (report.Total == 0 || report.Results.All(r =>
                r.Format == "members" && r.MembersTotal == 0 && r.Outcome is "ok" or "cached")))
                AddSkip(attempts, $"azdo.bytes:no-matches:{reports.Count}", "azdo_bytes", true, "azdo", "download_azdo_bytes",
                    Resource(("buildId", buildId)), "no_matches",
                    "Explicit byte selection matched no resources or members. Inspect the complete artifact/attachment metadata index and revise the selector.");
        }

        void AddByteAttempt(string key, long bytes, string? sha, string outcome, IReadOnlyDictionary<string, object?> resource)
        {
            attempts.Add(new CollectFetchAttempt
            {
                Id = key,
                Phase = "azdo_bytes",
                Required = true,
                Provider = "azdo",
                Operation = "download_azdo_bytes",
                Resource = resource,
                CacheKey = key,
                CompleteCacheKey = key,
                Bytes = bytes,
                Sha256 = sha,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                AttemptCount = 1,
                Outcome = outcome
            });
        }

        if (artifactsRequested)
        {
            progress?.Invoke("Collecting opted-in AzDO artifact bytes...");
            var selected = policy.ArtifactByteScope == "selected" ||
                policy.ArtifactByteScope == "auto" && policy.DownloadAzdoArtifacts is not null;
            var members = policy.ArtifactByteMode == "members" || policy.DownloadArtifactMembers is not null ||
                policy.ArtifactByteMode == "auto" && !selected;
            var memberPattern = members ? policy.DownloadArtifactMembers ?? "**/Build.binlog" : null;
            if (selected)
            {
                await RecordAsync(await service.DownloadArtifactsAsync(policy.BuildIdOrUrl,
                    Options() with { Pattern = policy.DownloadAzdoArtifacts ?? "*", MemberPattern = memberPattern }, progress, ct), requireMatch: true);
            }
            else
            {
                // Use the existing GUID/name matching model, never monitor names as fabricated artifact evidence.
                var all = await _azdoClient.GetBuildArtifactsAsync(org, project, buildId, ct);
                var timeline = await _azdoClient.GetTimelineAsync(org, project, buildId, ct);
                var jobs = timeline?.Records.Where(r => r.Type?.Equals("Job", StringComparison.OrdinalIgnoreCase) == true &&
                    (r.Result?.Equals("failed", StringComparison.OrdinalIgnoreCase) == true ||
                     r.Result?.Equals("canceled", StringComparison.OrdinalIgnoreCase) == true)).ToArray() ?? [];
                if (timeline is not null)
                {
                    var monitors = AzdoMonitorFailureParser.ScanTimeline(timeline, jobs.Select(j => j.Id ?? "").ToList())
                        .MonitorJobs.Where(j => j.Failures.Count > 0).Select(j => j.MonitorJobId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    jobs = jobs.Where(j => j.Id is null || !monitors.Contains(j.Id)).ToArray();
                }
                var matched = AzdoEvidenceMatcher.BuildPlanFromSelectedJobs(jobs, all, new AzdoEvidencePlanOptions
                {
                    ArtifactJobPrefix = "Logs_Build_",
                    Match = policy.Match
                }, includeAll: true);
                var ids = matched.Entries.SelectMany(e => e.Candidates).Select(c => c.ArtifactId).ToHashSet();
                var eligible = all.Where(a => a.Name?.StartsWith("Logs_Build_", StringComparison.OrdinalIgnoreCase) == true &&
                    ids.Contains(a.Id) && (policy.DownloadAzdoArtifacts is null || StringHelpers.MatchesPattern(a.Name, policy.DownloadAzdoArtifacts))).ToArray();
                foreach (var artifact in eligible)
                    await RecordAsync(await service.DownloadArtifactsAsync(policy.BuildIdOrUrl,
                        Options() with { ArtifactName = artifact.Name, MemberPattern = memberPattern }, progress, ct), requireMatch: true);
                if (eligible.Length == 0)
                    AddSkip(attempts, "azdo.bytes:no-eligible-failed-logs", "azdo_bytes", false, "azdo", "download_azdo_bytes",
                        Resource(("buildId", buildId)), "policy_excluded",
                        $"no_eligible_failed_logs: triage selected no artifacts. All bytes remain accessible: hlx azdo download-artifacts {QuoteArgument(policy.BuildIdOrUrl)} --all --delivery file --json");
            }
        }
        if (policy.DownloadTestAttachments is not null)
        {
            var initialReports = reports.Count;
            progress?.Invoke("Collecting explicitly selected run/result attachment bytes (independent of broad --test-scope)...");
            var runs = await _azdoClient.GetTestRunsAsync(org, project, buildId, int.MaxValue, ct);
            foreach (var run in runs)
            {
                await RecordAsync(await service.DownloadTestAttachmentsAsync(run.Id, null, org, project,
                    Options() with
                    {
                        Pattern = policy.DownloadTestAttachments,
                        MemberPattern = policy.DownloadAttachmentMembers,
                        ContentKind = policy.AttachmentContentKind
                    }, progress, ct));
                // Explicit filename/member selection must inspect its actual scope, even with broad tests disabled.
                var results = await _azdoClient.GetTestResultsAsync(org, project, run.Id, int.MaxValue, outcomes: null, ct);
                foreach (var result in results)
                    await RecordAsync(await service.DownloadTestAttachmentsAsync(run.Id, result.Id, org, project,
                        Options() with
                        {
                            Pattern = policy.DownloadTestAttachments,
                            MemberPattern = policy.DownloadAttachmentMembers,
                            ContentKind = policy.AttachmentContentKind
                        }, progress, ct));
            }
            var selectedAttachments = reports.Skip(initialReports).SelectMany(report => report.Results);
            if (!selectedAttachments.Any(result => policy.DownloadAttachmentMembers is null || result.MembersTotal > 0))
                AddSkip(attempts, "azdo.attachment-bytes:no-matches", "azdo_bytes", true, "azdo", "download_azdo_bytes",
                    Resource(("buildId", buildId), ("pattern", policy.DownloadTestAttachments),
                        ("memberPattern", policy.DownloadAttachmentMembers)), "no_matches",
                    "Explicit attachment byte selection matched no files or selected ZIP members in the complete run/result scope.");
        }
        return reports;
    }

    private IReadOnlyList<string> RecoveryArgv(CollectPolicy policy)
    {
        var argv = policy.Argv.Count > 0 ? policy.Argv.ToList() : new List<string> { "collect", "azdo-build", policy.BuildIdOrUrl };
        if (policy.Argv.Count == 0)
        {
            Add("--download-helix-files", policy.DownloadHelixFiles);
            Add("--download-azdo-artifacts", policy.DownloadAzdoArtifacts);
            Add("--download-artifact-members", policy.DownloadArtifactMembers);
            Add("--download-test-attachments", policy.DownloadTestAttachments);
            Add("--download-attachment-members", policy.DownloadAttachmentMembers);
            Add("--artifact-byte-scope", policy.ArtifactByteScope);
            Add("--artifact-byte-mode", policy.ArtifactByteMode);
            Add("--test-scope", policy.TestScope);
            Add("--log-scope", policy.LogScope);
            Add("--helix-scope", policy.HelixScope);
        }
        if (!argv.Contains("--allow-large")) argv.Add("--allow-large");
        if (!argv.Contains("--resume")) argv.Add("--resume");
        Set("--cache-dir", _cacheOptions.GetBaseCacheRoot());
        Set("--manifest", ResolveManifestPath(policy));
        if (policy.ExportPath is not null)
            Set("--export", Path.GetFullPath(policy.ExportPath) + $".resumed-{Guid.NewGuid():N}");
        return new[] { "hlx" }.Concat(argv.Select(Acquisition.AcquisitionRedaction.RedactSensitiveUrls)).ToArray();

        void Add(string flag, string? value) { if (value is not null) { argv.Add(flag); argv.Add(value); } }
        void Set(string flag, string value)
        {
            var assigned = argv.FindIndex(arg => arg.StartsWith(flag + "=", StringComparison.Ordinal));
            if (assigned >= 0) { argv[assigned] = flag + "=" + value; return; }
            var index = argv.IndexOf(flag);
            if (index >= 0 && index + 1 < argv.Count) argv[index + 1] = value;
            else Add(flag, value);
        }
    }

    private object PlanningRecovery(CollectPolicy policy)
    {
        var argv = RecoveryArgv(policy);
        return new
        {
            kind = "planning_deferred",
            argv,
            executionHost = OperatingSystem.IsWindows() ? "powershell" : "posix",
            command = string.Join(" ", argv.Select(QuoteArgument))
        };
    }

    private static string QuoteArgument(string value) => OperatingSystem.IsWindows()
        ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
        : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
