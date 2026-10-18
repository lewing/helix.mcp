using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Catalog = HelixTool.Core.AzDO.AzdoMemberCatalog;
using CatalogEntry = HelixTool.Core.AzDO.AzdoMemberCatalogEntry;

namespace HelixTool.Core.AzDO;

/// <summary>Acquires complete selected bytes independently of report presentation windows.</summary>
public sealed class AzdoDownloadService(
    IAzdoApiClient client, ICacheStore cache, CacheOptions cacheOptions, IEvidenceDeliveryStore deliveryStore)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string Partition => cacheOptions.AuthTokenHash ?? "public";
    private string Key(string org, string project, string suffix) => AzdoCacheKeys.MetadataKey(cacheOptions, org, project, suffix);

    public async Task<AzdoDownloadReport> DownloadArtifactsAsync(string buildIdOrUrl,
        AzdoDownloadOptions options, Action<string>? progress = null, CancellationToken ct = default)
    {
        Validate(options);
        if (options.OutputDir is not null) AzdoDownloadPaths.ValidateOutputOutsideSnapshot(options.OutputDir, cacheOptions);
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buildId);
        var artifacts = await client.GetBuildArtifactsAsync(org, project, buildId, ct);
        var selected = artifacts.Where(a => options.ArtifactName is not null
            ? string.Equals(a.Name, options.ArtifactName, StringComparison.OrdinalIgnoreCase)
            : options.Pattern is null || StringHelpers.MatchesPattern(a.Name ?? "", options.Pattern)).ToArray();
        if (options.ArtifactName is not null && selected.Length == 0)
            throw Missing("artifact", new Dictionary<string, object?> { ["buildId"] = buildId, ["artifactName"] = options.ArtifactName });
        if (options.MemberOffset > 0 && selected.Length != 1)
            throw new ArgumentException("Nonzero memberOffset requires one exact artifact.");
        var arguments = Arguments(options);
        arguments["buildIdOrUrl"] = int.TryParse(buildIdOrUrl, out _) ? buildIdOrUrl
            : $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_build/results?buildId={buildId}";
        var resources = selected.Select(a => new ResourceSpec(a.Id, a.Name ?? $"artifact-{a.Id}",
            $"build:{buildId}:artifact:{a.Id}", $"artifactbytes:v1:{buildId}:{a.Id}:zip",
            $"artifactmembers:v1:{buildId}:{a.Id}:all",
            path => $"artifactmemberbytes:v1:{buildId}:{a.Id}:{AzdoDownloadPaths.Hash(path)}:content",
            path => OpenArtifactWithRefreshAsync(org, project, buildId, a, path, ct),
            new AzdoDownloadResult { BuildId = buildId, ArtifactId = a.Id, Name = a.Name ?? $"artifact-{a.Id}" },
            a.Resource?.Type?.Equals("PipelineArtifact", StringComparison.OrdinalIgnoreCase) == true ? "pipeline_file" : "container_file",
            a.Resource?.Data is string identity ? AzdoDownloadPaths.Hash(identity) : null)).ToArray();
        return await DownloadAsync(org, project, resources, options, "azdo_download_artifacts", arguments,
            options.All || options.ArtifactName is not null || options.Pattern is not null, progress, ct);
    }

    public async Task<AzdoDownloadReport> DownloadTestAttachmentsAsync(int runId, int? resultId,
        string org, string project, AzdoDownloadOptions options, Action<string>? progress = null, CancellationToken ct = default)
    {
        Validate(options);
        if (options.OutputDir is not null) AzdoDownloadPaths.ValidateOutputOutsideSnapshot(options.OutputDir, cacheOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        if (resultId <= 0 || options.AttachmentId <= 0) throw new ArgumentException("Attachment/result IDs must be positive.");
        var attachments = resultId is int id
            ? await client.GetTestAttachmentsAsync(org, project, runId, id, int.MaxValue, ct)
            : await client.GetRunTestAttachmentsAsync(org, project, runId, int.MaxValue, ct);
        var selected = attachments.Where(a => options.AttachmentId is int requested ? a.Id == requested :
            options.Pattern is null || StringHelpers.MatchesPattern(a.FileName ?? "", options.Pattern)).ToArray();
        if (options.AttachmentId is not null && selected.Length == 0)
            throw Missing("test_attachment", new Dictionary<string, object?> { ["runId"] = runId, ["resultId"] = resultId, ["attachmentId"] = options.AttachmentId });
        if (options.MemberOffset > 0 && selected.Length != 1)
            throw new ArgumentException("Nonzero memberOffset requires one exact attachment.");
        var scope = resultId is null ? "run" : "result";
        var identity = $"{runId}:{scope}:{resultId ?? 0}";
        var arguments = Arguments(options);
        arguments["runId"] = runId;
        arguments["resultId"] = resultId;
        arguments["org"] = org;
        arguments["project"] = project;
        var resources = selected.Select(a => new ResourceSpec(a.Id, a.FileName ?? $"attachment-{a.Id}",
            $"run:{identity}:attachment:{a.Id}", $"testattachmentbytes:v1:{identity}:{a.Id}:content",
            $"testattachmentmembers:v1:{identity}:{a.Id}:all",
            path => $"testattachmentmemberbytes:v1:{identity}:{a.Id}:{AzdoDownloadPaths.Hash(path)}:content",
            _ => client.OpenTestAttachmentAsync(org, project, runId, resultId, a.Id, ct),
            new AzdoDownloadResult { RunId = runId, ResultId = resultId, Scope = scope, AttachmentId = a.Id, Name = a.FileName ?? $"attachment-{a.Id}" },
            "attachment_file", null)).ToArray();
        return await DownloadAsync(org, project, resources, options, "azdo_download_test_attachments", arguments,
            options.All || options.AttachmentId is not null || options.Pattern is not null, progress, ct);
    }

    private sealed record ResourceSpec(int Id, string Name, string Identity, string ArchiveSuffix,
        string CatalogSuffix, Func<string, string> MemberSuffix, Func<string?, Task<AzdoByteStream>> Open,
        AzdoDownloadResult Result, string NativeMode, string? ProviderIdentity);

    private async Task<AzdoByteStream> OpenArtifactWithRefreshAsync(string org, string project, int buildId,
        AzdoBuildArtifact artifact, string? member, CancellationToken ct)
    {
        try { return await client.OpenArtifactAsync(org, project, buildId, artifact, member, ct); }
        catch (HlxAcquisitionException ex) when (!cacheOptions.EvalMode && ex.Error.HttpStatus is 401 or 403 &&
            ex.Error.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
        {
            var refreshed = await client.RefreshBuildArtifactsAsync(org, project, buildId, ct);
            var current = refreshed.SingleOrDefault(a => a.Id == artifact.Id &&
                string.Equals(a.Name, artifact.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw Missing("artifact", new Dictionary<string, object?> { ["buildId"] = buildId, ["artifactId"] = artifact.Id });
            if (!string.Equals(current.Resource?.Data, artifact.Resource?.Data, StringComparison.Ordinal))
                throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(AcquisitionErrorKind.InvalidResponse,
                    "azdo", "refresh_artifact_capability", new Dictionary<string, object?> { ["buildId"] = buildId, ["artifactId"] = artifact.Id },
                    "Artifact content identity changed while refreshing its capability. Metadata was refreshed; restart the exact request at offset 0 to capture the new observation."));
            return await client.OpenArtifactAsync(org, project, buildId, current, member, ct);
        }
    }
    private sealed class PlanningDeferredException(string message) : Exception(message);
    private sealed class BytePublicationException(AzdoByteDescriptor descriptor, EvidenceDescriptor evidence, string message)
        : IOException(message)
    {
        public AzdoByteDescriptor Descriptor { get; } = descriptor;
        public EvidenceDescriptor Evidence { get; } = evidence;
    }
    private sealed class Counters(AzdoDownloadOptions options)
    {
        public long Streamed;
        public long Retained;
        private readonly HashSet<string> _retainedKeys = new(StringComparer.Ordinal);
        public void Retain(string key, long bytes)
        {
            if (_retainedKeys.Contains(key)) return;
            if (!options.AllowLarge && options.MaxTotalBytes is long total && bytes > total - Retained)
                throw new PlanningDeferredException("Selected retained-byte threshold reached; resume the same request with allowLarge=true.");
            _retainedKeys.Add(key);
            Retained += bytes;
        }
    }

    private async Task<AzdoDownloadReport> DownloadAsync(string org, string project, ResourceSpec[] resources,
        AzdoDownloadOptions options, string tool, Dictionary<string, object?> arguments, bool acquire,
        Action<string>? progress, CancellationToken ct)
    {
        var view = ScopeFingerprint(org, project, resources, options);
        if ((options.Offset > 0 || options.MemberOffset > 0) && options.ViewId is null)
            throw new ArgumentException("Nonzero offsets require viewId from the preceding report.");
        var results = new List<AzdoDownloadResult>();
        var counters = new Counters(options);
        if (acquire && !cacheOptions.EvalMode)
            await cache.RetainArtifactsAsync(TimeSpan.FromHours(24), ct);
        foreach (var resource in resources)
        {
            ct.ThrowIfCancellationRequested();
            if (!acquire)
            {
                results.Add(resource.Result with { Outcome = "skipped", Skip = "selection_required" });
                continue;
            }
            progress?.Invoke($"Acquiring {resource.Name}; completed files are verified/reused; upstream files restart without Range.");
            try
            {
                results.Add(await DownloadResourceAsync(org, project, resource, options, counters, progress, ct));
            }
            catch (PlanningDeferredException ex)
            {
                progress?.Invoke(ex.Message);
                results.Add(resource.Result with { Outcome = "skipped", Skip = "planning_deferred", ContentKind = options.ContentKind });
            }
            catch (HlxAcquisitionException ex) { results.Add(resource.Result with { Outcome = "failed", Error = ex.Error }); }
            catch (BytePublicationException ex)
            {
                results.Add(resource.Result with
                {
                    Outcome = "failed",
                    Delivery = ex.Evidence,
                    LocalPath = ex.Evidence.LocalPath,
                    Bytes = ex.Evidence.Bytes,
                    Sha256 = ex.Evidence.Sha256,
                    ContentType = ex.Descriptor.ContentType,
                    Source = ex.Descriptor.Source,
                    Error = AcquisitionErrorFactory.Create(AcquisitionErrorKind.TransportError, "cache", "publish_bytes",
                        new Dictionary<string, object?> { ["resource"] = resource.Identity, ["evidenceId"] = ex.Evidence.EvidenceId },
                        ex.Message + " Verified delivery remains readable; snapshot byte coverage is incomplete.")
                });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                results.Add(resource.Result with
                {
                    Outcome = "failed",
                    Error = AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.Timeout, "azdo", "download_bytes",
                    new Dictionary<string, object?> { ["resource"] = resource.Identity },
                    "transfer_interrupted: provider body timed out. Resume the exact selection; verified completed files are retained.")
                });
            }
            catch (HttpRequestException)
            {
                results.Add(resource.Result with
                {
                    Outcome = "failed",
                    Error = AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.TransportError, "azdo", "download_bytes",
                    new Dictionary<string, object?> { ["resource"] = resource.Identity },
                    "transfer_interrupted: provider body failed. Resume the exact selection; verified completed files are retained.")
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or JsonException)
            {
                results.Add(resource.Result with
                {
                    Outcome = "failed",
                    Error = AcquisitionErrorFactory.Create(
                    ex is InvalidDataException or JsonException ? AcquisitionErrorKind.InvalidResponse : AcquisitionErrorKind.TransportError,
                    "azdo", "download_bytes", new Dictionary<string, object?> { ["resource"] = resource.Identity },
                    AcquisitionRedaction.RedactSensitiveUrls(ex.Message))
                });
            }
        }
        view = ObservationFingerprint(view, results);
        if (options.ViewId is not null && options.ViewId != view)
            throw new ArgumentException("viewId no longer matches the selected observation; restart at offset 0.");
        var fullComplete = acquire && results.All(r => r.Outcome is "ok" or "cached");
        var full = new AzdoDownloadReport
        {
            Results = results,
            Returned = results.Count,
            Total = results.Count,
            Limit = results.Count,
            ViewId = view,
            Bytes = counters.Retained,
            StreamedBytes = counters.Streamed,
            Complete = fullComplete,
            Ok = !results.Any(r => r.Outcome == "failed"),
            Planning = new { options.AllowLarge, options.MaxFileBytes, options.MaxArchiveBytes, options.MaxTotalBytes, options.MaxTotalStreamBytes },
            Headroom = new
            {
                availableBytes = FileEvidenceDeliveryStore.ResolveDrive(cacheOptions.GetEffectiveCacheRoot()).AvailableFreeSpace,
                retainedBytes = counters.Retained,
                streamedBytes = counters.Streamed,
                retentionLease = "active cache artifacts protected for 24 hours; ordinary cache target is soft"
            },
            Cache = new { partition = Partition, snapshot = cacheOptions.EvalMode, retainedBytes = counters.Retained },
            Note = acquire ? "Paths are server-local. Binlogs/dumps are download-only; use external analysis tools. Upstream Range is not assumed."
                : "Choose an artifact/attachment ID or pattern; all=true downloads the complete selected set."
        };
        var pageResults = (options.All ? results : results.Skip(options.Offset).Take(options.Limit)).Select(r =>
        {
            var members = options.AllMembers ? r.Members : r.Members.Skip(options.MemberOffset).Take(options.MemberLimit).ToArray();
            var memberComplete = members.Count == r.Members.Count;
            var spec = resources.Single(s => s.Id == (r.ArtifactId ?? r.AttachmentId));
            var memberView = ObservationFingerprint(ScopeFingerprint(org, project, [spec], options), [r]);
            var memberArgs = new Dictionary<string, object?>(arguments)
            {
                ["memberOffset"] = options.MemberOffset + members.Count,
                ["offset"] = 0,
                ["viewId"] = memberView
            };
            if (r.ArtifactId is not null) { memberArgs["artifactName"] = r.Name; memberArgs["pattern"] = null; }
            else { memberArgs["attachmentId"] = r.AttachmentId; memberArgs["pattern"] = null; }
            return r with
            {
                Members = members,
                MembersReturned = members.Count,
                MembersTotal = r.Members.Count,
                MemberOffset = options.MemberOffset,
                MemberLimit = options.MemberLimit,
                MembersComplete = memberComplete,
                MembersTruncated = !memberComplete,
                MemberNext = options.MemberOffset + members.Count < r.Members.Count ? new(tool, memberArgs) : null
            };
        }).ToArray();
        var complete = fullComplete && pageResults.Length == results.Count && pageResults.All(r => r.MembersComplete);
        var nextArgs = new Dictionary<string, object?>(arguments) { ["offset"] = options.Offset + pageResults.Length, ["viewId"] = view };
        var recoveryArgs = new Dictionary<string, object?>(arguments)
        {
            ["all"] = true, ["allMembers"] = true, ["delivery"] = "file", ["allowLarge"] = true,
            ["offset"] = 0, ["memberOffset"] = 0, ["viewId"] = null
        };
        var recovery = BuildRecovery(tool, recoveryArgs, options, results);
        full = full with
        {
            Recovery = fullComplete ? null : recovery,
            IncompleteDetails = fullComplete ? [] : new object[] { new { code = !acquire ? "selection_required" :
                results.Any(r => r.Skip == "planning_deferred") ? "planning_deferred" : "download_incomplete", action = recovery } }
        };
        // Full manifests preserve outcomes and members that are omitted from inline windows.
        var manifest = deliveryStore.PutText(JsonSerializer.Serialize(full, JsonOptions), "application/json",
            view, null, Partition);
        var read = new AzdoDownloadAction("hlx_read_evidence", new Dictionary<string, object?> { ["evidenceId"] = manifest.EvidenceId });
        return full with
        {
            Results = pageResults,
            Returned = pageResults.Length,
            Offset = options.Offset,
            Limit = options.Limit,
            Complete = complete,
            Truncated = !complete,
            Delivery = manifest,
            Continuation = read,
            Next = !options.All && options.Offset + pageResults.Length < results.Count ? new(tool, nextArgs) : null,
            Recovery = fullComplete ? read : recovery,
            IncompleteDetails = full.IncompleteDetails
        };
    }

    private static string ScopeFingerprint(string org, string project, IEnumerable<ResourceSpec> resources, AzdoDownloadOptions options)
        => AzdoDownloadPaths.Hash(JsonSerializer.Serialize(new
        {
            resources = resources.Select(r => new { r.Id, r.Name, r.Identity, r.ProviderIdentity }),
            options.MemberPath,
            options.MemberPattern,
            org,
            project
        }, JsonOptions));

    private static string ObservationFingerprint(string selection, IEnumerable<AzdoDownloadResult> results)
        => AzdoDownloadPaths.Hash(JsonSerializer.Serialize(new
        {
            selection,
            members = results.Select(r => new
            {
                r.Source?.CatalogFingerprint,
                members = r.Members.Select(m => new { m.MemberPath, m.Sha256 })
            })
        }, JsonOptions));

    private object BuildRecovery(string tool, Dictionary<string, object?> arguments, AzdoDownloadOptions options,
        IReadOnlyList<AzdoDownloadResult> results)
    {
        var argv = new List<string> { "hlx", "azdo", tool == "azdo_download_artifacts" ? "download-artifacts" : "download-test-attachments" };
        var identityKey = tool == "azdo_download_artifacts" ? "buildIdOrUrl" : "runId";
        argv.Add(arguments[identityKey]?.ToString() ?? "");
        foreach (var (key, value) in arguments)
        {
            if (key == identityKey || value is null || value is JsonElement { ValueKind: JsonValueKind.Null }) continue;
            var text = value.ToString();
            bool? boolean = value switch
            {
                bool flagValue => flagValue,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                _ => null
            };
            if (text is null || boolean == false) continue;
            var flag = "--" + string.Concat(key.Select(c => char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : c.ToString()));
            argv.Add(flag);
            if (boolean is null) argv.Add(text);
            else if (key == "resume") argv.Add("true");
        }
        if (options.OutputDir is not null) { argv.Add("--output-dir"); argv.Add(Path.GetFullPath(options.OutputDir)); }
        var offline = results.Any(r => r.Error?.Kind == AcquisitionErrorKind.NotInSnapshot);
        var storage = results.Any(r => r.Error?.Message.Contains("insufficient_disk_headroom", StringComparison.Ordinal) == true);
        var planning = results.Any(r => r.Skip == "planning_deferred");
        using var locator = deliveryStore.CreateSpool(Partition);
        var recollectionRoot = Path.GetDirectoryName(locator.Name)!;
        var captureId = Guid.NewGuid().ToString("N");
        var snapshot = Path.Combine(recollectionRoot, $"recollected-{captureId}-snapshot");
        var liveCache = offline ? Path.Combine(recollectionRoot, $"recollected-{captureId}-cache") : cacheOptions.GetBaseCacheRoot();
        argv.Add("--cache-dir");
        argv.Add(liveCache);
        var exportArgv = new[] { "hlx", "snapshot", "export", snapshot, "--cache-dir", liveCache };
        return new
        {
            kind = offline ? "not_in_snapshot" : storage ? "insufficient_disk_headroom" : planning ? "planning_deferred" : "resume",
            tool,
            arguments,
            executionHost = OperatingSystem.IsWindows() ? "powershell" : "posix",
            argv = storage ? null : argv,
            command = storage ? null : string.Join(" ", argv.Select(Quote)),
            requiredStorageAction = storage ? new
            {
                option = "--cache-dir",
                currentPath = liveCache,
                instruction = "Choose a different cache directory on a volume with sufficient available storage, then rerun the same selection with that --cache-dir. If output storage also failed, change --output-dir too."
            } : null,
            sourceMode = offline ? "live; unset HLX_EVAL_SNAPSHOT before acquisition" : "current",
            snapshotDestination = offline ? snapshot : null,
            exportArgv = offline ? exportArgv : null,
            exportCommand = offline ? string.Join(" ", exportArgv.Select(Quote)) : null,
            replay = offline ? $"HLX_EVAL_SNAPSHOT={Quote(snapshot)} hlx mcp" : null,
            note = storage ? "allowLarge does not create disk space. Move the cache/output directory to a volume with sufficient available storage before resuming."
                : offline ? "Run in live mode, then snapshot export to snapshotDestination; the original snapshot stays read-only." : null
        };
        static string Quote(string value) => OperatingSystem.IsWindows() ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private async Task<AzdoDownloadResult> DownloadResourceAsync(string org, string project, ResourceSpec resource,
        AzdoDownloadOptions options, Counters counters, Action<string>? progress, CancellationToken ct)
    {
        var archiveKey = Key(org, project, resource.ArchiveSuffix);
        var exact = options.MemberPath is null ? null : AzdoDownloadPaths.NormalizeMember(options.MemberPath);
        var memberMode = exact is not null || options.MemberPattern is not null;
        if (!memberMode)
        {
            var published = await CachedAsync(archiveKey, options, ct, resource.ProviderIdentity);
            var cached = published is not null;
            if (published is null)
            {
                EnsureLive(archiveKey);
                await using var source = await OpenWithRetryAsync(resource, null, progress, ct);
                published = await PublishAsync(source.Content, source.ContentType, source.Length, archiveKey, resource,
                    null, resource.NativeMode == "attachment_file" ? "binary" : "archive",
                    new AzdoByteSource { TransferMode = resource.NativeMode == "attachment_file" ? "attachment_file" : "zip_archive", ProviderIdentity = resource.ProviderIdentity },
                    options, counters, resource.NativeMode != "attachment_file" ||
                        source.ContentType.Contains("zip", StringComparison.OrdinalIgnoreCase), progress, ct);
            }
            else counters.Retain(archiveKey, published.Value.Descriptor.Bytes);
            var (descriptor, evidence) = published.Value;
            var path = await CopyOutputAsync(evidence, resource, resource.Name, options, ct);
            return resource.Result with
            {
                Outcome = cached ? "cached" : "ok",
                LocalPath = path,
                CacheKey = archiveKey,
                Bytes = descriptor.Bytes,
                Sha256 = descriptor.Sha256,
                ContentType = descriptor.ContentType,
                Format = descriptor.Format,
                ContentKind = options.ContentKind,
                Source = descriptor.Source,
                Delivery = evidence
            };
        }

        var catalogKey = Key(org, project, resource.CatalogSuffix);
        var catalogJson = await cache.GetMetadataAsync(catalogKey, ct);
        Catalog? catalog = null;
        try
        {
            catalog = catalogJson is null ? null : JsonSerializer.Deserialize<Catalog>(catalogJson, JsonOptions);
            if (catalog is not null) ValidateCatalog(catalog);
        }
        catch (Exception ex) when (!cacheOptions.EvalMode && ex is InvalidDataException or JsonException)
        {
            catalog = null;
        }
        if (catalog is not null && resource.ProviderIdentity is not null &&
            catalog.Source.ProviderIdentity != resource.ProviderIdentity)
        {
            if (cacheOptions.EvalMode) throw new InvalidDataException("Catalog provider identity does not match captured artifact metadata.");
            catalog = null;
        }
        var exactEntry = catalog?.Members.SingleOrDefault(e => e.MemberPath == exact);

        if (exact is not null)
        {
            var key = Key(org, project, resource.MemberSuffix(exact));
            var cached = await CachedAsync(key, options, ct, resource.ProviderIdentity);
            if (cached is not null && (catalog is null || exactEntry is not null &&
                await MatchesCatalogAsync(cached.Value, exactEntry, ct)))
            {
                counters.Retain(key, cached.Value.Descriptor.Bytes);
                return MembersResult(resource, options, [await MemberAsync(exact, key, cached.Value, resource, options, true, ct)], cached.Value.Descriptor.Source);
            }
            if (!cacheOptions.EvalMode && resource.NativeMode != "attachment_file")
            {
                try
                {
                    // Exact paths are native files; do not broaden auth/not-found failures into archive downloads.
                    await using var source = await OpenWithRetryAsync(resource, exact, progress, ct);
                    if (source.ContentType.Contains("zip", StringComparison.OrdinalIgnoreCase) && !exact.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Native member endpoint returned a ZIP rather than the selected file.");
                    if (exactEntry is not null && source.Length is long declared && declared != exactEntry.Bytes)
                        throw new InvalidDataException("Native member declared length does not match the captured catalog.");
                    await using var checkedInput = exactEntry is null ? null : new CrcReadStream(source.Content, exactEntry.Crc32);
                    var published = await PublishAsync(checkedInput ?? source.Content, source.ContentType, exactEntry?.Bytes ?? source.Length, key, resource,
                        exact, "member", new AzdoByteSource { TransferMode = resource.NativeMode, ProviderIdentity = resource.ProviderIdentity },
                        options, counters, false, progress, ct);
                    return MembersResult(resource, options, [await MemberAsync(exact, key, published, resource, options, false, ct)], published.Descriptor.Source);
                }
                catch (InvalidDataException ex)
                {
                    var error = AcquisitionErrorFactory.Create(AcquisitionErrorKind.InvalidResponse, "azdo", "download_member",
                        new Dictionary<string, object?> { ["memberPath"] = exact }, ex.Message);
                    return MembersResult(resource, options, [new AzdoDownloadedMember
                    {
                        MemberPath = exact, CacheKey = key, Outcome = "failed", Error = error
                    }], catalog?.Source ?? new AzdoByteSource { TransferMode = resource.NativeMode });
                }
                catch (HlxAcquisitionException ex) when (
                    ex.Error.HttpStatus is 405 or 501 ||
                    ex.Error.HttpStatus == 400 && ex.Error.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
                    ex.Error.Message.Contains("format", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Invoke("Provider explicitly rejected native member selection; retaining the same exact member from its ZIP.");
                }
            }
        }

        if (catalog is not null && (options.Resume || cacheOptions.EvalMode))
        {
            var matched = Select(catalog.Members, exact, options).ToArray();
            var members = new List<AzdoDownloadedMember>();
            foreach (var entry in matched)
            {
                var key = Key(org, project, resource.MemberSuffix(entry.MemberPath));
                var cached = await CachedAsync(key, options, ct, resource.ProviderIdentity);
                if (cached is null || !await MatchesCatalogAsync(cached.Value, entry, ct)) break;
                members.Add(await MemberAsync(entry.MemberPath, key, cached.Value, resource, options, true, ct));
            }
            if (members.Count == matched.Length)
            {
                if (exact is not null && matched.Length == 0) throw Missing("artifact_member", new Dictionary<string, object?> { ["memberPath"] = exact });
                foreach (var member in members) counters.Retain(member.CacheKey, member.Bytes);
                return MembersResult(resource, options, members, catalog.Source);
            }
            if (cacheOptions.EvalMode && await cache.GetMetadataAsync(archiveKey, ct) is null)
            {
                foreach (var entry in matched.Skip(members.Count))
                {
                    var key = Key(org, project, resource.MemberSuffix(entry.MemberPath));
                    var available = await CachedAsync(key, options, ct, resource.ProviderIdentity);
                    if (available is not null && await MatchesCatalogAsync(available.Value, entry, ct))
                    {
                        counters.Retain(key, available.Value.Descriptor.Bytes);
                        members.Add(await MemberAsync(entry.MemberPath, key, available.Value, resource, options, true, ct));
                    }
                    else
                        members.Add(new AzdoDownloadedMember
                        {
                            MemberPath = entry.MemberPath,
                            CacheKey = key,
                            Outcome = "failed",
                            Error = AcquisitionErrorFactory.Create(AcquisitionErrorKind.NotInSnapshot, "cache", "download_member",
                                new Dictionary<string, object?> { ["cacheKey"] = key, ["memberPath"] = entry.MemberPath },
                                "Catalog coverage exists, but this member's bytes were not selected into the snapshot.")
                        });
                }
                foreach (var member in members.Where(m => m.Outcome == "cached")) counters.Retain(member.CacheKey, member.Bytes);
                return MembersResult(resource, options, members, catalog.Source);
            }
        }

        await using var spool = deliveryStore.CreateSpool(Partition);
        var archive = await CachedAsync(archiveKey, options, ct, resource.ProviderIdentity);
        var spoolContentType = archive?.Descriptor.ContentType ?? "application/zip";
        AzdoByteSource zipSource;
        if (archive is not null)
        {
            await using var local = File.OpenRead(archive.Value.Evidence.LocalPath);
            await local.CopyToAsync(spool, 65536, ct);
            zipSource = archive.Value.Descriptor.Source;
        }
        else
        {
            EnsureLive(catalogKey);
            await using var source = await OpenWithRetryAsync(resource, null, progress, ct);
            spoolContentType = source.ContentType;
            await using var captured = await CaptureBodyAsync(source.Content, source.Length, catalogKey,
                resource, null, options, counters, true, progress, ct);
            FileEvidenceDeliveryStore.CheckHeadroom(spool.Name, captured.Length);
            await captured.CopyToAsync(spool, 65536, ct);
            spool.Position = 0;
            var actualZip = IsZipSignature(spool);
            zipSource = new AzdoByteSource
            {
                TransferMode = actualZip ? "zip_selective" : "attachment_file",
                SourceBytes = spool.Length,
                SourceComplete = true,
                ArchiveBytes = actualZip ? spool.Length : null,
                ArchiveSha256 = actualZip ? Convert.ToHexString(await SHA256.HashDataAsync(spool, ct)).ToLowerInvariant() : null,
                ProviderIdentity = resource.ProviderIdentity
            };
        }
        spool.Position = 0;
        try
        {
            using var zip = new ZipArchive(spool, ZipArchiveMode.Read, leaveOpen: true);
            var entries = new SortedDictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException("Symbolic-link archive entries cannot be materialized.");
                var path = entry.FullName;
                if (path.StartsWith(resource.Name + "/", StringComparison.Ordinal)) path = path[(resource.Name.Length + 1)..];
                path = AzdoDownloadPaths.NormalizeMember(path);
                if (!collisions.Add(path)) throw new InvalidDataException("Duplicate or case-colliding archive member paths.");
                entries.Add(path, entry);
            }
            var catalogEntries = entries.Select(e => new CatalogEntry(e.Key, e.Value.Length, e.Value.Crc32)).ToArray();
            var fingerprint = AzdoDownloadPaths.Hash(JsonSerializer.Serialize(catalogEntries, JsonOptions));
            zipSource = zipSource with { TransferMode = "zip_selective", CatalogFingerprint = fingerprint };
            catalog = new Catalog(fingerprint, catalogEntries, zipSource);
            if (!cacheOptions.EvalMode)
                await cache.SetMetadataAsync(catalogKey, JsonSerializer.Serialize(catalog, JsonOptions), TimeSpan.FromHours(4), ct);
            var selected = Select(catalogEntries, exact, options).ToArray();
            if (exact is not null && selected.Length == 0) throw Missing("artifact_member", new Dictionary<string, object?> { ["memberPath"] = exact });
            var output = new List<AzdoDownloadedMember>();
            foreach (var entry in selected)
            {
                var key = Key(org, project, resource.MemberSuffix(entry.MemberPath));
                try
                {
                    var cached = await CachedAsync(key, options, ct, resource.ProviderIdentity);
                    if (cached is not null && await MatchesCatalogAsync(cached.Value, entry, ct))
                    {
                        counters.Retain(key, cached.Value.Descriptor.Bytes);
                        output.Add(await MemberAsync(entry.MemberPath, key, cached.Value, resource, options, true, ct));
                        continue;
                    }
                    await using var input = entries[entry.MemberPath].Open();
                    await using var checkedInput = new CrcReadStream(input, entry.Crc32);
                    var published = await PublishAsync(checkedInput, "application/octet-stream", entry.Bytes, key, resource,
                        entry.MemberPath, "member", zipSource, options, counters, false, progress, ct, network: false);
                    output.Add(await MemberAsync(entry.MemberPath, key, published, resource, options, false, ct));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PlanningDeferredException or HlxAcquisitionException)
                {
                    output.Add(new AzdoDownloadedMember
                    {
                        MemberPath = entry.MemberPath,
                        CacheKey = key,
                        Delivery = (ex as BytePublicationException)?.Evidence,
                        LocalPath = (ex as BytePublicationException)?.Evidence.LocalPath,
                        Bytes = (ex as BytePublicationException)?.Evidence.Bytes ?? 0,
                        Sha256 = (ex as BytePublicationException)?.Evidence.Sha256,
                        Outcome = ex is PlanningDeferredException ? "skipped" : "failed",
                        Skip = ex is PlanningDeferredException ? "planning_deferred" : null,
                        Error = ex is PlanningDeferredException ? null : ex is HlxAcquisitionException acquisition ? acquisition.Error :
                            AcquisitionErrorFactory.Create(ex is InvalidDataException ? AcquisitionErrorKind.InvalidResponse : AcquisitionErrorKind.TransportError,
                                "azdo", "download_member", new Dictionary<string, object?> { ["memberPath"] = entry.MemberPath }, ex.Message)
                    });
                }
            }
            return MembersResult(resource, options, output, zipSource);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Preserve a safe, opaque source archive when its catalog cannot be safely materialized.
            // It is a runtime handoff, not a full-archive cache/snapshot key in member mode.
            spool.Position = 0;
            var evidence = await deliveryStore.PutStreamAsync(spool, spoolContentType, zipSource.ArchiveSha256, null, Partition, ct);
            return resource.Result with
            {
                Outcome = "failed",
                Delivery = evidence,
                LocalPath = evidence.LocalPath,
                Source = zipSource,
                Error = AcquisitionErrorFactory.Create(AcquisitionErrorKind.InvalidResponse, "azdo", "select_archive_members",
                    new Dictionary<string, object?> { ["resource"] = resource.Identity, ["sourceEvidenceId"] = evidence.EvidenceId },
                    "Archive members could not be safely materialized: " + ex.Message + ". The opaque source archive is available through delivery.")
            };
        }
    }

    private static async Task<AzdoByteStream> OpenWithRetryAsync(ResourceSpec resource, string? member,
        Action<string>? progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await resource.Open(member); }
            catch (HlxAcquisitionException ex) when (attempt < 3 &&
                ex.Error.Kind is AcquisitionErrorKind.RateLimited or AcquisitionErrorKind.Timeout or AcquisitionErrorKind.TransportError &&
                ex.Error.RetryAfterSeconds is not > 3600)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(ex.Error.RetryAfterSeconds ?? 0, Math.Pow(2, attempt - 1)));
                progress?.Invoke($"Retrying {resource.Name} after {delay.TotalSeconds:F0}s ({ex.Error.Kind}); completed evidence stays retained.");
                await Task.Delay(delay, ct);
            }
        }
    }

    private static IEnumerable<CatalogEntry> Select(IEnumerable<CatalogEntry> entries, string? exact, AzdoDownloadOptions options)
        => entries.Where(e => exact is not null ? e.MemberPath == exact :
            options.MemberPattern is null || AzdoDownloadPaths.MatchesMember(e.MemberPath, options.MemberPattern));

    internal static void ValidateCatalog(Catalog catalog)
    {
        if (catalog.Source is null || catalog.Members is null || !catalog.Source.SourceComplete ||
            AzdoDownloadPaths.Hash(JsonSerializer.Serialize(catalog.Members, JsonOptions)) != catalog.Fingerprint)
            throw new InvalidDataException("Member catalog fingerprint or source completeness failed verification.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Members)
        {
            if (entry is null) throw new InvalidDataException("Null persisted catalog entry.");
            try { AzdoDownloadPaths.NormalizeMember(entry.MemberPath); }
            catch (ArgumentException ex) { throw new InvalidDataException("Unsafe persisted catalog path.", ex); }
            if (entry.Bytes < 0 || !paths.Add(entry.MemberPath))
                throw new InvalidDataException("Invalid or case-colliding persisted member catalog.");
        }
    }

    private static AzdoDownloadResult MembersResult(ResourceSpec resource, AzdoDownloadOptions options,
        IReadOnlyList<AzdoDownloadedMember> members, AzdoByteSource source)
        => resource.Result with
        {
            Outcome = members.Any(m => m.Outcome == "failed") ? "failed" :
                members.Any(m => m.Outcome == "skipped") ? "skipped" : members.All(m => m.Outcome == "cached") ? "cached" : "ok",
            Format = "members",
            ContentKind = options.ContentKind,
            Source = source,
            Members = members,
            MembersTotal = members.Count,
            MembersReturned = members.Count,
            Bytes = members.Sum(m => m.Bytes),
            Error = members.FirstOrDefault(m => m.Error is not null)?.Error,
            Skip = members.FirstOrDefault(m => m.Skip is not null)?.Skip
        };

    private async Task<bool> MatchesCatalogAsync(
        (AzdoByteDescriptor Descriptor, EvidenceDescriptor Evidence) cached, CatalogEntry entry, CancellationToken ct)
    {
        try
        {
            if (cached.Descriptor.MemberPath != entry.MemberPath || cached.Descriptor.Bytes != entry.Bytes)
                throw new InvalidDataException("Cached member length/path does not match the current catalog.");
            await using var input = File.OpenRead(cached.Evidence.LocalPath);
            await using var checkedInput = new CrcReadStream(input, entry.Crc32);
            await checkedInput.CopyToAsync(Stream.Null, ct);
            return true;
        }
        catch (InvalidDataException) when (!cacheOptions.EvalMode)
        {
            return false;
        }
    }

    private async Task<(AzdoByteDescriptor Descriptor, EvidenceDescriptor Evidence)?> CachedAsync(
        string key, AzdoDownloadOptions options, CancellationToken ct, string? providerIdentity = null)
    {
        if (!options.Resume && !cacheOptions.EvalMode) return null;
        try
        {
            var json = await cache.GetMetadataAsync(key, ct);
            if (json is null) return null;
            var descriptor = JsonSerializer.Deserialize<AzdoByteDescriptor>(json, JsonOptions)
                ?? throw new InvalidDataException("Invalid byte descriptor.");
            AzdoByteDescriptorValidator.Validate(key, descriptor);
            if (providerIdentity is not null && descriptor.Source.ProviderIdentity != providerIdentity)
            {
                if (cacheOptions.EvalMode) throw new InvalidDataException("Byte provider identity does not match captured artifact metadata.");
                return null;
            }
            await using var stream = await cache.GetArtifactAsync(key, ct);
            if (stream is null)
            {
                if (cacheOptions.EvalMode) EnsureLive(key);
                return null;
            }
            var evidence = await deliveryStore.PutStreamAsync(stream, descriptor.ContentType, descriptor.Sha256, key, Partition, ct);
            if (evidence.Bytes != descriptor.Bytes || evidence.Sha256 != descriptor.Sha256)
                throw new InvalidDataException("Cached byte evidence failed SHA-256/length verification.");
            return (descriptor, evidence);
        }
        catch (Exception ex) when (!cacheOptions.EvalMode && ex is InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private async Task<(AzdoByteDescriptor Descriptor, EvidenceDescriptor Evidence)> PublishAsync(
        Stream content, string contentType, long? expectedBytes, string key, ResourceSpec resource, string? member,
        string format, AzdoByteSource source, AzdoDownloadOptions options, Counters counters, bool archive,
        Action<string>? progress, CancellationToken ct, bool network = true)
    {
        if (!options.AllowLarge && expectedBytes is long expected)
        {
            var bound = archive ? options.MaxArchiveBytes : options.MaxFileBytes;
            if (bound is long cap && expected > cap ||
                options.MaxTotalBytes is long total && expected > total - counters.Retained ||
                network && options.MaxTotalStreamBytes is long streamed && expected > streamed - counters.Streamed)
                throw new PlanningDeferredException("Selected planning threshold reached. Resume this exact request with allowLarge=true.");
        }
        await using var captured = network ? await CaptureBodyAsync(content, expectedBytes, key, resource, member, options,
            counters, archive, progress, ct) : null;
        await using var observed = new ObservedStream(captured ?? content, counters, options, archive, progress, network: false);
        var evidence = await deliveryStore.PutStreamAsync(observed, contentType, null, key, Partition, ct);
        if (expectedBytes is long length && evidence.Bytes != length)
            throw new InvalidDataException("Provider/ZIP member body length did not match its declared length.");
        if (format == "archive")
        {
            await using var check = File.OpenRead(evidence.LocalPath);
            using var zip = new ZipArchive(check, ZipArchiveMode.Read);
            _ = zip.Entries.Count;
        }
        if (member is not null && !member.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            evidence.Bytes == 22 && network)
        {
            await using var check = File.OpenRead(evidence.LocalPath);
            if (IsZipSignature(check))
                throw new InvalidDataException("Native member selection returned an empty ZIP, not the requested file.");
        }
        if (network) source = source with { SourceBytes = evidence.Bytes, SourceComplete = true };
        if (archive && source.TransferMode == "zip_archive")
            source = source with { ArchiveBytes = evidence.Bytes, ArchiveSha256 = evidence.Sha256 };
        var descriptor = new AzdoByteDescriptor
        {
            Resource = resource.Identity,
            Name = resource.Name,
            MemberPath = member,
            Format = format,
            ContentType = contentType,
            ContentKind = options.ContentKind,
            Bytes = evidence.Bytes,
            Sha256 = evidence.Sha256,
            Source = source,
            DownloadedAt = DateTimeOffset.UtcNow
        };
        counters.Retain(key, evidence.Bytes);
        if (!cacheOptions.EvalMode && cacheOptions.MaxSizeBytes > 0)
        {
            try
            {
                await cache.RetainArtifactsAsync(TimeSpan.FromHours(24), ct);
                FileEvidenceDeliveryStore.CheckHeadroom(cacheOptions.GetEffectiveCacheRoot(), evidence.Bytes);
                await using var input = File.OpenRead(evidence.LocalPath);
                await cache.SetArtifactAsync(key, input, ct);
                await using var verify = await cache.GetArtifactAsync(key, ct);
                if (verify is null || Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)).ToLowerInvariant() != evidence.Sha256)
                    throw new IOException("Cache publication was declined or failed verification; completed delivery remains available.");
                await cache.SetMetadataAsync(key, JsonSerializer.Serialize(descriptor, JsonOptions), TimeSpan.FromHours(4), ct);
                var readback = await cache.GetMetadataAsync(key, ct);
                if (readback is null || JsonSerializer.Deserialize<AzdoByteDescriptor>(readback, JsonOptions) != descriptor)
                    throw new IOException("Byte descriptor publication failed verification; completed delivery remains available.");
                await cache.DeleteAcquisitionErrorAsync(key, ct);
            }
            catch (IOException ex)
            {
                throw new BytePublicationException(descriptor, evidence, ex.Message);
            }
        }
        return (descriptor, evidence);
    }

    private async Task<AzdoDownloadedMember> MemberAsync(string path, string key,
        (AzdoByteDescriptor Descriptor, EvidenceDescriptor Evidence) published, ResourceSpec resource,
        AzdoDownloadOptions options, bool cached, CancellationToken ct)
        => new()
        {
            MemberPath = path,
            CacheKey = key,
            Bytes = published.Descriptor.Bytes,
            Sha256 = published.Descriptor.Sha256,
            Delivery = published.Evidence,
            Outcome = cached ? "cached" : "ok",
            LocalPath = await CopyOutputAsync(published.Evidence, resource, path, options, ct)
        };

    private static async Task<string> CopyOutputAsync(EvidenceDescriptor evidence, ResourceSpec resource,
        string name, AzdoDownloadOptions options, CancellationToken ct)
    {
        if (options.OutputDir is null) return evidence.LocalPath;
        var path = AzdoDownloadPaths.OutputPath(options.OutputDir,
            resource.Identity.Replace(':', '-'), name, AzdoDownloadPaths.Hash($"{resource.Identity}:{name}:{evidence.Sha256}"));
        if (File.Exists(path))
        {
            await using var existing = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).ToLowerInvariant();
            if (hash == evidence.Sha256) return path;
            if (!options.Overwrite) throw new IOException("Output already exists with different content; pass --overwrite to replace it.");
        }
        var stage = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            FileEvidenceDeliveryStore.CheckHeadroom(stage, evidence.Bytes);
            await using (var input = File.OpenRead(evidence.LocalPath))
            await using (var output = AzdoDownloadPaths.CreatePrivateOutput(stage))
                await input.CopyToAsync(output, 65536, ct);
            File.Move(stage, path, options.Overwrite);
            return path;
        }
        finally { File.Delete(stage); }
    }

    private async Task<FileStream> CaptureBodyAsync(Stream content, long? length, string key, ResourceSpec resource,
            string? member, AzdoDownloadOptions options, Counters counters, bool archive, Action<string>? progress, CancellationToken ct)
    {
        var spool = deliveryStore.CreateTransferSpool(Partition, AzdoDownloadPaths.Hash(key));
        var receiptPath = spool.Name + ".json";
        try
        {
            if (spool.Length != 0)
                progress?.Invoke($"Restarting only the current upstream file ({spool.Length} checkpoint bytes); completed verified files are retained. Range is not supported on the measured PipelineArtifact route.");
            spool.SetLength(0);
            spool.Position = 0;
            await using var observed = new ObservedStream(content, counters, options, archive, progress);
            await CopySpoolAsync(observed, spool, ct);
            if (length is long expected && spool.Length != expected)
                throw new InvalidDataException("Provider body length did not match its declared length.");
            spool.Position = 0;
            // The complete open handle stays usable; unfinished owned prefixes alone survive restart/cancel.
            File.Delete(spool.Name);
            File.Delete(receiptPath);
            return spool;
        }
        catch
        {
            try
            {
                await spool.FlushAsync(CancellationToken.None);
                spool.Position = 0;
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(spool, CancellationToken.None)).ToLowerInvariant();
                var receipt = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    resource = resource.Identity,
                    memberPath = member,
                    cacheKey = key,
                    sourceMode = archive ? "zip" : resource.NativeMode,
                    providerIdentity = resource.ProviderIdentity,
                    verifiedPrefixBytes = spool.Length,
                    prefixSha256 = sha,
                    localOwnedPath = spool.Name,
                    complete = false,
                    resumeMode = "restart-file-without-Range",
                    capturedAt = DateTimeOffset.UtcNow
                }, JsonOptions);
                var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var receiptFile = new FileStream(receiptPath, fileOptions);
                await receiptFile.WriteAsync(System.Text.Encoding.UTF8.GetBytes(receipt), CancellationToken.None);
            }
            finally { await spool.DisposeAsync(); }
            throw;
        }
    }

    private static async Task CopySpoolAsync(Stream input, FileStream output, CancellationToken ct)
    {
        var buffer = new byte[65536];
        var disk = FileEvidenceDeliveryStore.ResolveDrive(output.Name);
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            var available = disk.AvailableFreeSpace;
            if (available < count + 1024L * 1024)
                throw new IOException($"insufficient_disk_headroom: required={count + 1024L * 1024}, available={available}; use --cache-dir on sufficient storage.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        await output.FlushAsync(ct);
    }

    private static bool IsZipSignature(Stream stream)
    {
        var saved = stream.Position;
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[4];
        var count = stream.Read(signature);
        stream.Position = saved;
        return count == 4 && signature[0] == 'P' && signature[1] == 'K' &&
            (signature[2] == 3 && signature[3] == 4 || signature[2] == 5 && signature[3] == 6 ||
             signature[2] == 6 && signature[3] == 6 || signature[2] == 7 && signature[3] == 8);
    }

    private void EnsureLive(string key)
    {
        if (cacheOptions.EvalMode)
            throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(AcquisitionErrorKind.NotInSnapshot,
                "cache", "download_bytes", new Dictionary<string, object?> { ["cacheKey"] = key },
                "Selected bytes/catalog are not in this snapshot. Run the original download/collector selector in live mode with HLX_EVAL_SNAPSHOT unset, export to a new snapshot, then replay that snapshot."));
    }

    private static HlxAcquisitionException Missing(string operation, IReadOnlyDictionary<string, object?> resource)
        => new(AcquisitionErrorFactory.Create(AcquisitionErrorKind.NotFound, "azdo", operation, resource, $"Explicitly requested {operation} was not found."));

    public static Dictionary<string, object?> Arguments(AzdoDownloadOptions options)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(options, JsonOptions), JsonOptions)!;
        args.Remove("outputDir");
        args.Remove("overwrite");
        if (options.ArtifactName is null) args.Remove("artifactName");
        if (options.AttachmentId is null) args.Remove("attachmentId");
        return args;
    }

    public static void Validate(AzdoDownloadOptions o)
    {
        if (o.ArtifactName is not null && o.Pattern is not null || o.AttachmentId is not null && o.Pattern is not null)
            throw new ArgumentException("Exact resource and pattern selectors are mutually exclusive.");
        if (o.MemberPath is not null && o.MemberPattern is not null)
            throw new ArgumentException("memberPath and memberPattern are mutually exclusive.");
        if (o.MemberPath is not null) AzdoDownloadPaths.NormalizeMember(o.MemberPath);
        if (o.MemberPattern is not null) AzdoDownloadPaths.NormalizeMember(o.MemberPattern);
        if (o.Offset < 0 || o.MemberOffset < 0 || o.Limit <= 0 || o.MemberLimit <= 0 || o.MaxResponseBytes <= 0)
            throw new ArgumentException("Offsets must be nonnegative; limits and response targets must be positive.");
        if (o.MaxFileBytes <= 0 || o.MaxArchiveBytes <= 0 || o.MaxTotalBytes < 0 || o.MaxTotalStreamBytes < 0)
            throw new ArgumentException("File/archive thresholds must be positive; total byte thresholds must be nonnegative.");
        if (o.Delivery is not ("auto" or "inline" or "file" or "chunked")) throw new ArgumentException("Invalid delivery mode.");
        if (o.ContentKind is not ("auto" or "logs" or "binlog" or "dump" or "other")) throw new ArgumentException("Invalid contentKind label.");
    }

    private class ObservedStream(Stream inner, Counters counters, AzdoDownloadOptions options,
        bool archive, Action<string>? progress, bool network = true) : Stream
    {
        private long _read;
        private long _reported;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            _read += count;
            if (network) counters.Streamed += count;
            if (!options.AllowLarge)
            {
                var cap = archive ? options.MaxArchiveBytes : options.MaxFileBytes;
                if (cap is long c && _read > c ||
                    (!archive || !network) && options.MaxTotalBytes is long total && _read > total - counters.Retained ||
                    network && options.MaxTotalStreamBytes is long stream && counters.Streamed > stream)
                    throw new PlanningDeferredException("Selected planning threshold reached; resume the same request with allowLarge=true.");
            }
            if (_read - _reported >= 16L * 1024 * 1024)
            {
                _reported = _read;
                progress?.Invoke($"Transferred {_read} bytes for current file; invocation source bytes={counters.Streamed}.");
            }
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CrcReadStream(Stream inner, uint expected) : Stream
    {
        private uint _crc = uint.MaxValue;
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
        {
            var value = (uint)i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            return value;
        }).ToArray();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            foreach (var b in buffer.Span[..count]) _crc = Table[(_crc ^ b) & 0xff] ^ (_crc >> 8);
            if (count == 0 && ~_crc != expected) throw new InvalidDataException("Selected ZIP member failed CRC verification.");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
