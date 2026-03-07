using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoServiceTests
{
    private readonly IAzdoApiClient _mockApi;
    private readonly AzdoService _svc;

    public AzdoServiceTests()
    {
        _mockApi = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_mockApi);
    }

    // ── URL resolution integration ──────────────────────────────────

    [Fact]
    public async Task GetBuildSummaryAsync_PlainId_UsesDefaultOrgProject()
    {
        var build = MakeBuild(42);
        _mockApi.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("42");

        Assert.Equal(42, result.Id);
        await _mockApi.Received(1).GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildSummaryAsync_DevAzureUrl_ParsesOrgAndProject()
    {
        var build = MakeBuild(123);
        _mockApi.GetBuildAsync("myorg", "myproject", 123, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=123");

        Assert.Equal(123, result.Id);
        await _mockApi.Received(1).GetBuildAsync("myorg", "myproject", 123, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildSummaryAsync_VisualStudioUrl_ParsesOrgAndProject()
    {
        var build = MakeBuild(456);
        _mockApi.GetBuildAsync("dnceng", "internal", 456, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync(
            "https://dnceng.visualstudio.com/internal/_build/results?buildId=456");

        Assert.Equal(456, result.Id);
        await _mockApi.Received(1).GetBuildAsync("dnceng", "internal", 456, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTimelineAsync_PlainId_ResolvesToDefaultOrgProject()
    {
        var timeline = new AzdoTimeline { Id = "tl1" };
        _mockApi.GetTimelineAsync("dnceng-public", "public", 99, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await _svc.GetTimelineAsync("99");

        Assert.NotNull(result);
        Assert.Equal("tl1", result!.Id);
    }

    [Fact]
    public async Task GetBuildLogAsync_Url_ResolvesCorrectly()
    {
        _mockApi.GetBuildLogAsync("myorg", "proj", 10, 5, Arg.Any<CancellationToken>())
            .Returns("log content");

        var result = await _svc.GetBuildLogAsync(
            "https://dev.azure.com/myorg/proj/_build/results?buildId=10", 5);

        Assert.Equal("log content", result);
    }

    [Fact]
    public async Task GetBuildChangesAsync_PlainId_PassesDefaultsToClient()
    {
        var changes = new List<AzdoBuildChange>
        {
            new() { Id = "abc123", Message = "fix bug" }
        };
        _mockApi.GetBuildChangesAsync("dnceng-public", "public", 7, Arg.Any<CancellationToken>())
            .Returns(changes);

        var result = await _svc.GetBuildChangesAsync("7");

        Assert.Single(result);
        Assert.Equal("abc123", result[0].Id);
    }

    [Fact]
    public async Task GetTestRunsAsync_PlainId_PassesDefaultsToClient()
    {
        var runs = new List<AzdoTestRun> { new() { Id = 1, Name = "run1" } };
        _mockApi.GetTestRunsAsync("dnceng-public", "public", 50, Arg.Any<CancellationToken>())
            .Returns(runs);

        var result = await _svc.GetTestRunsAsync("50");

        Assert.Single(result);
        Assert.Equal("run1", result[0].Name);
    }

    [Fact]
    public async Task GetTestResultsAsync_Url_ResolvesOrgProject()
    {
        var results = new List<AzdoTestResult>
        {
            new() { Id = 1, TestCaseTitle = "TestA", Outcome = "Failed" }
        };
        _mockApi.GetTestResultsAsync("myorg", "proj", 77, Arg.Any<CancellationToken>())
            .Returns(results);

        var result = await _svc.GetTestResultsAsync(
            "https://dev.azure.com/myorg/proj/_build/results?buildId=999", 77);

        Assert.Single(result);
        Assert.Equal("TestA", result[0].TestCaseTitle);
    }

    // ── GetBuildSummaryAsync field mapping ───────────────────────────

    [Fact]
    public async Task GetBuildSummaryAsync_MapsAllFieldsCorrectly()
    {
        var start = new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero);
        var finish = new DateTimeOffset(2025, 7, 18, 10, 5, 30, TimeSpan.Zero);

        var build = new AzdoBuild
        {
            Id = 42,
            BuildNumber = "20250718.1",
            Status = "completed",
            Result = "succeeded",
            Definition = new AzdoBuildDefinition { Id = 100, Name = "runtime" },
            SourceBranch = "refs/heads/main",
            SourceVersion = "abc123def",
            QueueTime = start.AddMinutes(-1),
            StartTime = start,
            FinishTime = finish,
            RequestedFor = new AzdoIdentityRef { DisplayName = "Larry" }
        };

        _mockApi.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("42");

        Assert.Equal(42, result.Id);
        Assert.Equal("20250718.1", result.BuildNumber);
        Assert.Equal("completed", result.Status);
        Assert.Equal("succeeded", result.Result);
        Assert.Equal("runtime", result.DefinitionName);
        Assert.Equal(100, result.DefinitionId);
        Assert.Equal("refs/heads/main", result.SourceBranch);
        Assert.Equal("abc123def", result.SourceVersion);
        Assert.Equal(start, result.StartTime);
        Assert.Equal(finish, result.FinishTime);
        Assert.Equal("Larry", result.RequestedFor);
    }

    [Fact]
    public async Task GetBuildSummaryAsync_Duration_ComputedFromStartAndFinish()
    {
        var start = new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero);
        var finish = new DateTimeOffset(2025, 7, 18, 10, 5, 30, TimeSpan.Zero);

        var build = new AzdoBuild
        {
            Id = 1,
            StartTime = start,
            FinishTime = finish
        };

        _mockApi.GetBuildAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("1");

        Assert.NotNull(result.Duration);
        Assert.Equal(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), result.Duration.Value);
    }

    [Fact]
    public async Task GetBuildSummaryAsync_NullStartOrFinish_DurationIsNull()
    {
        var build = new AzdoBuild { Id = 1, StartTime = null, FinishTime = null };
        _mockApi.GetBuildAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("1");

        Assert.Null(result.Duration);
    }

    [Fact]
    public async Task GetBuildSummaryAsync_WebUrl_ContainsOrgProjectBuildId()
    {
        var build = MakeBuild(42);
        _mockApi.GetBuildAsync("myorg", "myproject", 42, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=42");

        Assert.Equal("https://dev.azure.com/myorg/myproject/_build/results?buildId=42", result.WebUrl);
    }

    [Fact]
    public async Task GetBuildSummaryAsync_NullDefinition_DefinitionFieldsNull()
    {
        var build = new AzdoBuild { Id = 1, Definition = null };
        _mockApi.GetBuildAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("1");

        Assert.Null(result.DefinitionName);
        Assert.Null(result.DefinitionId);
    }

    // ── GetBuildLogAsync with tailLines ──────────────────────────────

    [Fact]
    public async Task GetBuildLogAsync_TailLines_ReturnsLastNLines()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<CancellationToken>())
            .Returns(lines);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 3);

        Assert.NotNull(result);
        var resultLines = result!.Split('\n');
        Assert.Equal(3, resultLines.Length);
        Assert.Equal("line 18", resultLines[0]);
        Assert.Equal("line 19", resultLines[1]);
        Assert.Equal("line 20", resultLines[2]);
    }

    [Fact]
    public async Task GetBuildLogAsync_TailLinesExceedsTotalLines_ReturnsAllContent()
    {
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<CancellationToken>())
            .Returns("line 1\nline 2");

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 100);

        Assert.Equal("line 1\nline 2", result);
    }

    [Fact]
    public async Task GetBuildLogAsync_NullTailLines_ReturnsFullContent()
    {
        var full = "line 1\nline 2\nline 3";
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<CancellationToken>())
            .Returns(full);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: null);

        Assert.Equal(full, result);
    }

    [Fact]
    public async Task GetBuildLogAsync_ZeroTailLines_ReturnsFullContent()
    {
        var full = "line 1\nline 2";
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<CancellationToken>())
            .Returns(full);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 0);

        Assert.Equal(full, result);
    }

    [Fact]
    public async Task GetBuildLogAsync_NegativeTailLines_ReturnsFullContent()
    {
        var full = "line 1\nline 2";
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<CancellationToken>())
            .Returns(full);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: -1);

        Assert.Equal(full, result);
    }

    // ── Null handling ────────────────────────────────────────────────

    [Fact]
    public async Task GetBuildSummaryAsync_NullBuild_ThrowsInvalidOperation()
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 999, Arg.Any<CancellationToken>())
            .Returns((AzdoBuild?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.GetBuildSummaryAsync("999"));
    }

    [Fact]
    public async Task GetTimelineAsync_NullResult_ReturnsNull()
    {
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns((AzdoTimeline?)null);

        var result = await _svc.GetTimelineAsync("1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBuildLogAsync_NullContent_ReturnsNull()
    {
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _svc.GetBuildLogAsync("1", 5);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBuildLogAsync_NullContent_IgnoresTailLines()
    {
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 10);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListBuildsAsync_EmptyList_ReturnsEmpty()
    {
        _mockApi.ListBuildsAsync("org", "proj", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        var result = await _svc.ListBuildsAsync("org", "proj", new AzdoBuildFilter());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBuildChangesAsync_EmptyList_ReturnsEmpty()
    {
        _mockApi.GetBuildChangesAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildChange>());

        var result = await _svc.GetBuildChangesAsync("1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTestRunsAsync_EmptyList_ReturnsEmpty()
    {
        _mockApi.GetTestRunsAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestRun>());

        var result = await _svc.GetTestRunsAsync("1");

        Assert.Empty(result);
    }

    // ── Error propagation ────────────────────────────────────────────

    [Fact]
    public async Task GetBuildSummaryAsync_InvalidUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetBuildSummaryAsync("not-a-valid-id"));
    }

    [Fact]
    public async Task GetTimelineAsync_InvalidUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetTimelineAsync("garbage"));
    }

    [Fact]
    public async Task GetBuildLogAsync_InvalidUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetBuildLogAsync("garbage", 1));
    }

    [Fact]
    public async Task GetBuildChangesAsync_EmptyString_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetBuildChangesAsync(""));
    }

    [Fact]
    public async Task GetTestRunsAsync_NullString_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetTestRunsAsync(null!));
    }

    [Fact]
    public async Task ListBuildsAsync_PassesFilterToClient()
    {
        var filter = new AzdoBuildFilter
        {
            Top = 5,
            Branch = "refs/heads/main",
            DefinitionId = 42,
            StatusFilter = "completed"
        };
        _mockApi.ListBuildsAsync("org", "proj", filter, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _svc.ListBuildsAsync("org", "proj", filter);

        await _mockApi.Received(1).ListBuildsAsync("org", "proj", filter, Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AzdoBuild MakeBuild(int id, string status = "completed") =>
        new()
        {
            Id = id,
            BuildNumber = $"2025.{id}",
            Status = status,
            Result = "succeeded",
            SourceBranch = "refs/heads/main"
        };
}
