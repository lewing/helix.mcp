using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Contract tests for pagination standardization across all list-returning MCP tools.
/// Per Dallas's spec (.squad/decisions.md#pagination-architecture):
/// - All tools honor 'top' parameter (or equivalent)
/// - When result count == top, truncated = true (more may exist)
/// - When result count &lt; top, truncated = false
/// - LimitedResults&lt;T&gt; envelope has stable shape: {results, truncated, total?, note?}
/// </summary>
public class PaginationContractTests
{
    // ──────────────────────────────────────────────────────────────────
    // Core Helper: CreateLimitedResults behavior
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateLimitedResults_WhenCountEqualsTop_SetsTruncatedTrue()
    {
        // Arrange: exactly 5 results with top=5
        var results = new List<string> { "a", "b", "c", "d", "e" }.AsReadOnly();
        int top = 5;

        // Act: call via reflection (private helper on AzdoMcpTools)
        var limited = InvokeCreateLimitedResults(results, top);

        // Assert
        Assert.True(limited.Truncated);
        Assert.NotNull(limited.Note);
        Assert.Contains("limited to 5", limited.Note);
        Assert.Equal(5, limited.Count);
    }

    [Fact]
    public void CreateLimitedResults_WhenCountBelowTop_SetsTruncatedFalse()
    {
        // Arrange: 3 results with top=5
        var results = new List<string> { "a", "b", "c" }.AsReadOnly();
        int top = 5;

        // Act
        var limited = InvokeCreateLimitedResults(results, top);

        // Assert
        Assert.False(limited.Truncated);
        Assert.Null(limited.Note);
        Assert.Equal(3, limited.Count);
    }

    [Fact]
    public void CreateLimitedResults_WhenTopIsZero_SetsTruncatedFalse()
    {
        // Arrange: 3 results with top=0 (unbounded)
        var results = new List<string> { "a", "b", "c" }.AsReadOnly();
        int top = 0;

        // Act
        var limited = InvokeCreateLimitedResults(results, top);

        // Assert
        Assert.False(limited.Truncated);
        Assert.Null(limited.Note);
    }

    [Fact]
    public void CreateLimitedResults_EmptyResults_ReturnsFalse()
    {
        // Arrange: no results
        var results = new List<string>().AsReadOnly();
        int top = 10;

        // Act
        var limited = InvokeCreateLimitedResults(results, top);

        // Assert
        Assert.False(limited.Truncated);
        Assert.Null(limited.Note);
        Assert.Empty(limited.Results);
    }

    [Fact]
    public void CreateLimitedResults_CountExceedsTop_StillSetsTruncatedTrue()
    {
        // Edge case: if service returns MORE than top (shouldn't happen but test robustness)
        var results = new List<int> { 1, 2, 3, 4, 5, 6, 7 }.AsReadOnly();
        int top = 5;

        // Act
        var limited = InvokeCreateLimitedResults(results, top);

        // Assert: truncated because count >= top
        Assert.True(limited.Truncated);
    }

    // ──────────────────────────────────────────────────────────────────
    // Phase 1 (🔴): azdo_changes, azdo_test_runs
    // These should NOW return LimitedResults<T> instead of raw lists
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AzdoChanges_WhenCountEqualsTop_ReturnsTruncatedTrue()
    {
        // Arrange
        var mockApi = Substitute.For<IAzdoApiClient>();
        var changes = Enumerable.Range(1, 20)
            .Select(i => new AzdoBuildChange
            {
                Id = $"commit{i}",
                Message = $"Change {i}",
                Author = new AzdoChangeAuthor { DisplayName = "dev" },
                Timestamp = DateTimeOffset.UtcNow
            })
            .ToList()
            .AsReadOnly();

        mockApi.GetBuildChangesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(changes);

        var tools = CreateAzdoTools(mockApi);

        // Act: request top=20, get exactly 20
        var result = await tools.Changes("123", top: 20);

        // Assert: should be wrapped in LimitedResults<T> with truncated=true
        Assert.IsType<LimitedResults<AzdoBuildChange>>(result);
        var limited = (LimitedResults<AzdoBuildChange>)result;
        Assert.True(limited.Truncated);
        Assert.NotNull(limited.Note);
        Assert.Equal(20, limited.Count);
    }

    [Fact]
    public async Task AzdoChanges_WhenCountBelowTop_ReturnsTruncatedFalse()
    {
        // Arrange
        var mockApi = Substitute.For<IAzdoApiClient>();
        var changes = Enumerable.Range(1, 5)
            .Select(i => new AzdoBuildChange { Id = $"c{i}", Message = $"msg{i}" })
            .ToList()
            .AsReadOnly();

        mockApi.GetBuildChangesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(changes);

        var tools = CreateAzdoTools(mockApi);

        // Act: request top=20, get only 5
        var result = await tools.Changes("123", top: 20);

        // Assert
        var limited = (LimitedResults<AzdoBuildChange>)result;
        Assert.False(limited.Truncated);
        Assert.Null(limited.Note);
        Assert.Equal(5, limited.Count);
    }

