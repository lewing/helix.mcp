using System.Text.Json;
using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineIssueContinuationRegressionTests
{
    [Theory]
    [InlineData(0, 4, true)]
    [InlineData(1, 3, true)]
    [InlineData(1, 2, false)]
    public async Task IssueContinuation_ReplaysExactRowWindowAndUnchangedAncestorContext(
        int offset, int limit, bool includePhase)
    {
        var source = TimelineViewFixture.Real(1621192);
        foreach (var id in new[] { TimelineViewFixture.MonitorStage, TimelineViewFixture.MonitorPhase, TimelineViewFixture.MonitorJob })
            source = TimelineViewFixture.WithIssues(source, id,
                new AzdoIssue { Type = "warning", Message = $"context-{id}-one" },
                new AzdoIssue { Type = "warning", Message = $"context-{id}-two" });
        var rowOrder = new[] { TimelineViewFixture.MonitorStage, TimelineViewFixture.MonitorPhase,
            TimelineViewFixture.MonitorJob, TimelineViewFixture.MonitorTask };
        source = source with
        {
            Records = source.Records.OrderBy(row =>
            {
                var index = Array.IndexOf(rowOrder, row.Id);
                return index < 0 ? int.MaxValue : index;
            }).ToArray()
        };
        await using var host = await TimelineViewHost.StartAsync(source);
        var original = await host.TimelineAsync(new()
        {
            ["recordId"] = TimelineViewFixture.MonitorTask, ["projection"] = "full",
            ["expand"] = "ancestors", ["includePhase"] = includePhase,
            ["offset"] = offset, ["limit"] = limit, ["issueOffset"] = 0, ["issueLimit"] = 1,
            ["delivery"] = "inline", ["maxResponseBytes"] = 65536
        });
        var originalRows = original.GetProperty("records");
        Assert.Contains(originalRows.EnumerateArray(), row => row.GetProperty("id").GetString() == TimelineViewFixture.MonitorTask);
        Assert.Contains(originalRows.EnumerateArray(), row => row.GetProperty("id").GetString() != TimelineViewFixture.MonitorTask);

        var page = original;
        for (var issueOffset = 1; issueOffset < 3; issueOffset++)
        {
            var action = page.GetProperty("continuation");
            Assert.Equal("azdo_timeline", action.GetProperty("tool").GetString());
            var args = action.GetProperty("arguments");
            Assert.Equal(offset, args.GetProperty("offset").GetInt32());
            Assert.Equal(limit, args.GetProperty("limit").GetInt32());
            Assert.Equal(issueOffset, args.GetProperty("issueOffset").GetInt32());
            Assert.Equal(1, args.GetProperty("issueLimit").GetInt32());
            Assert.Equal(TimelineViewFixture.MonitorTask, args.GetProperty("recordId").GetString());
            Assert.Equal("full", args.GetProperty("projection").GetString());
            Assert.Equal(includePhase, args.GetProperty("includePhase").GetBoolean());
            Assert.Equal(original.GetProperty("viewId").GetString(), args.GetProperty("viewId").GetString());

            page = await host.ExecuteAsync(action);
            Assert.Equal(offset, page.GetProperty("offset").GetInt32());
            Assert.Equal(issueOffset, page.GetProperty("issueOffset").GetInt32());
            Assert.Equal(1, page.GetProperty("issueReturned").GetInt32());
            var replayRows = page.GetProperty("records");
            Assert.Equal(originalRows.EnumerateArray().Select(Id), replayRows.EnumerateArray().Select(Id));
            foreach (var context in originalRows.EnumerateArray().Where(row => Id(row) != TimelineViewFixture.MonitorTask))
                Assert.True(JsonElement.DeepEquals(context, replayRows.EnumerateArray().Single(row => Id(row) == Id(context))));
            var target = replayRows.EnumerateArray().Single(row => Id(row) == TimelineViewFixture.MonitorTask);
            var expectedIssue = source.Records.Single(row => row.Id == TimelineViewFixture.MonitorTask).Issues![issueOffset];
            Assert.Equal(expectedIssue.Message, Assert.Single(target.GetProperty("issues").EnumerateArray()).GetProperty("message").GetString());
            Assert.False(page.GetProperty("complete").GetBoolean());
        }
    }

    private static string? Id(JsonElement row) => row.GetProperty("id").GetString();
}
