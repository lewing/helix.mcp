using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineProjectorContractTests
{
    [Theory]
    [InlineData("compact")]
    [InlineData("full")]
    public void CompactAndFull_PreserveProviderOrdinal_NotGuidOrder(string projection)
    {
        var source = TimelineViewFixture.Real(1600801);
        var view = AzdoTimelineProjector.Project(source, new()
        {
            Filter = "all", Projection = projection, Expand = "none", Limit = 100
        });
        var ids = projection == "full" ? view.FullRecords!.Select(r => r.Id) : view.CompactRows!.Select(r => r.Id);
        Assert.Equal(source.Records.Select(r => r.Id), ids);
    }

    [Fact]
    public void ViewId_IsFullSha256_StableAcrossWindowsAndBudgets_ChangesWithSourceAndSemanticRequest()
    {
        var source = TimelineViewFixture.Real(1621192);
        var request = new TimelineProjectionRequest { Filter = "all", Projection = "compact" };
        var first = AzdoTimelineProjector.Project(source, request);
        Assert.Equal(64, first.ViewId.Length);
        var paged = AzdoTimelineProjector.Project(source, request with { Offset = 1, Limit = 1, MaxResponseBytes = 4096 });
        Assert.Equal(first.ViewId, paged.ViewId);
        var changed = source with
        {
            Records = source.Records.Select(r => r with { Attempt = (r.Attempt ?? 0) + 1 }).ToArray()
        };
        Assert.NotEqual(first.ViewId, AzdoTimelineProjector.Project(changed, request).ViewId);
        Assert.NotEqual(first.ViewId, AzdoTimelineProjector.Project(source, request with { IncludePhase = false }).ViewId);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("parent")]
    public void InvalidGuidSelectors_AreValidated_NotTreatedAsEmptySuccessfulMatches(string field)
    {
        var request = new TimelineProjectionRequest
        {
            RecordId = field == "record" ? "invalid-guid" : null,
            ParentId = field == "parent" ? "invalid-guid" : null
        };
        Assert.ThrowsAny<ArgumentException>(() => AzdoTimelineProjector.Project(TimelineViewFixture.Real(1621192), request));
    }

    [Fact]
    public void FinalRecordAndIssuePages_AreIncompleteEvenWhenNoLaterRowsExist()
    {
        var source = TimelineViewFixture.Real(1621192);
        var recordPage = AzdoTimelineProjector.Project(source, new()
        {
            Filter = "all", Projection = "compact", Offset = 3, Limit = 1
        });
        Assert.False(recordPage.Complete);
        Assert.True(recordPage.Truncated);
        Assert.Null(recordPage.Next);
        var issuePage = AzdoTimelineProjector.Project(source, new()
        {
            RecordId = TimelineViewFixture.MonitorTask, Projection = "full",
            Expand = "none", IssueOffset = 2, IssueLimit = 1
        });
        Assert.False(issuePage.Complete);
        Assert.True(issuePage.Truncated);
        Assert.NotNull(issuePage.IssueWindow);
        Assert.False(issuePage.IssueWindow.IssueComplete);
        Assert.True(issuePage.IssueWindow.IssueTruncated);
        Assert.Null(issuePage.IssueWindow.IssueNext);
    }
}