    [Fact]
    public async Task AzdoTestRuns_WhenCountEqualsTop_ReturnsTruncatedTrue()
    {
        // Arrange
        var mockApi = Substitute.For<IAzdoApiClient>();
        var runs = Enumerable.Range(1, 50)
            .Select(i => new AzdoTestRun
            {
                Id = i,
                Name = $"Run {i}",
                State = "completed",
                TotalTests = 100
            })
            .ToList()
            .AsReadOnly();

        mockApi.GetTestRunsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(runs);

        var tools = CreateAzdoTools(mockApi);

        // Act: request top=50, get exactly 50
        var result = await tools.TestRuns("456", top: 50);

        // Assert
        Assert.IsType<LimitedResults<AzdoTestRun>>(result);
        var limited = (LimitedResults<AzdoTestRun>)result;
        Assert.True(limited.Truncated);
        Assert.NotNull(limited.Note);
        Assert.Contains("50", limited.Note);
        Assert.Equal(50, limited.Count);
    }

    [Fact]
    public async Task AzdoTestRuns_WhenCountBelowTop_ReturnsTruncatedFalse()
    {
        // Arrange
        var mockApi = Substitute.For<IAzdoApiClient>();
        var runs = new List<AzdoTestRun>
        {
            new() { Id = 1, Name = "Run 1", State = "completed" },
            new() { Id = 2, Name = "Run 2", State = "completed" }
        }.AsReadOnly();

        mockApi.GetTestRunsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(runs);

        var tools = CreateAzdoTools(mockApi);

        // Act: request top=50, get only 2
        var result = await tools.TestRuns("456", top: 50);

        // Assert
        var limited = (LimitedResults<AzdoTestRun>)result;
        Assert.False(limited.Truncated);
        Assert.Null(limited.Note);
        Assert.Equal(2, limited.Count);
    }

    // ──────────────────────────────────────────────────────────────────
    // Default 'top' parameter tests — ensure sane defaults exist
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AzdoBuilds_DefaultTop_IsReasonable()
    {
        // Verify default top is not unbounded (should be 20 per spec)
        var mockApi = Substitute.For<IAzdoApiClient>();
        mockApi.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        var tools = CreateAzdoTools(mockApi);

        // Act: call without top parameter
        await tools.Builds();

        // Assert: should have passed top=20 to service
        await mockApi.Received(1).ListBuildsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<AzdoBuildFilter>(f => f.Top == 20),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AzdoTestResults_DefaultTop_IsReasonable()
    {
        // Default should be 200 per spec (detail tool, needs more context)
        var mockApi = Substitute.For<IAzdoApiClient>();
        mockApi.GetTestResultsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestResult>());

        var tools = CreateAzdoTools(mockApi);

        // Act: call without top parameter
        await tools.TestResults("123", runId: 1);

        // Assert: should have passed top=200 to service
        await mockApi.Received(1).GetTestResultsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            200, // default top
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AzdoChanges_DefaultTop_IsReasonable()
    {
        // Default should be 20 per spec (list tool)
        var mockApi = Substitute.For<IAzdoApiClient>();
        mockApi.GetBuildChangesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildChange>());

        var tools = CreateAzdoTools(mockApi);

        // Act: call without top parameter - should use default
        await tools.Changes("789");

        // Assert: should have passed top=20 to service
        await mockApi.Received(1).GetBuildChangesAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            20, // default top
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AzdoTestRuns_DefaultTop_IsReasonable()
    {
        // Default should be 50 per spec (list tool)
        var mockApi = Substitute.For<IAzdoApiClient>();
        mockApi.GetTestRunsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestRun>());

        var tools = CreateAzdoTools(mockApi);

        // Act: call without top parameter
        await tools.TestRuns("999");

        // Assert: should have passed top=50 to service
        await mockApi.Received(1).GetTestRunsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            50, // default top
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────
    // Test helpers
    // ──────────────────────────────────────────────────────────────────

    private static LimitedResults<T> InvokeCreateLimitedResults<T>(IReadOnlyList<T> results, int top)
    {
        // Use reflection to call private static CreateLimitedResults on AzdoMcpTools
        var method = typeof(AzdoMcpTools).GetMethod(
            "CreateLimitedResults",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var genericMethod = method!.MakeGenericMethod(typeof(T));
        var result = genericMethod.Invoke(null, [results, top]);

        return (LimitedResults<T>)result!;
    }

    private static AzdoMcpTools CreateAzdoTools(IAzdoApiClient mockApi)
        => new(new AzdoService(mockApi), Substitute.For<IAzdoTokenAccessor>());
}
