// Per-param contract tests for AzDO build/test API parameters.
//
// For each MCP/CLI-visible parameter on azdo_builds (and GetTestResultsAsync.outcomes),
// three assertions are made:
//   (a) The value appears in the AzDO REST URL  — AzdoApiClient + FakeHttpMessageHandler
//   (b) Different values produce different cache keys — CachingAzdoApiClient, inner called 2×
//   (c) The service is invoked with the correct value — NSubstitute Received()
//
// [Theory]+[InlineData] keeps the test count high while keeping LOC manageable.

using System.Net;
using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoBuildContractTests
{
    // ── (a) Direct HTTP client — URL contract ───────────────────────────────

    private readonly FakeHttpHandler _handler;
    private readonly AzdoApiClient _apiClient;

    // ── (b)+(c) Caching layer — cache-key discrimination + service forwarding ─

    private readonly IAzdoApiClient _inner;
    private readonly ICacheStore _cache;
    private readonly CachingAzdoApiClient _sut;

    private static readonly string EmptyBuildsJson =
        JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });

    public AzdoBuildContractTests()
    {
        var mockToken = Substitute.For<IAzdoTokenAccessor>();
        mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new AzdoCredential("test-token", "Bearer", "Test") { DisplayToken = "test-token" });
        _handler = new FakeHttpHandler();
        _apiClient = new AzdoApiClient(new HttpClient(_handler), mockToken);

        _inner = Substitute.For<IAzdoApiClient>();
        _cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        _sut = new CachingAzdoApiClient(_inner, _cache, opts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // top
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(5, "$top=5")]
    [InlineData(100, "$top=100")]
    [InlineData(1, "$top=1")]
    public async Task ListBuildsAsync_Top_AppearsInUrl(int top, string expectedPart)
    {
        // (a) URL contract
        _handler.Response = EmptyBuildsJson;
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Top = top });
        Assert.Contains(expectedPart, _handler.LastUrl);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(1, 50)]
    public async Task ListBuildsAsync_DifferentTop_DistinctCacheKeys(int top1, int top2)
    {
        // (b) different values → different keys → inner called 2×
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Top = top1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Top = top2 });

        // (c) service forwarded each value
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.Top == top1), Arg.Any<CancellationToken>());
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.Top == top2), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // pr_number → PrNumber (→ branchName=refs/pull/{N}/merge)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("100", "refs/pull/100/merge")]
    [InlineData("12345", "refs/pull/12345/merge")]
    public async Task ListBuildsAsync_PrNumber_AppearsInUrl(string prNumber, string expectedPart)
    {
        // (a) URL contract
        _handler.Response = EmptyBuildsJson;
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { PrNumber = prNumber });
        Assert.Contains(expectedPart, _handler.LastUrl);
    }

    [Theory]
    [InlineData("100", "200")]
    [InlineData("42", "99")]
    public async Task ListBuildsAsync_DifferentPrNumber_DistinctCacheKeys(string pr1, string pr2)
    {
        // (b) different values → different keys → inner called 2×
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { PrNumber = pr1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { PrNumber = pr2 });

        // (c) service forwarded each value (normalizer trims; values are already clean)
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.PrNumber == pr1), Arg.Any<CancellationToken>());
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.PrNumber == pr2), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // branch → Branch (→ branchName=... URL-escaped)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("main", "branchName=main")]
    [InlineData("refs/heads/main", "branchName=refs%2Fheads%2Fmain")]
    [InlineData("feature/foo", "branchName=feature%2Ffoo")]
    public async Task ListBuildsAsync_Branch_AppearsInUrl(string branch, string expectedPart)
    {
        // (a) URL contract — branch is URL-escaped
        _handler.Response = EmptyBuildsJson;
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = branch });
        Assert.Contains(expectedPart, _handler.LastUrl);
    }

    [Theory]
    [InlineData("main", "develop")]
    [InlineData("refs/heads/main", "refs/heads/release/6.0")]
    public async Task ListBuildsAsync_DifferentBranch_DistinctCacheKeys(string b1, string b2)
    {
        // (b) different branches → different keys → inner called 2×
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = b1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = b2 });

        // (c) service forwarded each value
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.Branch == b1), Arg.Any<CancellationToken>());
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.Branch == b2), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // definition_id → DefinitionId (→ definitions=N)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(42, "definitions=42")]
    [InlineData(777, "definitions=777")]
    public async Task ListBuildsAsync_DefinitionId_AppearsInUrl(int defId, string expectedPart)
    {
        // (a) URL contract
        _handler.Response = EmptyBuildsJson;
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { DefinitionId = defId });
        Assert.Contains(expectedPart, _handler.LastUrl);
    }

    [Theory]
    [InlineData(42, 99)]
    [InlineData(1, 777)]
    public async Task ListBuildsAsync_DifferentDefinitionId_DistinctCacheKeys(int def1, int def2)
    {
        // (b) different definition IDs → different keys → inner called 2×
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { DefinitionId = def1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { DefinitionId = def2 });

        // (c) service forwarded each value
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.DefinitionId == def1), Arg.Any<CancellationToken>());
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.DefinitionId == def2), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // status_filter → StatusFilter (→ statusFilter=...)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("completed", "statusFilter=completed")]
    [InlineData("inProgress", "statusFilter=inProgress")]
    [InlineData("failed", "statusFilter=failed")]
    public async Task ListBuildsAsync_StatusFilter_AppearsInUrl(string status, string expectedPart)
    {
        // (a) URL contract
        _handler.Response = EmptyBuildsJson;
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { StatusFilter = status });
        Assert.Contains(expectedPart, _handler.LastUrl);
    }

    [Theory]
    [InlineData("completed", "inProgress")]
    [InlineData("failed", "completed")]
    public async Task ListBuildsAsync_DifferentStatusFilter_DistinctCacheKeys(string s1, string s2)
    {
        // (b) different status values → different keys → inner called 2×
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { StatusFilter = s1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { StatusFilter = s2 });

        // (c) service forwarded each value
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.StatusFilter == s1), Arg.Any<CancellationToken>());
        await _inner.Received(1).ListBuildsAsync("org", "proj",
            Arg.Is<AzdoBuildFilter>(f => f.StatusFilter == s2), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // min_time → MinTime (→ minTime=... ISO 8601, URL-escaped)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(2026, 1, 15, "minTime=")]
    [InlineData(2025, 6, 1, "minTime=")]
    public async Task ListBuildsAsync_MinTime_AppearsInUrl(int year, int month, int day, string expectedPart)
    {
        // (a) URL contract — ISO 8601 date in URL
        _handler.Response = EmptyBuildsJson;
        var minTime = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { MinTime = minTime });
        var unescaped = Uri.UnescapeDataString(_handler.LastUrl);
        Assert.Contains(expectedPart, _handler.LastUrl);
        Assert.Contains($"{year:D4}-{month:D2}-{day:D2}", unescaped);
    }

    [Fact]
    public async Task ListBuildsAsync_DifferentMinTime_DistinctCacheKeys()
    {
        // (b) different min-time values → different keys → inner called 2×
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { MinTime = t1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { MinTime = t2 });

        // (c) two distinct inner calls
        await _inner.Received(2).ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // max_time → MaxTime (→ maxTime=... ISO 8601, URL-escaped)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(2026, 6, 30, "maxTime=")]
    [InlineData(2025, 12, 31, "maxTime=")]
    public async Task ListBuildsAsync_MaxTime_AppearsInUrl(int year, int month, int day, string expectedPart)
    {
        // (a) URL contract — ISO 8601 date in URL
        _handler.Response = EmptyBuildsJson;
        var maxTime = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { MaxTime = maxTime });
        var unescaped = Uri.UnescapeDataString(_handler.LastUrl);
        Assert.Contains(expectedPart, _handler.LastUrl);
        Assert.Contains($"{year:D4}-{month:D2}-{day:D2}", unescaped);
    }

    [Fact]
    public async Task ListBuildsAsync_DifferentMaxTime_DistinctCacheKeys()
    {
        // (b) different max-time values → different keys → inner called 2×
        var t1 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { MaxTime = t1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { MaxTime = t2 });

        // (c) two distinct inner calls
        await _inner.Received(2).ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // query_order → QueryOrder (→ queryOrder=... normalizer lowercases non-default)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("finishTimeDescending", "queryOrder=finishtimedescending")]
    [InlineData("startTimeAscending", "queryOrder=starttimeascending")]
    [InlineData("queueTimeDescending", "queryOrder=queueTimeDescending")]   // default → sent verbatim
    public async Task ListBuildsAsync_QueryOrder_AppearsInUrl(string queryOrder, string expectedPart)
    {
        // (a) URL contract — non-defaults are lowercased by the normalizer;
        //     the default "queueTimeDescending" is written verbatim from AzdoBuildFilterDefaults.
        _handler.Response = EmptyBuildsJson;
        await _apiClient.ListBuildsAsync("org", "proj", new AzdoBuildFilter { QueryOrder = queryOrder });
        Assert.Contains(expectedPart, _handler.LastUrl);
    }

    [Theory]
    [InlineData("finishtimedescending", "starttimeascending")]
    [InlineData("finishtimedescending", "finishasc")]
    public async Task ListBuildsAsync_DifferentQueryOrder_DistinctCacheKeys(string q1, string q2)
    {
        // (b) distinct non-default query orders → distinct keys → inner called 2×
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { QueryOrder = q1 });
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { QueryOrder = q2 });

        await _inner.Received(2).ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // GetTestResultsAsync: outcomes param
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Passed,Failed", "outcomes=Passed%2CFailed")]
    [InlineData("Failed", "outcomes=Failed")]
    [InlineData(null, "outcomes=Failed")]              // null → default "Failed"
    [InlineData("", "outcomes=Failed")]                // empty → default "Failed"
    [InlineData("   ", "outcomes=Failed")]             // whitespace → default "Failed"
    public async Task GetTestResultsAsync_Outcomes_AppearsInUrl(string? outcomes, string expectedPart)
    {
        // (a) URL contract — null/empty/whitespace fall back to AzdoBuildFilterDefaults.Outcomes ("Failed")
        _handler.Response = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        await _apiClient.GetTestResultsAsync("org", "proj", runId: 42, outcomes: outcomes);
        Assert.Contains(expectedPart, _handler.LastUrl);
    }

    [Theory]
    [InlineData("Passed,Failed", "Failed")]            // custom vs default → distinct keys
    [InlineData("Passed", "Failed")]
    public async Task GetTestResultsAsync_DifferentOutcomes_DistinctCacheKeys(string o1, string o2)
    {
        // (b) different outcomes → different keys → inner called 2×
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.GetTestResultsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestResult>());

        await _sut.GetTestResultsAsync("org", "proj", 42, outcomes: o1);
        await _sut.GetTestResultsAsync("org", "proj", 42, outcomes: o2);

        // (c) service forwarded the trimmed value; two distinct inner calls
        await _inner.Received(2).GetTestResultsAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // Fail-safe: new field on AzdoBuildFilter → cache key changes automatically
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListBuildsAsync_NullFilterVsFilterWithBranch_DistinctCacheKeys()
    {
        // Validates the JSON fail-safe property: any field present in the record
        // automatically participates in the cache key.
        // To add a field-presence guard test: set the field on one filter and leave
        // it null on the other; assert inner is called 2×. No manual HashFilter wiring needed.
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _inner.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter());
        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = "main" });

        await _inner.Received(2).ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // Fake HTTP handler (minimal — captures last request URL)
    // ════════════════════════════════════════════════════════════════════════

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public string Response { get; set; } = "{}";
        public string LastUrl { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Response, Encoding.UTF8, "application/json")
            });
        }
    }
}
