using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using Xunit;

namespace HelixTool.Tests.Collect;

public sealed partial class IndependentReviewRegressionTests
{
    [Theory]
    [InlineData(9999, null, true)]
    [InlineData(10000, null, true)]
    [InlineData(10001, null, false)]
    [InlineData(10001, 10000L, false)]
    [InlineData(10001, 10001L, true)]
    [InlineData(1405433, 2000000L, true)]
    public async Task AllResultGuard_EnforcesEstimateBoundaryAndExplicitConsent(
        int estimate, long? budget, bool allowed)
    {
        using var fixture = new Fixture();
        fixture.Handler.TestRuns = [new() { Id = RunId, State = "completed", TotalTests = estimate }];

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            TestScope = "all",
            MaxTestResults = budget
        });

        Assert.Equal(allowed, result.Manifest.Complete);
        Assert.Equal(allowed ? 0 : 2, result.Manifest.ExitCode);
        Assert.Equal(allowed ? 1 : 0, ResultRequests(fixture).Count);
        Assert.Equal(budget ?? CollectPolicy.DefaultMaxTestResults, result.Manifest.Policy.Caps.MaxTestResults);
        Assert.Equal(budget.HasValue, result.Manifest.Policy.MaxTestResultsExplicit);
        if (!allowed)
        {
            var skip = Assert.Single(result.Manifest.Attempts, a => a.Skip?.Kind == "test_result_limit");
            Assert.Equal("skipped", skip.Outcome);
            Assert.True(skip.Required);
            Assert.Equal((long)estimate, skip.Resource["requestedCount"]);
            Assert.Equal(budget ?? CollectPolicy.DefaultMaxTestResults, skip.Resource["maxTestResults"]);
            Assert.Contains($"--max-test-results {estimate}", skip.Skip!.Message, StringComparison.Ordinal);
            Assert.Contains(result.Manifest.IncompleteDetails, d => d.Code == "test_result_limit" &&
                Convert.ToInt64(d.Resource!["requestedCount"]) == estimate);
            Assert.Empty(AttachmentRequests(fixture));
        }
    }

    [Fact]
    public async Task AllResultGuard_SumsAcrossRunsWithoutIntOverflow()
    {
        using var fixture = new Fixture();
        fixture.Handler.TestRuns =
        [
            new() { Id = RunId, TotalTests = int.MaxValue },
            new() { Id = RunId + 1, TotalTests = int.MaxValue }
        ];

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with { TestScope = "all" });

        var skip = Assert.Single(result.Manifest.Attempts, a => a.Skip?.Kind == "test_result_limit");
        Assert.Equal(2L * int.MaxValue, skip.Resource["requestedCount"]);
        Assert.Empty(ResultRequests(fixture));
        Assert.Equal(2, result.Manifest.ExitCode);
    }

    [Fact]
    public async Task InvalidNegativeRunTotals_FailClosedInsteadOfReducingVolumeEstimate()
    {
        using var fixture = new Fixture();
        fixture.Handler.TestRuns = [new() { Id = RunId, TotalTests = -1 }];

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with { TestScope = "all" });

        Assert.False(result.Manifest.Complete);
        Assert.Equal(2, result.Manifest.ExitCode);
        var runAttempt = Assert.Single(result.Manifest.Attempts, a => a.Operation == "list_test_runs");
        Assert.Equal("failed", runAttempt.Outcome);
        Assert.Equal(AcquisitionErrorKind.InvalidResponse, runAttempt.Error?.Kind);
        Assert.Empty(ResultRequests(fixture));
        Assert.Empty(AttachmentRequests(fixture));
    }

    [Fact]
    public async Task ResultBudget_DoesNotChangeDefaultFailedOnlyCollection()
    {
        using var fixture = new Fixture();
        fixture.Handler.TestRuns = [new() { Id = RunId, TotalTests = 2_000_000 }];

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with { MaxTestResults = 1 });

        Assert.True(result.Manifest.Complete);
        Assert.Single(ResultRequests(fixture));
        Assert.Equal("Failed", QueryValue(ResultRequests(fixture)[0], "outcomes"));
        Assert.Single(AttachmentRequests(fixture));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public async Task ResultGuard_AllowIncompleteKeepsGapAndJsonStdoutClean(bool allowIncomplete, int expectedExit)
    {
        using var fixture = new Fixture();
        fixture.Handler.TestRuns = [new() { Id = RunId, TotalTests = 10_001 }];

        var manifest = await fixture.InvokeCliAsync(fixture.Options.CacheRoot, null,
            testScope: "all", allowIncomplete: allowIncomplete, expectedExit: expectedExit);

        Assert.False(manifest.Complete);
        Assert.Equal(expectedExit, manifest.ExitCode);
        Assert.Contains(manifest.IncompleteDetails, d => d.Code == "test_result_limit");
        using var json = JsonDocument.Parse(fixture.LastStdout);
        Assert.False(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.Contains("10001 estimated result(s)", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Contains("All-result collection skipped", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Contains("--max-test-results 10001", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Empty(ResultRequests(fixture));
    }

    [Fact]
    public async Task ResultGuard_ResumeCannotBypassRefusalAndRaisedBudgetRehydratesCache()
    {
        using var fixture = new Fixture();
        fixture.Handler.TestRuns = [new() { Id = RunId, TotalTests = 10_001 }];
        var policy = fixture.Policy with { TestScope = "all" };
        var refused = await fixture.Collector.CollectAzdoBuildAsync(policy);
        Assert.False(refused.Manifest.Complete);
        Assert.Empty(ResultRequests(fixture));

        var raised = await fixture.Collector.CollectAzdoBuildAsync(policy with { Resume = true, MaxTestResults = 10_001 });
        Assert.True(raised.Manifest.Complete);
        Assert.Single(ResultRequests(fixture));
        var replayed = await fixture.Collector.CollectAzdoBuildAsync(policy with { Resume = true, MaxTestResults = 10_001 });
        Assert.True(replayed.Manifest.Complete);
        Assert.Single(ResultRequests(fixture));
        Assert.Contains(replayed.Manifest.Attempts, a => a.Operation == "list_test_results" && a.Outcome == "cached");

        var reduced = await fixture.Collector.CollectAzdoBuildAsync(policy with { Resume = true });
        Assert.False(reduced.Manifest.Complete);
        Assert.Equal(2, reduced.Manifest.ExitCode);
        Assert.Single(ResultRequests(fixture));
        Assert.Contains(reduced.Manifest.Attempts, a => a.Skip?.Kind == "test_result_limit");
    }

    [Fact]
    public async Task Resume_OlderManifestWithoutVolumePolicyFieldsRemainsReadable()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(initial.ManifestPath))!.AsObject();
        var policy = document["policy"]!.AsObject();
        policy.Remove("maxTestResultsExplicit");
        policy.Remove("testAttachmentScope");
        var caps = policy["caps"]!.AsObject();
        caps.Remove("maxTestResults");
        caps.Remove("maxTestAttachments");
        await File.WriteAllTextAsync(initial.ManifestPath, document.ToJsonString());
        fixture.Handler.Requests.Clear();

        var resumed = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with { Resume = true });

        Assert.True(resumed.Manifest.Complete);
        Assert.Empty(ResultRequests(fixture));
        Assert.Empty(AttachmentRequests(fixture));
        Assert.Contains(resumed.Manifest.Attempts, a => a.Operation == "list_test_results" && a.Outcome == "cached");
    }

    [Fact]
    public async Task DefaultAttachments_SelectOnlyDiagnosticRowsAndPreserveFailedReplay()
    {
        using var fixture = new Fixture();
        var rows = Enumerable.Range(1, 300).Select(id => new AzdoTestResult { Id = id, Outcome = "Passed" })
            .Concat(new[]
            {
                new AzdoTestResult { Id = 501, Outcome = "Failed" },
                new AzdoTestResult { Id = 502, Outcome = "Error" },
                new AzdoTestResult { Id = 503, Outcome = "Timeout" },
                new AzdoTestResult { Id = 504, Outcome = "NotApplicable" }
            }).ToList();
        fixture.Handler.ResultsByRun[RunId] = rows;
        fixture.Handler.TestRuns = [new() { Id = RunId, TotalTests = rows.Count }];

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            TestScope = "all",
            ExportPath = fixture.SnapshotPath
        });

        Assert.True(result.Manifest.Complete);
        Assert.Equal("diagnostic", result.Manifest.Policy.TestAttachmentScope);
        Assert.Equal(3, AttachmentRequests(fixture).Count);
        Assert.Equal(new[] { 501, 502, 503 }, AttachmentRequests(fixture).Select(AttachmentResultId).Order());
        var exclusions = Assert.Single(result.Manifest.Attempts, a => a.Skip?.Kind == "policy_excluded" && a.Operation == "list_test_attachments");
        Assert.Equal(301, exclusions.Resource["requestedCount"]);
        var options = new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, options);
        Assert.Equal(501, Assert.Single(await offline.GetTestResultsAsync(Org, Project, RunId, outcomes: "Failed")).Id);
        Assert.Empty(await offline.GetTestAttachmentsAsync(Org, Project, RunId, 501));
        Assert.True((await SnapshotValidator.ValidateAsync(fixture.SnapshotPath)).IsValid);
    }

    [Fact]
    public async Task BroaderAttachments_RequireExplicitBudgetAndEnforceBuildWideDeterministicLimit()
    {
        using var fixture = new Fixture();
        fixture.Handler.TestRuns =
        [
            new() { Id = RunId + 1, TotalTests = 3 },
            new() { Id = RunId, TotalTests = 3 }
        ];
        foreach (var run in fixture.Handler.TestRuns)
            fixture.Handler.ResultsByRun[run.Id] =
                [new() { Id = 3, Outcome = "Passed" }, new() { Id = 1, Outcome = "Passed" }, new() { Id = 2, Outcome = "Passed" }];
        var policy = fixture.Policy with { TestScope = "all", TestAttachmentScope = "all", MaxConcurrency = 2 };

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Collector.CollectAzdoBuildAsync(policy));
        Assert.Contains("--max-test-attachments", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Handler.Requests);
        var limited = await fixture.Collector.CollectAzdoBuildAsync(policy with { MaxTestAttachments = 4 });
        Assert.False(limited.Manifest.Complete);
        Assert.Equal(2, limited.Manifest.ExitCode);
        Assert.Equal(4, AttachmentRequests(fixture).Count);
        Assert.Equal(new[] { (RunId, 1), (RunId, 2), (RunId, 3), (RunId + 1, 1) },
            AttachmentRequests(fixture).Select(uri => (AttachmentRunId(uri), AttachmentResultId(uri))).Order());
        var skip = Assert.Single(limited.Manifest.Attempts, a => a.Skip?.Kind == "test_attachment_limit");
        Assert.Equal(2, skip.Resource["skippedCount"]);
        Assert.Contains(limited.Manifest.IncompleteDetails, d => d.Code == "test_attachment_limit");
    }

    [Fact]
    public async Task DiagnosticAttachmentLimit_IsDeclaredAndNoneScopeMakesNoRequests()
    {
        using var fixture = new Fixture();
        var limited = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            TestScope = "all",
            MaxTestAttachments = 2
        });
        Assert.False(limited.Manifest.Complete);
        Assert.Equal(2, AttachmentRequests(fixture).Count);
        Assert.Contains(limited.Manifest.IncompleteDetails, d => d.Code == "test_attachment_limit");

        fixture.Handler.Requests.Clear();
        var none = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            TestScope = "all",
            TestAttachmentScope = "none"
        });
        Assert.True(none.Manifest.Complete);
        Assert.Empty(AttachmentRequests(fixture));
        Assert.Contains(none.Manifest.Attempts, a => a.Skip?.Kind == "policy_excluded" &&
            a.Operation == "list_test_attachments" && Convert.ToInt32(a.Resource["requestedCount"]) == 15);
    }

    [Fact]
    public async Task TestProgress_UsesStderrAndLeavesJsonStdoutParseable()
    {
        using var fixture = new Fixture();
        var manifest = await fixture.InvokeCliAsync(fixture.Options.CacheRoot, null, testScope: "all");

        Assert.True(manifest.Complete);
        using var json = JsonDocument.Parse(fixture.LastStdout);
        Assert.True(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.DoesNotContain("Tests: 1 run(s)", fixture.LastStdout, StringComparison.Ordinal);
        Assert.Contains("Tests: 1 run(s), 15 estimated", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Contains("all-result budget accepted", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Contains($"Run {RunId}: acquiring", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Contains($"Run {RunId}: acquired 15", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Contains("Attachments: selected 7, excluded 8", fixture.LastStderr, StringComparison.Ordinal);
        Assert.Contains("Attachments: completed 7/7", fixture.LastStderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongSingleRun_ReportsHeartbeatBeforeResultAcquisitionCompletes()
    {
        using var fixture = new Fixture();
        fixture.Handler.ResultsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();
        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy,
            message =>
            {
                messages.Enqueue(message);
                if (message.Contains("elapsed; acquisition still running", StringComparison.Ordinal))
                    fixture.Handler.ResultsGate.TrySetResult();
            }).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.Manifest.Complete);
        var output = messages.ToList();
        var heartbeatIndex = output.FindIndex(message => message.Contains("elapsed; acquisition still running", StringComparison.Ordinal));
        var completedIndex = output.FindIndex(message => message.StartsWith($"Run {RunId}: acquired", StringComparison.Ordinal));
        Assert.True(heartbeatIndex >= 0 && completedIndex > heartbeatIndex);
    }

    [Fact]
    public async Task TestProgress_CallerCancellationStopsAcquisitionAndHeartbeat()
    {
        using var fixture = new Fixture();
        fixture.Handler.ResultsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var messages = new ConcurrentQueue<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Collector.CollectAzdoBuildAsync(fixture.Policy,
            message =>
            {
                messages.Enqueue(message);
                if (message.StartsWith($"Run {RunId}: acquiring", StringComparison.Ordinal))
                    cts.Cancel();
            }, cts.Token));
        Assert.DoesNotContain(messages, message => message.Contains("acquisition still running", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.StartsWith("Attachments: completed", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedAttachmentProgress_IsThrottledButFinalCountsAreEmitted()
    {
        var messages = new List<string>();
        var clock = new ProgressClock();
        var reporter = new CollectProgressReporter(messages.Add, clock);
        reporter.Report("selected", force: true);
        for (var i = 0; i < 1000; i++)
            reporter.Report($"completed {i}");
        reporter.Report("completed 1000", force: true);

        Assert.Equal(new[] { "selected", "completed 1000" }, messages);
        clock.Timestamp = 1_999;
        reporter.Report("too soon");
        Assert.Equal(2, messages.Count);
        clock.Timestamp = 2_000;
        reporter.Report("next interval");
        Assert.Equal("next interval", messages[^1]);
    }

    private sealed class ProgressClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Timestamp;
    }

    private static List<Uri> ResultRequests(Fixture fixture) =>
        fixture.Handler.Requests.Where(uri => uri.AbsolutePath.EndsWith("/results", StringComparison.Ordinal)).ToList();

    private static List<Uri> AttachmentRequests(Fixture fixture) =>
        fixture.Handler.Requests.Where(uri => uri.AbsolutePath.EndsWith("/attachments", StringComparison.Ordinal)).ToList();

    private static int AttachmentResultId(Uri uri) =>
        int.Parse(uri.AbsolutePath.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);

    private static int AttachmentRunId(Uri uri) =>
        int.Parse(uri.AbsolutePath.Split('/')[^4], System.Globalization.CultureInfo.InvariantCulture);
}
