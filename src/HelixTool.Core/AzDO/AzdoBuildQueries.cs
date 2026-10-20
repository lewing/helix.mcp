using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HelixTool.Core.Text;
using HelixTool.Core.Cache;
using HelixTool.Core.Acquisition;
using HelixTool.Core.Paging;

namespace HelixTool.Core.AzDO;

public interface IAzdoBuildPageSource
{
    Task<AzdoProviderPage<AzdoBuild>> GetBuildsPageAsync(string org, string project, AzdoBuildFilter filter,
        string? continuationToken = null, CancellationToken ct = default);
}

public sealed record AzdoProviderPage<T>(IReadOnlyList<T> Results, string? ContinuationToken);

public sealed record AzdoBuildQueryRequest
{
    public AzdoBuildFilter Filter { get; init; } = new();
    public int Offset { get; init; }
    public int? Limit { get; init; }
    public string Projection { get; init; } = "full";
    public string? ViewId { get; init; }
    public bool All { get; init; }
    public bool Frozen { get; init; }
    public int? MaxDiscoveryBuilds { get; init; }
    public int? MaxScanBuilds { get; init; }
    public string? ResumeCacheKey { get; init; }
    public int? MaxDefinitions { get; init; }
    public IReadOnlyList<AzdoBuildDefinition>? FrozenDefinitions { get; init; }
    public Func<AzdoBuildQuerySource, string, CancellationToken, Task>? Checkpoint { get; init; }
}

public sealed record AzdoBuildQueryResult
{
    [JsonIgnore]
    public IReadOnlyList<AzdoBuild> Results { get; init; } = [];
    [JsonPropertyName("results")]
    public IEnumerable<object> Rows => Projection == "compact" ? Results.Select(b => AzdoBuildQueries.Compact(b, Org, Project)) : Results.Cast<object>();
    [JsonIgnore]
    public AzdoBuild this[int index] => Results[index];
    public bool Ok => true;
    public int Returned => Count;
    public int? Limit { get; init; }
    public HlxCacheProvenance Cache => new() { Key = CacheKey, CompleteKey = SourceComplete ? CacheKey : null };
    public string? Note { get; init; }
    public object? Next { get; init; }
    public Delivery.EvidenceDescriptor? Delivery { get; init; }
    public object? Recovery { get; init; }
    public long? RequestedMaxResponseBytes { get; init; }
    public long? EffectiveMaxResponseBytes { get; init; }
    public int Count => Results.Count;
    public string Org { get; init; } = "";
    public string Project { get; init; } = "";
    public AzdoBuildFilter Query { get; init; } = new();
    public AzdoBuildFilter EffectiveQuery { get; init; } = new();
    public IReadOnlyList<AzdoBuildDefinition> ResolvedDefinitions { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; }
    public bool SourceComplete { get; init; }
    public bool Complete { get; init; }
    public bool Truncated => !Complete;
    public int? Total { get; init; }
    public int Scanned { get; init; }
    public int Pages { get; init; }
    public string ViewId { get; init; } = "";
    public string QueryHash { get; init; } = "";
    public string? CacheKey { get; init; }
    public string Projection { get; init; } = "full";
    public int Offset { get; init; }
    public int? NextOffset { get; init; }
    public IReadOnlyList<string> IncompleteDetails { get; init; } = [];

    public HlxListEnvelope<object> ToListEnvelope()
    {
        var page = new HlxPageInfo(Count, Total, Offset, Limit, Complete, Truncated,
            NextOffset is { } next ? new HlxNextPage(next, Limit ?? 20) : null);
        return new HlxListEnvelope<object>
        {
            Results = new BuildRows(this), Returned = page.Returned, Total = page.Total, Offset = page.Offset,
            Limit = page.Limit, Complete = page.Complete, Truncated = page.Truncated, Next = page.Next, Cache = Cache, Note = Note
        };
    }

