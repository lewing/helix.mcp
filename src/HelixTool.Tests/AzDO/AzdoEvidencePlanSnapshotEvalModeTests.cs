// Closes the remaining test gap between two previously separate suites:
//   - SnapshotEvalModeTests.SnapshotCiEvidenceScenarioTests proves individual cached AzDO
//     endpoints (build, timeline) are served offline from a snapshot.
//   - AzdoEvidenceFixtureTests proves AzdoEvidenceMatcher.BuildPlan's mapping/completeness
//     algorithm against real-world fixture data, but drives it directly (bypassing AzdoService
//     and any cache/network layer).
//
// Neither proves the full, real call path: AzdoService.GetEvidencePlanAsync -> CachingAzdoApiClient
// (eval mode) -> OfflineAzdoApiClient, reading build+timeline+artifacts from a snapshot with
// network access genuinely blocked, and still producing the exact deterministic mapping/
// completeness behavior the matcher-level fixture tests already established.
//
// Run: `dotnet test --filter "FullyQualifiedName~AzdoEvidencePlanSnapshotEvalModeTests"`

using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoEvidencePlanSnapshotEvalModeTests : IDisposable
{
    private const string Org = AzdoIdResolver.DefaultOrg;
    private const string Project = AzdoIdResolver.DefaultProject;
    private const int BuildId = 1569889;

    private readonly string _parentDir;
    private readonly string _liveDir;
    private readonly string _snapshotDir;
    private SqliteCacheStore? _evalStore;

    public AzdoEvidencePlanSnapshotEvalModeTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-evidence-eval-{Guid.NewGuid():N}");
        _liveDir = Path.Combine(_parentDir, "live");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        _evalStore?.Dispose();
        try { Directory.Delete(_parentDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Seeds a snapshot (via the real writer -> backup path from
    /// <see cref="SnapshotEvalTestHarness"/>, not direct file copy) with the build, timeline,
    /// and artifacts a real "warm cache capture" would contain for build 1569889 — reusing the
    /// same deterministic 7-job / 14-artifact fixture as the matcher-level fixture tests
    /// (<see cref="AzdoEvidenceFixtureTests.Build1569889Fixture"/>) instead of duplicating it.
    /// When <paramref name="includeArtifacts"/> is false, the artifacts cache entry is omitted
    /// to simulate a partial/incomplete snapshot capture.
    /// </summary>
    private async Task<AzdoService> CreateSnapshotBackedServiceAsync(bool includeArtifacts = true)
    {
        var (jobs, artifacts) = AzdoEvidenceFixtureTests.Build1569889Fixture();

        var build = new AzdoBuild { Id = BuildId, Status = "completed", BuildNumber = "20260827.1" };
        var timeline = new AzdoTimeline { Id = Guid.NewGuid().ToString(), Records = jobs };

        var buildKey = $"azdo:{Org}:{Project}:build:{BuildId}";
        var timelineKey = $"azdo:{Org}:{Project}:timeline:{BuildId}";
        var artifactsKey = $"azdo:{Org}:{Project}:artifacts:{BuildId}";

        await SnapshotEvalTestHarness.CreateStableSnapshotAsync(_liveDir, _snapshotDir, async writer =>
        {
            await writer.SetMetadataAsync(buildKey, JsonSerializer.Serialize(build), TimeSpan.FromHours(4));
            await writer.SetMetadataAsync(timelineKey, JsonSerializer.Serialize(timeline), TimeSpan.FromHours(4));
            if (includeArtifacts)
                await writer.SetMetadataAsync(artifactsKey, JsonSerializer.Serialize(artifacts), TimeSpan.FromHours(4));
        });

        // EvalMode + OfflineAzdoApiClient: any cache miss throws instead of calling the network,
        // so a successful GetEvidencePlanAsync call is itself proof no live AzDO call occurred.
        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        _evalStore = new SqliteCacheStore(evalOpts);
        var evalClient = new CachingAzdoApiClient(new OfflineAzdoApiClient(), _evalStore, evalOpts);
        return new AzdoService(evalClient);
    }

    [Fact]
    public async Task EvalMode_AutoStrategy_ProducesDeterministicCompleteMapping_FromSnapshotOnly()
    {
        var svc = await CreateSnapshotBackedServiceAsync();

        var plan = await svc.GetEvidencePlanAsync(
            BuildId.ToString(),
            new AzdoEvidencePlanOptions
            {
                ArtifactPattern = "Logs_Build_*",
                ArtifactJobPrefix = "Logs_Build_",
                StripAttemptPrefix = true,
                Match = AzdoEvidenceMatchStrategy.Auto,
                JobResults = ["failed", "canceled"]
            });

        Assert.Equal(BuildId, plan.BuildId);
        Assert.False(plan.BuildIncomplete);
        Assert.True(plan.Complete);
        Assert.Empty(plan.IncompleteReasons);
        Assert.Equal(7, plan.Entries.Count);
        Assert.All(plan.Entries, e =>
        {
            Assert.Equal("mapped", e.Status);
            var candidate = Assert.Single(e.Candidates);
            Assert.Equal(2, candidate.Attempt); // source-id resolves the current attempt only
        });
    }

    [Fact]
    public async Task EvalMode_NormalizedExactStrategy_ProducesDeterministicAmbiguity_FromSnapshotOnly()
    {
        // Same snapshot, same fixture data, different match strategy: stripping the attempt
        // prefix collapses Attempt1/Attempt2 artifact names onto the same key, so every job
        // becomes ambiguous instead of mapped. This is the completeness/ambiguity half of the
        // gate the matcher-level fixture tests already assert (B4) — proven here through the
        // real snapshot-backed service call path instead of calling the matcher directly.
        var svc = await CreateSnapshotBackedServiceAsync();

        var plan = await svc.GetEvidencePlanAsync(
            BuildId.ToString(),
            new AzdoEvidencePlanOptions
            {
                ArtifactPattern = "Logs_Build_*",
                ArtifactJobPrefix = "Logs_Build_",
                StripAttemptPrefix = true,
                Match = AzdoEvidenceMatchStrategy.NormalizedExact,
                JobResults = ["failed", "canceled"]
            });

        Assert.False(plan.Complete);
        Assert.Equal(7, plan.Entries.Count);
        Assert.Equal(7, plan.IncompleteReasons.Count);
        Assert.All(plan.Entries, e =>
        {
            Assert.Equal("ambiguous", e.Status);
            Assert.Equal(2, e.Candidates.Count);
            var attempts = e.Candidates.Select(c => c.Attempt).OrderBy(a => a).ToList();
            Assert.Equal([1, 2], attempts);
        });
    }

    [Fact]
    public async Task EvalMode_MissingArtifactsSnapshotEntry_ThrowsEvalModeError_NeverFallsBackToNetwork()
    {
        // Partial snapshot: build + timeline captured, artifacts endpoint never warmed.
        // Proves the cache-miss path is a hard, descriptive failure — not a silent live-network
        // fallback — for every one of the three cached GETs GetEvidencePlanAsync depends on.
        var svc = await CreateSnapshotBackedServiceAsync(includeArtifacts: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetEvidencePlanAsync(
            BuildId.ToString(),
            new AzdoEvidencePlanOptions
            {
                ArtifactPattern = "Logs_Build_*",
                ArtifactJobPrefix = "Logs_Build_",
                StripAttemptPrefix = true,
                Match = AzdoEvidenceMatchStrategy.Auto,
                JobResults = ["failed", "canceled"]
            }));

        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
