using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineFourthReviewRegressionTests
{
    [Theory]
    [InlineData("auto", 3)]
    [InlineData("auto", 5)]
    [InlineData("inline", 3)]
    [InlineData("inline", 5)]
    [InlineData("file", 3)]
    [InlineData("file", 5)]
    [InlineData("chunked", 3)]
    [InlineData("chunked", 5)]
    public async Task FourthReview1_ExactAndOverCoveringIssueLimits_ExitZeroWhenAllActualIssuesArePresent(string delivery, int issueLimit)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.FullIssueLookup());
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.SyntheticId(5),
            "--projection", "full", "--expand", "none", "--issue-limit", issueLimit.ToString(), "--delivery", delivery, "--json");
        using var contents = await ReadVerifiedBackingAsync(result.Stdout);
        var data = contents.RootElement;
        Assert.True(data.GetProperty("complete").GetBoolean());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("issueComplete").GetBoolean());
        var rows = data.TryGetProperty("results", out var results) ? results : data.GetProperty("records");
        Assert.Equal(3, rows[0].GetProperty("issues").GetArrayLength());
        Assert.Equal(3, data.GetProperty("issueTotal").GetInt32());
        Assert.Equal(3, data.GetProperty("issueReturned").GetInt32());
        Assert.Equal(1, data.GetProperty("selectedTotal").GetInt32());
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("inline")]
    public async Task FourthReview2_ExactRecordWithDefaultAncestorExpansion_LimitOneOfTwoRemainsExitTwo(string delivery)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.FullIssueLookup());
        // Omit --expand deliberately: an exact seed is not the complete expanded selection.
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.SyntheticId(5),
            "--projection", "full", "--limit", "1", "--delivery", delivery, "--json");
        using var contents = await ReadVerifiedBackingAsync(result.Stdout);
        var data = contents.RootElement;
        Assert.Equal(2, data.GetProperty("selectedTotal").GetInt32());
        Assert.Equal(1, data.GetProperty("returned").GetInt32());
        var rows = data.TryGetProperty("results", out var results) ? results : data.GetProperty("records");
        Assert.Equal(3, rows[0].GetProperty("issues").GetArrayLength());
        Assert.False(data.GetProperty("complete").GetBoolean());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, result.ExitCode);
    }

    public static IEnumerable<object[]> CompletenessMatrix()
    {
        foreach (var delivery in new[] { "auto", "inline", "file", "chunked" })
        foreach (var graphComplete in new[] { true, false })
        foreach (var issueComplete in new[] { true, false })
            yield return [delivery, graphComplete, issueComplete];
    }

    [Theory]
    [MemberData(nameof(CompletenessMatrix))]
    public async Task FourthReview_LogicalCompletenessMatrix_NotDeliveryModeOrInputFlagPresence_DeterminesExit(
        string delivery, bool graphComplete, bool issueComplete)
    {
        using var fixture = new TimelineViewReplayTests();
        var source = TimelineViewFixture.FullIssueLookup(includeAncestor: false);
        if (graphComplete)
            source = source with { Records = [source.Records[0] with { ParentId = null }] };
        await fixture.SeedAsync(source);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.SyntheticId(5),
            "--projection", "full", "--expand", "ancestors", "--issue-limit", issueComplete ? "5" : "1",
            "--limit", "2000", "--delivery", delivery, "--json");
        using var contents = await ReadVerifiedBackingAsync(result.Stdout);
        var data = contents.RootElement;
        var expectedComplete = graphComplete && issueComplete;
        Assert.Equal(issueComplete, data.GetProperty("issueComplete").GetBoolean());
        Assert.Equal(issueComplete ? 3 : 1, data.GetProperty("issueReturned").GetInt32());
        Assert.Equal(expectedComplete, data.GetProperty("complete").GetBoolean());
        Assert.Equal(!expectedComplete, data.GetProperty("truncated").GetBoolean());
        if (!graphComplete)
            Assert.Contains(data.GetProperty("incompleteDetails").EnumerateArray(),
                d => d.GetProperty("code").GetString() == "timeline_graph_invalid");
        Assert.Equal(expectedComplete ? 0 : 2, result.ExitCode);
    }

    private static async Task<JsonDocument> ReadVerifiedBackingAsync(string stdout)
    {
        using var receipt = JsonDocument.Parse(stdout);
        var descriptor = receipt.RootElement.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        var bytes = await File.ReadAllBytesAsync(descriptor.GetProperty("localPath").GetString()!);
        Assert.Equal(bytes.LongLength, descriptor.GetProperty("bytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptor.GetProperty("sha256").GetString());
        return JsonDocument.Parse(bytes);
    }
}