    private sealed class BuildRows(AzdoBuildQueryResult data) : IReadOnlyList<object>
    {
        public int Count => data.Count;
        public object this[int index] => data.Projection == "compact" ? AzdoBuildQueries.Compact(data[index], data.Org, data.Project) : data[index];
        public IEnumerator<object> GetEnumerator() => data.Rows.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public sealed record AzdoDefinitionQuerySource(IReadOnlyList<AzdoBuildDefinition> Definitions, DateTimeOffset CapturedAt, bool SourceComplete);

public sealed record AzdoBuildQuerySource
{
    public AzdoBuildFilter Query { get; init; } = new();
    public AzdoBuildFilter EffectiveQuery { get; init; } = new();
    public IReadOnlyList<AzdoBuildDefinition> ResolvedDefinitions { get; init; } = [];
    public IReadOnlyList<string> PageKeys { get; init; } = [];
    public IReadOnlyDictionary<string, string> PageHashes { get; init; } = new Dictionary<string, string>();
    public bool SourceComplete { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public int Scanned { get; init; }
    public string QueryHash { get; init; } = "";
    public string ViewId { get; init; } = "";
    public string? ContinuationToken { get; init; }
    public IReadOnlyList<string> SeenTokens { get; init; } = [];
    public string StatusApplied { get; init; } = "provider";
    public int SelectedCount { get; init; }
}

/// <summary>Shared semantic normalization, hashing and conservative coverage proof.</summary>
public static class AzdoBuildQueries
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static AzdoBuildFilter Normalize(AzdoBuildFilter filter)
    {
        var f = AzdoBuildFilterNormalizer.Normalize(filter);
        if (f.PrNumber is not null && f.Branch is not null)
            throw new ArgumentException("branch and prNumber are mutually exclusive.");
        if (f.DefinitionId is not null && f.DefinitionName is not null)
            throw new ArgumentException("definitionId and definitionName are mutually exclusive.");
        if (f.PrNumber is not null && (!int.TryParse(f.PrNumber, out var pr) || pr <= 0) || f.DefinitionId is <= 0)
            throw new ArgumentException("PR and definition IDs must be positive integers.");
        if (f.MinTime > f.MaxTime) throw new ArgumentException("minTime must not exceed maxTime.");
        if (f.Tags?.Any(string.IsNullOrWhiteSpace) == true) throw new ArgumentException("Tags must be nonempty strings.");
        if (!AzdoService.IsValidQueryOrder(f.QueryOrder)) throw new ArgumentException("Invalid queryOrder.");
        var status = EnumValue(f.StatusFilter, "all,cancelling,completed,inProgress,none,notStarted,postponed");
        var result = EnumValue(f.Result, "none,succeeded,partiallySucceeded,failed,canceled");
        var reason = EnumValue(f.Reason, "none,manual,individualCI,batchedCI,schedule,scheduleForced,userCreated,validateShelveset,checkInShelveset,pullRequest,buildCompletion,resourceTrigger,triggered,all");
        if (f.QueryOrder?.StartsWith("finishtime", StringComparison.OrdinalIgnoreCase) == true && status is not null && status is not "completed" and not "all")
            throw new ArgumentException("finishTime order requires a terminal build status.");
        return f with
        {
            Top = null, StatusFilter = status == "all" ? null : status, Result = result, Reason = reason == "all" ? null : reason,
            PrNumber = f.PrNumber is null ? null : int.Parse(f.PrNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            Branch = f.Branch is not null && !f.Branch.StartsWith("refs/", StringComparison.Ordinal) ? "refs/heads/" + f.Branch : f.Branch,
            DefinitionIds = f.DefinitionIds?.Distinct().Order().ToArray(),
            Tags = f.Tags is { Count: > 0 } ? f.Tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : null,
            MinTime = f.MinTime?.ToUniversalTime(), MaxTime = f.MaxTime?.ToUniversalTime()
        };
    }

    private static string? EnumValue(string? value, string allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = allowed.Split(',').FirstOrDefault(v => v.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return normalized ?? throw new ArgumentException($"Unsupported filter '{value}'; expected {allowed}.");
    }

    public static string Hash(AzdoBuildFilter filter)
    {
        var f = Normalize(filter);
        // A name resolved to one ID and an explicit numeric ID have the same semantic identity.
        var ids = f.DefinitionIds ?? (f.DefinitionId is { } id ? [id] : null);
        return Digest(JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["branch"] = f.Branch, ["buildNumber"] = f.BuildNumber, ["definitionIds"] = ids,
            ["maxTime"] = f.MaxTime, ["minTime"] = f.MinTime, ["prNumber"] = f.PrNumber,
            ["queryOrder"] = (f.QueryOrder ?? AzdoBuildFilterDefaults.QueryOrder).ToLowerInvariant(),
            ["reason"] = f.Reason, ["repositoryId"] = f.RepositoryId, ["repositoryType"] = f.RepositoryType,
            ["result"] = f.Result, ["status"] = f.StatusFilter, ["tags"] = f.Tags
        }, JsonOptions));
    }

    public static string DefinitionHash(AzdoBuildFilter f) => Digest(JsonSerializer.Serialize(new SortedDictionary<string, object?>
    {
        ["name"] = f.DefinitionName, ["repositoryId"] = f.RepositoryId, ["repositoryType"] = f.RepositoryType
    }, JsonOptions));
    public static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || "-_./:".Contains(c))) return value;
        return OperatingSystem.IsWindows() ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    public static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static bool Glob(string? value, string pattern) => value is not null &&
        (HlxNameGlob.HasWildcard(pattern) ? HlxNameGlob.IsMatch(value, pattern, caseSensitive: true) : value.Equals(pattern, StringComparison.Ordinal));

