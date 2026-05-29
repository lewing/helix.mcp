using HelixTool.Core.AzDO;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class TrueTestCountTests
{
    private readonly IAzdoApiClient _mockApi;
    private readonly AzdoService _svc;

    private const string Org = "dnceng-public";
    private const string Project = "public";

    public TrueTestCountTests()
    {
        _mockApi = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_mockApi);
    }

    [Fact]
    public async Task AllSimpleTests_ReportsCorrectCount()
    {
        // All results have null resultGroupType → trueCount == reportedCount
        var runs = new List<AzdoTestRun> { new() { Id = 1, Name = "Run1" } };
        var results = new List<AzdoTestResult>
        {
            new() { Id = 100, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 101, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 102, Outcome = "Failed", ResultGroupType = null },
        };

        SetupRuns(42, runs);
        SetupResults(1, results);

        var result = await _svc.GetTrueTestCountAsync("42");

        Assert.Equal(3, result.ReportedCount);
        Assert.Equal(3, result.TrueCount);
        Assert.Equal(0, result.TheoryParentCount);
        Assert.Equal(0, result.TheorySubResultTotal);
        Assert.Equal(0, result.FailedToExpand);
        Assert.Single(result.Runs);
    }

    [Fact]
    public async Task DataDrivenTests_ExpandsSubResults()
    {
        // dataDriven results get SubResults expanded, true count reflects sub-result count
        var runs = new List<AzdoTestRun> { new() { Id = 1, Name = "Run1" } };
        var results = new List<AzdoTestResult>
        {
            new() { Id = 100, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 200, Outcome = "Passed", ResultGroupType = "dataDriven" },
        };

        SetupRuns(42, runs);
        SetupResults(1, results);

        // The dataDriven parent expands to 5 sub-results
        _mockApi.GetTestResultWithSubResultsAsync(Org, Project, 1, 200, Arg.Any<CancellationToken>())
            .Returns(new AzdoTestResult
            {
                Id = 200,
                ResultGroupType = "dataDriven",
                SubResults = new List<AzdoTestSubResult>
                {
                    new() { Id = 1, Outcome = "Passed" },
                    new() { Id = 2, Outcome = "Passed" },
                    new() { Id = 3, Outcome = "Passed" },
                    new() { Id = 4, Outcome = "Failed" },
                    new() { Id = 5, Outcome = "Passed" },
                }
            });

        var result = await _svc.GetTrueTestCountAsync("42");

        Assert.Equal(2, result.ReportedCount);        // 2 top-level results
        Assert.Equal(6, result.TrueCount);             // 1 simple + 5 sub-results
        Assert.Equal(1, result.TheoryParentCount);
        Assert.Equal(5, result.TheorySubResultTotal);
        Assert.Equal(0, result.FailedToExpand);
    }

    [Fact]
    public async Task MixedSimpleAndDataDriven_CombinesCorrectly()
    {
        var runs = new List<AzdoTestRun> { new() { Id = 1, Name = "Run1" } };
        var results = new List<AzdoTestResult>
        {
            new() { Id = 10, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 11, Outcome = "Failed", ResultGroupType = null },
            new() { Id = 12, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 20, Outcome = "Passed", ResultGroupType = "dataDriven" },
            new() { Id = 21, Outcome = "Passed", ResultGroupType = "orderedTest" },
        };

        SetupRuns(42, runs);
        SetupResults(1, results);

        // dataDriven parent → 3 sub-results
        _mockApi.GetTestResultWithSubResultsAsync(Org, Project, 1, 20, Arg.Any<CancellationToken>())
            .Returns(new AzdoTestResult
            {
                Id = 20,
                SubResults = new List<AzdoTestSubResult>
                {
                    new() { Id = 1, Outcome = "Passed" },
                    new() { Id = 2, Outcome = "Passed" },
                    new() { Id = 3, Outcome = "Passed" },
                }
            });

        // orderedTest parent → 2 sub-results
        _mockApi.GetTestResultWithSubResultsAsync(Org, Project, 1, 21, Arg.Any<CancellationToken>())
            .Returns(new AzdoTestResult
            {
                Id = 21,
                SubResults = new List<AzdoTestSubResult>
                {
                    new() { Id = 1, Outcome = "Passed" },
                    new() { Id = 2, Outcome = "Failed" },
                }
            });

        var result = await _svc.GetTrueTestCountAsync("42");

        Assert.Equal(5, result.ReportedCount);         // 5 top-level
        Assert.Equal(8, result.TrueCount);             // 3 simple + 3 sub + 2 sub
        Assert.Equal(2, result.TheoryParentCount);
        Assert.Equal(5, result.TheorySubResultTotal);  // 3 + 2
        Assert.Equal(0, result.FailedToExpand);
    }

    [Fact]
    public async Task SubResultsFetchFails_GracefulDegradation()
    {
        var runs = new List<AzdoTestRun> { new() { Id = 1, Name = "Run1" } };
        var results = new List<AzdoTestResult>
        {
            new() { Id = 10, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 20, Outcome = "Passed", ResultGroupType = "dataDriven" },
        };

        SetupRuns(42, runs);
        SetupResults(1, results);

        // SubResults fetch throws
        _mockApi.GetTestResultWithSubResultsAsync(Org, Project, 1, 20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("API error"));

        var result = await _svc.GetTrueTestCountAsync("42");

        Assert.Equal(2, result.ReportedCount);
        Assert.Equal(2, result.TrueCount);             // 1 simple + 1 failed-to-expand
        Assert.Equal(1, result.TheoryParentCount);
        Assert.Equal(0, result.TheorySubResultTotal);
        Assert.Equal(1, result.FailedToExpand);
    }

    [Fact]
    public async Task EmptyTestRun_ReturnsZeros()
    {
        var runs = new List<AzdoTestRun> { new() { Id = 1, Name = "EmptyRun" } };
        SetupRuns(42, runs);
        SetupResults(1, new List<AzdoTestResult>());

        var result = await _svc.GetTrueTestCountAsync("42");

        Assert.Equal(0, result.ReportedCount);
        Assert.Equal(0, result.TrueCount);
        Assert.Equal(0, result.TheoryParentCount);
        Assert.Equal(0, result.TheorySubResultTotal);
        Assert.Equal(0, result.FailedToExpand);
        Assert.Single(result.Runs);
    }

    [Fact]
    public async Task MultipleRuns_AggregatesAcrossRuns()
    {
        var runs = new List<AzdoTestRun>
        {
            new() { Id = 1, Name = "Run1" },
            new() { Id = 2, Name = "Run2" },
        };

        var resultsRun1 = new List<AzdoTestResult>
        {
            new() { Id = 10, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 20, Outcome = "Passed", ResultGroupType = "dataDriven" },
        };

        var resultsRun2 = new List<AzdoTestResult>
        {
            new() { Id = 30, Outcome = "Passed", ResultGroupType = null },
            new() { Id = 31, Outcome = "Failed", ResultGroupType = null },
            new() { Id = 40, Outcome = "Passed", ResultGroupType = "dataDriven" },
        };

        SetupRuns(42, runs);
        SetupResults(1, resultsRun1);
        SetupResults(2, resultsRun2);

        // Run1: dataDriven parent → 4 sub-results
        _mockApi.GetTestResultWithSubResultsAsync(Org, Project, 1, 20, Arg.Any<CancellationToken>())
            .Returns(new AzdoTestResult
            {
                Id = 20,
                SubResults = new List<AzdoTestSubResult>
                {
                    new() { Id = 1, Outcome = "Passed" },
                    new() { Id = 2, Outcome = "Passed" },
                    new() { Id = 3, Outcome = "Passed" },
                    new() { Id = 4, Outcome = "Passed" },
                }
            });

        // Run2: dataDriven parent → 2 sub-results
        _mockApi.GetTestResultWithSubResultsAsync(Org, Project, 2, 40, Arg.Any<CancellationToken>())
            .Returns(new AzdoTestResult
            {
                Id = 40,
                SubResults = new List<AzdoTestSubResult>
                {
                    new() { Id = 1, Outcome = "Passed" },
                    new() { Id = 2, Outcome = "Failed" },
                }
            });

        var result = await _svc.GetTrueTestCountAsync("42");

        Assert.Equal(5, result.ReportedCount);          // 2 + 3
        Assert.Equal(9, result.TrueCount);              // Run1: 1+4=5, Run2: 2+2=4
        Assert.Equal(2, result.TheoryParentCount);      // 1 per run
        Assert.Equal(6, result.TheorySubResultTotal);   // 4 + 2
        Assert.Equal(0, result.FailedToExpand);
        Assert.Equal(2, result.Runs.Count);

        // Verify per-run breakdown
        var run1 = result.Runs.Single(r => r.RunId == 1);
        Assert.Equal(2, run1.Reported);
        Assert.Equal(5, run1.TrueCount);               // 1 simple + 4 sub

        var run2 = result.Runs.Single(r => r.RunId == 2);
        Assert.Equal(3, run2.Reported);
        Assert.Equal(4, run2.TrueCount);               // 2 simple + 2 sub
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void SetupRuns(int buildId, IReadOnlyList<AzdoTestRun> runs)
    {
        _mockApi.GetTestRunsAsync(Org, Project, buildId, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(runs);
    }

    private void SetupResults(int runId, IReadOnlyList<AzdoTestResult> results)
    {
        _mockApi.GetTestResultsAllOutcomesAsync(Org, Project, runId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(results);
    }
}
