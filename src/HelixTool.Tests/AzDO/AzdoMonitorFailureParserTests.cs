using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoMonitorFailureParserTests
{
    private const string MonitorJobId = "078edc60-2a90-5618-e72d-426a045b11f0";
    private const string MonitorTaskId = "0ca3371c-1de8-59d8-daef-35ea5811e911";
    private const string RuntimeHelixJobId = "d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a";
    private const string SvcHelixJobId = "344b1d8c-5e14-49b2-9981-2e2891ac0132";

    [Fact]
    public void ParseMessage_LegacyFormat_ProducesLegacyFailure()
    {
        const string guid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var failures = AzdoMonitorFailureParser.ParseMessage(
            $"Work item Legacy.dll in job {guid} has failed");

        var failure = Assert.Single(failures);
        Assert.Equal("legacy", failure.SourceFormat);
        Assert.Equal("Legacy.dll", failure.WorkItem);
        Assert.Equal(guid, failure.HelixJobId);
        Assert.Null(failure.State);
        Assert.Null(failure.ExitCode);
    }

    [Fact]
    public void ParseMessage_ArcadeWarningWithDirectGuid_ParsesCoordinatesAndDetails()
    {
        var failures = AzdoMonitorFailureParser.ParseMessage(
            LiveMonitorWarning(RuntimeHelixJobId, queue: "windows.10.amd64.open.rt"));

        var failure = Assert.Single(failures);
        Assert.Equal("monitor-warning", failure.SourceFormat);
        Assert.Equal(RuntimeHelixJobId, failure.HelixJobId);
        Assert.Equal("System.Diagnostics.Process.Tests", failure.WorkItem);
        Assert.Equal(
            "windows-x86 Debug Libraries_CheckedCoreCLR - windows.10.amd64.open.rt",
            failure.HelixJobName);
        Assert.Equal("windows-x86 Debug Libraries_CheckedCoreCLR", failure.Leg);
        Assert.Equal("windows.10.amd64.open.rt", failure.Queue);
        Assert.Equal("Finished", failure.State);
        Assert.Equal(-3, failure.ExitCode);
        Assert.Equal("Finished, exit code -3", failure.Details);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("BadExit")]
    public void ParseMessage_ArcadeWarningWithStateOnly_LeavesExitCodeNull(string state)
    {
        var failures = AzdoMonitorFailureParser.ParseMessage(
            $"Work item 'StateOnly.Tests' in job 'linux-x64 Release Libraries - ubuntu.2204.amd64.open ({RuntimeHelixJobId})' failed ({state}).");

        var failure = Assert.Single(failures);
        Assert.Equal(state, failure.State);
        Assert.Null(failure.ExitCode);
        Assert.Equal(state, failure.Details);
    }

    [Fact]
    public void ScanTimeline_ConsoleUrlFallback_RecoversExactlyOneGuid()
    {
        var timeline = TimelineWithMonitorIssue(
            """
            Work item 'Fallback.Tests' in job 'linux-x64 Release Libraries - ubuntu.2204.amd64.open' failed (BadExit).
              Console: https://helix.dot.net/api/2019-06-17/jobs/d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a/workitems/Fallback.Tests/console
            """);

        var scan = AzdoMonitorFailureParser.ScanTimeline(timeline, [MonitorJobId]);

        var job = Assert.Single(scan.MonitorJobs);
        var failure = Assert.Single(job.Failures);
        Assert.Equal(RuntimeHelixJobId, failure.HelixJobId);
        Assert.Equal("Fallback.Tests", failure.WorkItem);
        Assert.Equal("BadExit", failure.State);
        Assert.Equal(0, job.UnresolvedFailureEntryCount);
    }

    [Fact]
    public void ScanTimeline_ConsoleUrlFallback_DoesNotBorrowSiblingConsoleUrl()
    {
        var timeline = TimelineWithMonitorIssue(
            """
            Work item 'MissingGuid.Tests' in job 'linux-x64 Release Libraries - ubuntu.2204.amd64.open' failed (Failed).
            Work item 'HasGuid.Tests' in job 'windows-x64 Release Libraries - windows.10.amd64.open' failed (Failed).
              Console: https://helix.dot.net/api/2019-06-17/jobs/344b1d8c-5e14-49b2-9981-2e2891ac0132/workitems/HasGuid.Tests/console
            """);

        var scan = AzdoMonitorFailureParser.ScanTimeline(timeline, [MonitorJobId]);

        var job = Assert.Single(scan.MonitorJobs);
        var failure = Assert.Single(job.Failures);
        Assert.Equal("HasGuid.Tests", failure.WorkItem);
        Assert.Equal(SvcHelixJobId, failure.HelixJobId);
        Assert.Equal(1, job.UnresolvedFailureEntryCount);
    }

    [Fact]
    public void ScanTimeline_TreeLines_ParseMultipleRowsAndIgnoreConsoleChildren()
    {
        var timeline = TimelineWithMonitorIssue(
            $"""
            Failed work item information:
            ├─ System.Diagnostics.Process.Tests (Job: windows-x86 Debug Libraries_CheckedCoreCLR - windows.10.amd64.open.rt ({RuntimeHelixJobId})) (Finished, exit code -3)
            │  └─ Console: https://helix.dot.net/api/2019-06-17/jobs/{RuntimeHelixJobId}/workitems/System.Diagnostics.Process.Tests/console
            └─ System.Net.Http.Tests (Job: linux-x64 Release Libraries - ubuntu.2204.amd64.open ({SvcHelixJobId})) (Failed)
               └─ Console: https://helix.dot.net/api/2019-06-17/jobs/{SvcHelixJobId}/workitems/System.Net.Http.Tests/console
            """);

        var scan = AzdoMonitorFailureParser.ScanTimeline(timeline, [MonitorJobId]);

        var job = Assert.Single(scan.MonitorJobs);
        Assert.Equal(2, job.Failures.Count);
        Assert.Equal(["System.Diagnostics.Process.Tests", "System.Net.Http.Tests"], job.Failures.Select(f => f.WorkItem));
        Assert.All(job.Failures, f => Assert.Equal("monitor-tree", f.SourceFormat));
        Assert.Equal(-3, job.Failures[0].ExitCode);
        Assert.Equal("Failed", job.Failures[1].State);
        Assert.DoesNotContain(job.Failures, f => f.WorkItem.Contains("Console:", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanTimeline_AmbiguousFallback_IncrementsUnresolvedAndSkipsRow()
    {
        var timeline = TimelineWithMonitorIssue(
            $"""
            Work item 'Ambiguous.Tests' in job 'linux-x64 Release Libraries - ubuntu.2204.amd64.open' failed (Failed).
              Console: https://helix.dot.net/api/2019-06-17/jobs/{RuntimeHelixJobId}/workitems/Ambiguous.Tests/console
              Console: https://helix.dot.net/api/2019-06-17/jobs/{SvcHelixJobId}/workitems/Ambiguous.Tests/console
            """);

        var scan = AzdoMonitorFailureParser.ScanTimeline(timeline, [MonitorJobId]);

        var job = Assert.Single(scan.MonitorJobs);
        Assert.Empty(job.Failures);
        Assert.Equal(1, job.UnresolvedFailureEntryCount);
        Assert.True(job.IsMonitorLike);
    }

    [Fact]
    public void ScanTimeline_WarningAndTreeDuplicate_DedupesByMonitorJobHelixJobAndWorkItem()
    {
        var timeline = TimelineWithMonitorIssue(
            $"""
            {LiveMonitorWarning(RuntimeHelixJobId, queue: "windows.10.amd64.open.rt")}
            Failed work item information:
            └─ System.Diagnostics.Process.Tests (Job: windows-x86 Debug Libraries_CheckedCoreCLR - windows.10.amd64.open.rt ({RuntimeHelixJobId})) (Finished, exit code -3)
               └─ Console: https://helix.dot.net/api/2019-06-17/jobs/{RuntimeHelixJobId}/workitems/System.Diagnostics.Process.Tests/console
            """);

        var scan = AzdoMonitorFailureParser.ScanTimeline(timeline, [MonitorJobId]);

        var job = Assert.Single(scan.MonitorJobs);
        var failure = Assert.Single(job.Failures);
        Assert.Equal("monitor-warning", failure.SourceFormat);
        Assert.Equal(RuntimeHelixJobId, failure.HelixJobId);
        Assert.Equal("System.Diagnostics.Process.Tests", failure.WorkItem);
    }

    private static string LiveMonitorWarning(string helixJobId, string queue) =>
        $"Work item 'System.Diagnostics.Process.Tests' in job 'windows-x86 Debug Libraries_CheckedCoreCLR - {queue} ({helixJobId})' failed (Finished, exit code -3).";

    private static AzdoTimeline TimelineWithMonitorIssue(string message) => new()
    {
        Records =
        [
            new()
            {
                Id = MonitorJobId,
                Type = "Job",
                Name = "Monitor Helix Jobs",
                State = "completed",
                Result = "failed",
                Order = 1,
                Attempt = 1
            },
            new()
            {
                Id = MonitorTaskId,
                ParentId = MonitorJobId,
                Type = "Task",
                Name = "Monitor Helix Jobs",
                State = "completed",
                Result = "failed",
                Order = 4,
                Attempt = 1,
                Issues = [new() { Type = "warning", Category = "General", Message = message }]
            }
        ]
    };
}