    public static bool Matches(AzdoBuild b, AzdoBuildFilter f)
    {
        var ids = f.DefinitionIds ?? (f.DefinitionId is { } id ? [id] : null);
        var branch = f.PrNumber is not null ? $"refs/pull/{f.PrNumber}/merge" : f.Branch;
        var time = (f.QueryOrder ?? "queueTimeDescending").ToLowerInvariant() switch
        {
            var order when order.StartsWith("finishtime", StringComparison.Ordinal) => b.FinishTime,
            var order when order.StartsWith("starttime", StringComparison.Ordinal) => b.StartTime,
            _ => b.QueueTime
        };
        return (ids is null || b.Definition is not null && ids.Contains(b.Definition.Id)) &&
            (branch is null || b.SourceBranch == branch) &&
            (f.PrNumber is null || b.TriggerInfo?.PrNumber is null || b.TriggerInfo.PrNumber == f.PrNumber) &&
            (f.RepositoryId is null || b.Repository?.Id == f.RepositoryId) &&
            (f.RepositoryType is null || b.Repository?.Type == f.RepositoryType) &&
            (f.StatusFilter is null || string.Equals(b.Status, f.StatusFilter, StringComparison.OrdinalIgnoreCase)) &&
            (f.Result is null || string.Equals(b.Result, f.Result, StringComparison.OrdinalIgnoreCase)) &&
            (f.Reason is null || f.Reason == "all" || string.Equals(b.Reason, f.Reason, StringComparison.OrdinalIgnoreCase)) &&
            (f.BuildNumber is null || Glob(b.BuildNumber, f.BuildNumber)) &&
            (f.Tags is null || f.Tags.All(t => b.Tags?.Contains(t, StringComparer.Ordinal) == true)) &&
            (f.MinTime is null || time >= f.MinTime) && (f.MaxTime is null || time <= f.MaxTime);
    }

    public static bool Covers(AzdoBuildFilter source, AzdoBuildFilter requested)
    {
        var s = Normalize(source); var r = Normalize(requested);
        var sameTimeField = (s.QueryOrder ?? "queueTimeDescending").Replace("Ascending", "", StringComparison.OrdinalIgnoreCase).Replace("Descending", "", StringComparison.OrdinalIgnoreCase)
            .Equals((r.QueryOrder ?? "queueTimeDescending").Replace("Ascending", "", StringComparison.OrdinalIgnoreCase).Replace("Descending", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        static bool Narrow(string? a, string? b) => a is null || a == b;
        var si = s.DefinitionIds ?? (s.DefinitionId is { } sid ? [sid] : null);
        var ri = r.DefinitionIds ?? (r.DefinitionId is { } rid ? [rid] : null);
        return sameTimeField && Narrow(s.Branch, r.Branch) && Narrow(s.PrNumber, r.PrNumber) &&
            Narrow(s.RepositoryId, r.RepositoryId) && Narrow(s.RepositoryType, r.RepositoryType) &&
            Narrow(s.Result, r.Result) && Narrow(s.Reason, r.Reason) && Narrow(s.StatusFilter, r.StatusFilter) &&
            Narrow(s.BuildNumber, r.BuildNumber) && (si is null || ri is not null && ri.All(si.Contains)) &&
            (s.Tags is null || r.Tags is not null && s.Tags.All(r.Tags.Contains)) &&
            (s.MinTime is null || r.MinTime >= s.MinTime) && (s.MaxTime is null || r.MaxTime <= s.MaxTime);
    }

    public static object Compact(AzdoBuild b, string org, string project) => new
    {
        b.Id, b.BuildNumber, b.Status, b.Result, b.Reason,
        definition = b.Definition is null ? null : new { b.Definition.Id, b.Definition.Name }, b.SourceBranch,
        b.QueueTime, b.StartTime, b.FinishTime,
        webUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_build/results?buildId={b.Id}", b.Repository
    };
}

public sealed class AzdoBuildQueryExecutor(IAzdoApiClient client, ICacheStore? cache = null, CacheOptions? options = null)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(4);
    private readonly CacheOptions _options = options ?? new();
    private string Key(string org, string project, string suffix) => AzdoCacheKeys.MetadataKey(_options, org, project, suffix);

    public async Task<AzdoBuildQueryResult> ExecuteAsync(string org, string project, AzdoBuildQueryRequest request,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        if (request.Offset < 0 || request.Limit is <= 0 || request.Filter.Top is <= 0) throw new ArgumentException("offset must be nonnegative; counts must be positive.");
        if (request.Limit.HasValue && request.Filter.Top is { } top && top != 20 && top != request.Limit) throw new ArgumentException("Conflicting top and limit.");
        if (request.Projection is not "full" and not "compact") throw new ArgumentException("projection must be full or compact.");
        var query = AzdoBuildQueries.Normalize(request.Filter);
        IReadOnlyList<AzdoBuildDefinition> definitions = [];
        if (query.DefinitionName is not null)
        {
            var dk = Key(org, project, $"definitions:v1:{AzdoBuildQueries.DefinitionHash(query)}:all");
            var saved = cache is null ? null : await cache.GetMetadataAsync(dk, ct);
            AzdoDefinitionQuerySource? mapping = saved?.TrimStart().StartsWith('{') == true
                ? JsonSerializer.Deserialize<AzdoDefinitionQuerySource>(saved, AzdoBuildQueries.JsonOptions) : null;
            if (saved?.TrimStart().StartsWith('[') == true && (_options.EvalMode || request.Frozen))
                mapping = new(JsonSerializer.Deserialize<AzdoBuildDefinition[]>(saved, AzdoBuildQueries.JsonOptions)!, DateTimeOffset.MinValue, true);
            if (request.FrozenDefinitions is not null) definitions = request.FrozenDefinitions;
            else if (mapping is { SourceComplete: true } && (_options.EvalMode || request.Frozen || DateTimeOffset.UtcNow - mapping.CapturedAt <= TimeSpan.FromSeconds(30)))
                definitions = mapping.Definitions;
            else
            {
                if (_options.EvalMode)
                {
                    var failure = cache is null ? null : await cache.GetAcquisitionErrorAsync(dk, ct);
                    if (failure is not null) throw new HlxAcquisitionException(failure with { Replayed = true, Source = "snapshot" });
                    throw MissingSnapshot(org, project, query);
                }
                progress?.Invoke("Resolving pipeline definitions...");
                try
                {
                    definitions = (await client.ListDefinitionsAsync(org, project, query.DefinitionName, ct))
                        .Where(d => AzdoBuildQueries.Glob(d.Name, query.DefinitionName)).OrderBy(d => d.Id).ToArray();
                }
                catch (HlxAcquisitionException ex)
                {
                    if (cache is not null && AcquisitionFailureRecorderPolicy.IsRecordable(ex.Error)) await cache.SetAcquisitionErrorAsync(dk, ex.Error, Retention, ct);
                    throw;
                }
                if (cache is not null) await cache.SetMetadataAsync(dk, JsonSerializer.Serialize(new AzdoDefinitionQuerySource(definitions, DateTimeOffset.UtcNow, true), AzdoBuildQueries.JsonOptions), Retention, ct);
            }
            query = query with { DefinitionIds = definitions.Select(d => d.Id).Distinct().Order().ToArray() };
        }
        var hash = AzdoBuildQueries.Hash(query);
        var completeKey = Key(org, project, $"builds:v2:{hash}:all");
        var key = completeKey;
        var json = cache is null ? null : await cache.GetMetadataAsync(key, ct);
        var source = json is null ? null : JsonSerializer.Deserialize<AzdoBuildQuerySource>(json, AzdoBuildQueries.JsonOptions);
        if (source is not null && !_options.EvalMode && !request.Frozen && DateTimeOffset.UtcNow - source.CapturedAt > TimeSpan.FromSeconds(30)) source = null;
        if (source is null && _options.EvalMode && cache is not null)
        {
            source = await FindCoveredSourceAsync(org, project, query, ct);
            if (source is not null) key = Key(org, project, $"builds:v2:{source.QueryHash}:all");
        }
        if (source is null && !_options.EvalMode && cache is not null && request.Checkpoint is null)
        {
            var browsingKey = Key(org, project, $"builds:v2:{hash}:window:0:{int.MaxValue}");
            var browsingJson = await cache.GetMetadataAsync(browsingKey, ct);
            var browsing = browsingJson is null ? null : JsonSerializer.Deserialize<AzdoBuildQuerySource>(browsingJson, AzdoBuildQueries.JsonOptions);
            if (browsing is not null && DateTimeOffset.UtcNow - browsing.CapturedAt <= TimeSpan.FromSeconds(30)) { source = browsing; key = browsingKey; }
        }
        if (source is null && _options.EvalMode && cache is not null && !request.All)
        {
            var windowKey = Key(org, project, $"builds:v2:{hash}:window:{request.Offset}:{request.Limit ?? request.Filter.Top ?? 20}");
            var savedWindow = await cache.GetMetadataAsync(windowKey, ct);
            if (savedWindow is null)
            {
                var browsingKey = Key(org, project, $"builds:v2:{hash}:window:0:{int.MaxValue}");
                savedWindow = await cache.GetMetadataAsync(browsingKey, ct);
                if (savedWindow is not null) windowKey = browsingKey;
            }
            if (savedWindow is null)
            {
                var manifestPath = Path.Combine(_options.GetEffectiveCacheRoot(), "manifest", "hlx-collect-manifest.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = JsonSerializer.Deserialize<Collect.CollectManifest>(await File.ReadAllTextAsync(manifestPath, ct), AzdoBuildQueries.JsonOptions);
                    if (manifest?.Kind == "hlx.collect.azdo-builds" && manifest.Source.Org == org && manifest.Source.Project == project &&
                        manifest.Source.QueryHash == hash && manifest.Source.Discovery is { SourceComplete: false } discovery &&
                        discovery.CacheKey.StartsWith(Key(org, project, $"builds:v2:{hash}:window:"), StringComparison.Ordinal))
                    { windowKey = discovery.CacheKey; savedWindow = await cache.GetMetadataAsync(windowKey, ct); }
                }
            }
            if (savedWindow is not null) { source = JsonSerializer.Deserialize<AzdoBuildQuerySource>(savedWindow, AzdoBuildQueries.JsonOptions); key = windowKey; }
        }
        if (source is null && _options.EvalMode && cache is not null)
        {
            var recorded = await cache.GetAcquisitionErrorAsync(key, ct);
            if (recorded is not null) throw new HlxAcquisitionException(recorded with { Replayed = true, Source = "snapshot" });
            if (query.DefinitionName is null && query.BuildNumber is null && query.Tags is null && query.Result is null &&
                query.Reason is null && query.RepositoryId is null && query.RepositoryType is null && query.DefinitionIds is null)
            {
                var legacyKey = Key(org, project, "builds:" + AzdoCacheKeys.HashFilter(request.Filter));
                var legacy = await cache.GetMetadataAsync(legacyKey, ct);
                if (legacy is not null)
                {
                    var available = JsonSerializer.Deserialize<AzdoBuild[]>(legacy, AzdoBuildQueries.JsonOptions)!;
                    var wanted = request.Limit ?? request.Filter.Top ?? 20;
                    if (!request.All && request.Offset < available.Length && (long)request.Offset + wanted <= available.Length || available.Length == 0 && request.Offset == 0 && !request.All)
                    {
                        var selectedLegacy = available.Skip(request.Offset).Take(wanted).ToArray();
                        return new AzdoBuildQueryResult { Org = org, Project = project, Query = query, EffectiveQuery = query,
                            Results = selectedLegacy, Offset = request.Offset, Limit = wanted, Projection = request.Projection,
                            SourceComplete = false, Complete = false, CacheKey = legacyKey, QueryHash = hash,
                            ViewId = AzdoBuildQueries.Digest(legacy), Note = "Legacy bounded evidence has unknown source coverage; recollect for complete access." };
                    }
                }
                recorded = await cache.GetAcquisitionErrorAsync(legacyKey, ct);
                if (recorded is not null) throw new HlxAcquisitionException(recorded with { Replayed = true, Source = "snapshot" });
            }
            throw MissingSnapshot(org, project, query);
        }
        if (source is null && request.ResumeCacheKey is { } resumeKey && cache is not null)
        {
            var prefix = Key(org, project, $"builds:v2:{hash}:");
            if (!resumeKey.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Discovery checkpoint does not match the query or partition.");
            var checkpoint = await cache.GetMetadataIgnoringTtlAsync(resumeKey, ct);
            if (checkpoint is not null) source = JsonSerializer.Deserialize<AzdoBuildQuerySource>(checkpoint, AzdoBuildQueries.JsonOptions);
        }
        List<AzdoBuild> rows = [];
        var locations = new Dictionary<int, (string Key, int Index)>();
        AzdoBuild IndexRow(AzdoBuild build) => cache is null ? build : build with
        {
            Properties = null, RequestedFor = null, Url = null, SourceVersion = null,
            TriggerInfo = build.TriggerInfo is null ? null : new AzdoTriggerInfo { PrNumber = build.TriggerInfo.PrNumber },
            Definition = build.Definition is null ? null : new AzdoBuildDefinition { Id = build.Definition.Id },
            Repository = build.Repository is null ? null : new AzdoBuildRepository { Id = build.Repository.Id, Type = build.Repository.Type }
        };
        if (source is not null)
        {
            if (source.QueryHash != AzdoBuildQueries.Hash(source.Query)) throw new InvalidDataException("Invalid build-query coverage descriptor.");
            foreach (var pk in source.PageKeys)
            {
                if (!pk.StartsWith(Key(org, project, $"builds:v2:{source.QueryHash}:page:"), StringComparison.Ordinal)) throw new InvalidDataException("Build-query page outside source partition.");
                var page = await cache!.GetMetadataIgnoringTtlAsync(pk, ct) ?? throw new InvalidDataException("Build-query backing page is missing.");
                if (!source.PageHashes.TryGetValue(pk, out var expectedHash) || AzdoBuildQueries.Digest(page) != expectedHash)
                    throw new InvalidDataException("Build-query backing page failed hash verification.");
                var fullPage = JsonSerializer.Deserialize<AzdoBuild[]>(page, AzdoBuildQueries.JsonOptions)!;
                for (var i = 0; i < fullPage.Length; i++) locations[fullPage[i].Id] = (pk, i);
                rows.AddRange(fullPage.Select(IndexRow));
            }
        }
        if (source is null || !source.SourceComplete && !_options.EvalMode)
        {
            progress?.Invoke("Discovering build membership (bounded provider pages; no interactive collection quota)...");
            var effective = source?.EffectiveQuery ?? query with { MaxTime = query.MaxTime ?? DateTimeOffset.UtcNow, Top = null };
            var pages = source?.PageKeys.ToList() ?? [];
            var pageHashes = source?.PageHashes.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, string>();
            var scanned = source?.Scanned ?? 0;
            var capturedAt = source?.CapturedAt ?? DateTimeOffset.UtcNow;
            var token = source?.ContinuationToken;
            var seen = source?.SeenTokens.ToHashSet(StringComparer.Ordinal) ?? [];
            var ids = rows.Select(b => b.Id).ToHashSet();
            var exhausted = query.DefinitionIds is { Count: 0 };
            var statusApplied = source?.StatusApplied ?? "provider";
            var definitionDeferred = request.MaxDefinitions is { } definitionMax && definitions.Count > definitionMax;
            if (definitionDeferred)
            {
                source = new AzdoBuildQuerySource { Query = query, EffectiveQuery = effective, ResolvedDefinitions = definitions,
                    SourceComplete = false, QueryHash = hash, CapturedAt = DateTimeOffset.UtcNow, ViewId = AzdoBuildQueries.Digest(hash + ":definitions") };
                key = Key(org, project, $"builds:v2:{hash}:window:0:{request.MaxDiscoveryBuilds ?? int.MaxValue}");
                if (cache is not null) await cache.SetMetadataAsync(key, JsonSerializer.Serialize(source, AzdoBuildQueries.JsonOptions), Retention, ct);
                if (request.Checkpoint is not null) await request.Checkpoint(source, key, ct);
            }
            var discoveryTarget = request.MaxDiscoveryBuilds ?? (!request.All ? (long)request.Offset + (request.Limit ?? request.Filter.Top ?? 20) : (long?)null);
            while (!exhausted && !definitionDeferred && (discoveryTarget is null || rows.Count < discoveryTarget))
            {
                ct.ThrowIfCancellationRequested();
                AzdoProviderPage<AzdoBuild> page;
                try
                {
                    // Non-HTTP clients retain the mockable legacy list seam.
                    page = client is IAzdoBuildPageSource api
                        ? await api.GetBuildsPageAsync(org, project, effective, token, ct)
                        : new AzdoProviderPage<AzdoBuild>(await client.ListBuildsAsync(org, project, effective, ct), null);
                }
                catch (HlxAcquisitionException ex) when (effective.StatusFilter is not null && IsStatusOrderCompatibility(ex))
                {
                    effective = effective with { StatusFilter = null }; statusApplied = "client";
                    progress?.Invoke("Provider rejected status/order pairing; retaining requested order/time and applying status locally.");
                    continue;
                }
                catch (HlxAcquisitionException ex)
                {
                    if (cache is not null && AcquisitionFailureRecorderPolicy.IsRecordable(ex.Error))
                        await cache.SetAcquisitionErrorAsync(key, ex.Error, Retention, ct);
                    throw;
                }
                scanned += page.Results.Count;
                var pageRows = page.Results.Where(b => AzdoBuildQueries.Matches(b, query with { MaxTime = effective.MaxTime }) && ids.Add(b.Id)).ToArray();
                rows.AddRange(pageRows.Select(IndexRow));
                var pk = Key(org, project, $"builds:v2:{hash}:page:{pages.Count}"); pages.Add(pk);
                for (var i = 0; i < pageRows.Length; i++) locations[pageRows[i].Id] = (pk, i);
                var pageJson = JsonSerializer.Serialize(pageRows, AzdoBuildQueries.JsonOptions);
                pageHashes[pk] = AzdoBuildQueries.Digest(pageJson);
                if (cache is not null) await cache.SetMetadataAsync(pk, pageJson, Retention, ct);
                token = string.IsNullOrWhiteSpace(page.ContinuationToken) ? null : page.ContinuationToken;
                exhausted = token is null;
                if (token is not null && !seen.Add(token)) throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(AcquisitionErrorKind.InvalidResponse, "azdo", "list_builds",
                    new Dictionary<string, object?> { ["org"] = org, ["project"] = project }, "AzDO provider repeated a continuation token."));
                source = new AzdoBuildQuerySource
                {
                    Query = query, EffectiveQuery = effective, ResolvedDefinitions = definitions, PageKeys = pages.ToArray(), PageHashes = new Dictionary<string, string>(pageHashes),
                    SourceComplete = exhausted, SelectedCount = rows.Count, CapturedAt = capturedAt, Scanned = scanned, QueryHash = hash,
                    ViewId = source?.ViewId ?? AzdoBuildQueries.Digest(hash + ":" + capturedAt.ToString("O", CultureInfo.InvariantCulture) + ":" + string.Join(",", pages.Select(pk => pageHashes[pk]))),
                    ContinuationToken = token, SeenTokens = seen.ToArray(), StatusApplied = statusApplied
                };
                var checkpointKey = Key(org, project, $"builds:v2:{hash}:window:0:{request.MaxDiscoveryBuilds ?? int.MaxValue}");
                if (cache is not null) await cache.SetMetadataAsync(exhausted ? completeKey : checkpointKey, JsonSerializer.Serialize(source, AzdoBuildQueries.JsonOptions), Retention, ct);
                if (request.Checkpoint is not null) await request.Checkpoint(source, exhausted ? completeKey : checkpointKey, ct);
                progress?.Invoke($"Scanned {scanned} builds; selected {rows.Count}; sourceComplete={exhausted}.");
                if (!exhausted && (discoveryTarget is { } max && rows.Count >= max || request.MaxScanBuilds is { } scanMax && scanned >= scanMax))
                { key = checkpointKey; break; }
            }
            if (source is null)
            {
                source = new AzdoBuildQuerySource { Query = query, EffectiveQuery = effective, ResolvedDefinitions = definitions, SourceComplete = true,
                    CapturedAt = DateTimeOffset.UtcNow, QueryHash = hash, ViewId = AzdoBuildQueries.Digest(hash + ":empty") };
                if (cache is not null) await cache.SetMetadataAsync(key, JsonSerializer.Serialize(source, AzdoBuildQueries.JsonOptions), Retention, ct);
            }
        }
        if (source.SourceComplete) key = Key(org, project, $"builds:v2:{source.QueryHash}:all");
        rows = rows.Where(b => AzdoBuildQueries.Matches(b, query)).ToList();
        var order = query.QueryOrder ?? "queueTimeDescending";
        DateTimeOffset? Time(AzdoBuild b) => order.StartsWith("finish", StringComparison.OrdinalIgnoreCase) ? b.FinishTime : order.StartsWith("start", StringComparison.OrdinalIgnoreCase) ? b.StartTime : b.QueueTime;
        rows = (order.EndsWith("ascending", StringComparison.OrdinalIgnoreCase) ? rows.OrderBy(Time) : rows.OrderByDescending(Time)).ToList();
        var view = AzdoBuildQueries.Digest(hash + ":" + source.ViewId + ":" + request.Projection);
        if (request.ViewId is not null && request.ViewId != view) throw new ArgumentException("viewId changed; restart at offset 0 for the captured membership.");
        var outputPage = new HlxPageRequest { All = request.All, Offset = request.Offset, Limit = request.Limit ?? request.Filter.Top };
        var limit = outputPage.All ? rows.Count : outputPage.EffectiveLimit(20);
        if (_options.EvalMode && !source.SourceComplete && (request.All || (long)request.Offset + limit > rows.Count)) throw MissingSnapshot(org, project, query);
        var selected = rows.Skip(request.Offset).Take(limit).ToArray();
        var next = request.Offset + selected.Length;
        return new AzdoBuildQueryResult
        {
            Org = org, Project = project, Query = query, EffectiveQuery = source.EffectiveQuery with
            {
                StatusApplied = source.StatusApplied,
                TimeField = (query.QueryOrder ?? "queueTimeDescending").StartsWith("finish", StringComparison.OrdinalIgnoreCase) ? "finishTime" : (query.QueryOrder ?? "").StartsWith("start", StringComparison.OrdinalIgnoreCase) ? "startTime" : "queueTime",
                CompatibilityStrategy = source.StatusApplied == "client" ? "recognized-status-order-fallback" : "provider"
            },
            ResolvedDefinitions = source.ResolvedDefinitions, CapturedAt = source.CapturedAt, SourceComplete = source.SourceComplete,
            Complete = source.SourceComplete && request.Offset == 0 && next >= rows.Count, Total = source.SourceComplete ? rows.Count : null, Scanned = source.Scanned, Pages = source.PageKeys.Count, QueryHash = hash,
            CacheKey = key, ViewId = view,
            Results = cache is null ? selected : new AzdoCachedBuildList(cache, selected.Select(b => locations[b.Id]).ToArray(), source.PageHashes), Offset = request.Offset,
            NextOffset = next < rows.Count || !_options.EvalMode && !source.SourceComplete && selected.Length > 0 ? next : null, Projection = request.Projection, Limit = request.All ? null : limit,
            Recovery = source.SourceComplete || !_options.EvalMode ? null : MissingSnapshot(org, project, query).Error.Resource["recovery"],
            IncompleteDetails = source.SourceComplete ? [] : ["source_incomplete"]
        };
    }

    private HlxAcquisitionException MissingSnapshot(string org, string project, AzdoBuildFilter f)
    {
        var semantic = new Collect.AzdoBuildSetQuery
        {
            Org = org, Project = project, DefinitionName = f.DefinitionName, DefinitionId = f.DefinitionId,
            Branch = f.Branch, PrNumber = f.PrNumber, BuildNumber = f.BuildNumber, Tags = f.Tags?.ToArray(),
            Result = f.Result, Reason = f.Reason, RepositoryId = f.RepositoryId, RepositoryType = f.RepositoryType,
            Status = f.StatusFilter, MinTime = f.MinTime, MaxTime = f.MaxTime, QueryOrder = f.QueryOrder
        };
        var partition = string.IsNullOrEmpty(_options.AuthTokenHash) ? "public" : "cache-" + _options.AuthTokenHash;
        var delivery = new Delivery.FileEvidenceDeliveryStore(_options).PutText(JsonSerializer.Serialize(semantic, AzdoBuildQueries.JsonOptions),
            "application/json", AzdoBuildQueries.Hash(f), null, partition);
        var destination = Path.Combine(Path.GetDirectoryName(delivery.LocalPath)!, "snapshot-" + Guid.NewGuid().ToString("N"));
        var argv = new[] { "hlx", "collect", "azdo-builds", "--query", delivery.LocalPath, "--build-scope", "metadata", "--allow-large", "--export", destination };
        return new HlxAcquisitionException(AcquisitionErrorFactory.Create(AcquisitionErrorKind.NotInSnapshot, "cache", "list_builds",
            new Dictionary<string, object?>
            {
                ["org"] = org, ["project"] = project,
                ["recovery"] = new { kind = "live_recollection", argv, query = delivery, unsetEnvironment = "HLX_EVAL_SNAPSHOT",
                    executionHost = OperatingSystem.IsWindows() ? "powershell" : "posix",
                    command = (OperatingSystem.IsWindows() ? "$env:HLX_EVAL_SNAPSHOT=$null; " : "env -u HLX_EVAL_SNAPSHOT ") + string.Join(" ", argv.Select(AzdoBuildQueries.QuoteArgument)),
                    replay = new { environment = new Dictionary<string, string> { ["HLX_EVAL_SNAPSHOT"] = destination }, argv = new[] { "hlx", "mcp" } } }
            }, "Snapshot does not cover this build query. Unset HLX_EVAL_SNAPSHOT; run the exact live recollection argv, then replay the new snapshot.") with { Source = "snapshot" });
    }

    private static bool IsStatusOrderCompatibility(HlxAcquisitionException ex)
    {
        var message = ex.Message;
        return ex.Error.HttpStatus == 400 && message.Contains("status", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("order", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("incompatib", StringComparison.OrdinalIgnoreCase) || message.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<AzdoBuildQuerySource?> FindCoveredSourceAsync(string org, string project, AzdoBuildFilter query, CancellationToken ct)
    {
        var path = Path.Combine(_options.GetEffectiveCacheRoot(), "manifest", "hlx-collect-manifest.json");
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<Collect.CollectManifest>(await File.ReadAllTextAsync(path, ct), AzdoBuildQueries.JsonOptions);
        if (manifest?.Kind != "hlx.collect.azdo-builds" || manifest.Source.Org != org || manifest.Source.Project != project || manifest.Source.Discovery is not { SourceComplete: true } discovery) return null;
        if (manifest.Source.Query is not { } covered) return null;
        var effectiveCovered = covered with { MaxTime = manifest.Source.EffectiveQuery?.MaxTime ?? covered.MaxTime };
        var requestedCoverage = query with { MaxTime = query.MaxTime ?? effectiveCovered.MaxTime };
        if (!AzdoBuildQueries.Covers(effectiveCovered, requestedCoverage)) return null;
        var expected = Key(org, project, $"builds:v2:{AzdoBuildQueries.Hash(covered)}:all");
        if (discovery.CacheKey != expected) return null;
        var saved = await cache!.GetMetadataAsync(expected, ct);
        return saved is null ? null : JsonSerializer.Deserialize<AzdoBuildQuerySource>(saved, AzdoBuildQueries.JsonOptions);
    }
}

/// <summary>Full provider pages stay on disk; only lightweight row coordinates are retained during projection.</summary>
internal sealed class AzdoCachedBuildList(ICacheStore cache, IReadOnlyList<(string Key, int Index)> locations, IReadOnlyDictionary<string, string> hashes) : IReadOnlyList<AzdoBuild>
{
    public int Count => locations.Count;
    public AzdoBuild this[int index]
    {
        get
        {
            var location = locations[index];
            var page = cache.GetMetadataIgnoringTtlAsync(location.Key).GetAwaiter().GetResult()
                ?? throw new InvalidDataException("Build-query backing page is missing.");
            if (!hashes.TryGetValue(location.Key, out var hash) || AzdoBuildQueries.Digest(page) != hash) throw new InvalidDataException("Build-query page changed after capture.");
            return JsonSerializer.Deserialize<AzdoBuild[]>(page, AzdoBuildQueries.JsonOptions)![location.Index];
        }
    }
    public IEnumerator<AzdoBuild> GetEnumerator()
    {
        string? key = null;
        AzdoBuild[] page = [];
        foreach (var location in locations)
        {
            if (key != location.Key)
            {
                key = location.Key;
                var json = cache.GetMetadataIgnoringTtlAsync(key).GetAwaiter().GetResult()
                    ?? throw new InvalidDataException("Build-query backing page is missing.");
                if (!hashes.TryGetValue(key, out var hash) || AzdoBuildQueries.Digest(json) != hash) throw new InvalidDataException("Build-query page changed after capture.");
                page = JsonSerializer.Deserialize<AzdoBuild[]>(json, AzdoBuildQueries.JsonOptions)!;
            }
            yield return page[location.Index];
        }
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
