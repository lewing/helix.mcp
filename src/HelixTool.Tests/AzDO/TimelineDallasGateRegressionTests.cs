using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineDallasGateRegressionTests
{
    [Theory]
    [InlineData(true, "auto", null)]
    [InlineData(true, "auto", 1)]
    [InlineData(false, "file", null)]
    [InlineData(false, "file", 1)]
    [InlineData(false, "chunked", null)]
    [InlineData(false, "chunked", 1)]
    public async Task Dallas3_AllOrExplicitDelivery_ReturnsEntire1524RecordSelection_DespiteOmittedOrSmallLimit(
        bool all, string delivery, int? limit)
    {
        var source = TimelineViewFixture.FullReal(1621192);
        await using var host = await TimelineViewHost.StartAsync(source);
        var args = new Dictionary<string, object?>
        {
            ["filter"] = "all", ["projection"] = "full", ["expand"] = "none",
            ["all"] = all, ["delivery"] = delivery
        };
        if (limit.HasValue) args["limit"] = limit.Value;
        var receipt = await host.TimelineAsync(args);
        Assert.Equal(1524, receipt.GetProperty("selectedTotal").GetInt32());
        Assert.InRange(host.LastResponseBytes, 1, 16384);
        var descriptor = receipt.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        using var data = JsonDocument.Parse(await host.ReadEvidenceAsync(descriptor));
        var complete = data.RootElement;
        Assert.True(complete.GetProperty("complete").GetBoolean());
        Assert.False(complete.GetProperty("truncated").GetBoolean());
        Assert.Equal(1524, complete.GetProperty("returned").GetInt32());
        Assert.Equal(0, complete.GetProperty("offset").GetInt32());
        Assert.Equal(1524, complete.GetProperty("records").GetArrayLength());
        Assert.Equal(source.Records.Select(r => r.Id), TimelineViewContractTests.Ids(complete));
        Assert.Equal(JsonValueKind.Null, complete.GetProperty("next").ValueKind);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("chunked")]
    public async Task Dallas3_CompleteDelivery_PreservesDeliberateIssueFilter(string delivery)
    {
        var source = TimelineViewFixture.FullReal(1621192);
        var expected = source.Records.Where(r => r.Issues?.Count > 0).Select(r => r.Id).ToArray();
        await using var host = await TimelineViewHost.StartAsync(source);
        var receipt = await host.TimelineAsync(new()
        {
            ["filter"] = "issues", ["projection"] = "full", ["expand"] = "none",
            ["delivery"] = delivery, ["limit"] = 1
        });
        using var data = JsonDocument.Parse(await host.ReadEvidenceAsync(receipt.GetProperty("delivery")));
        Assert.True(data.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(expected, TimelineViewContractTests.Ids(data.RootElement));
        Assert.Equal("issues", receipt.GetProperty("effectiveFilter").GetString());
    }

    [Fact]
    public async Task Dallas3_AllFile_DoesNotHideIncompleteExpansionGraph()
    {
        var source = TimelineViewFixture.Real(1621192);
        source = source with { Records = source.Records.Where(r => r.Id != TimelineViewFixture.MonitorPhase).ToArray() };
        await using var host = await TimelineViewHost.StartAsync(source);
        var receipt = await host.TimelineAsync(new()
        {
            ["filter"] = "all", ["projection"] = "full", ["expand"] = "ancestors",
            ["all"] = true, ["delivery"] = "file"
        });
        using var data = JsonDocument.Parse(await host.ReadEvidenceAsync(receipt.GetProperty("delivery")));
        Assert.False(data.RootElement.GetProperty("complete").GetBoolean());
        Assert.True(data.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains(data.RootElement.GetProperty("incompleteDetails").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "timeline_graph_invalid");
    }

    [Theory]
    [InlineData(40, 1, 4096)]
    [InlineData(77, 2, 12288)]
    public async Task Dallas4_NondefaultContinuation_PreservesSemanticShapingAndExecutesItsOwnViewId(
        int previewChars, int previewLimit, long requestedBudget)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.FullReal(1621192));
        var first = await host.TimelineAsync(new()
        {
            ["previewChars"] = previewChars, ["previewIssueLimit"] = previewLimit,
            ["maxResponseBytes"] = requestedBudget, ["limit"] = 1,
            ["includePhase"] = false, ["expand"] = "ancestors", ["delivery"] = "inline"
        });
        var next = first.GetProperty("continuation");
        AssertContinuationSettings(first, next);
        var second = await host.ExecuteAsync(next);
        Assert.Equal(first.GetProperty("viewId").GetString(), second.GetProperty("viewId").GetString());
        Assert.Equal(first.GetProperty("returned").GetInt32(), second.GetProperty("offset").GetInt32());
        AssertContinuationSettings(second, second.GetProperty("continuation"));
    }

    [Fact]
    public async Task Dallas4_AutomaticallyShortenedPreview_ContinuationExecutesTheEffectiveView()
    {
        var issues = Enumerable.Range(0, 10).Select(i => new AzdoIssue
        {
            Type = "error", Message = $"error AX{i}: " + string.Concat(Enumerable.Repeat("\U0001f680", 500))
        }).ToArray();
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.EscapedLongNameWithSecondRow(1),
            TimelineViewFixture.MonitorTask, issues);
        await using var host = await TimelineViewHost.StartAsync(source);
        var first = await host.TimelineAsync(new()
        {
            ["previewChars"] = 2000, ["previewIssueLimit"] = 10,
            ["maxResponseBytes"] = 8192, ["limit"] = 1
        });
        Assert.Equal(1, first.GetProperty("returned").GetInt32());
        Assert.True(first.GetProperty("previewChars").GetInt32() < 2000
            || first.GetProperty("previewIssueLimit").GetInt32() < 10);
        var action = first.GetProperty("continuation");
        AssertContinuationSettings(first, action);
        var next = await host.ExecuteAsync(action);
        Assert.Equal(first.GetProperty("viewId").GetString(), next.GetProperty("viewId").GetString());
    }

    [Fact]
    public async Task Dallas5_OneLongIssueWithoutDedupeOrOmittedGroups_IsMarkedAndRecoverable()
    {
        var message = "warning AX42: " + new string('x', 700);
        var source = TimelineViewFixture.EscapedLongName(1);
        source = source with
        {
            Records = [source.Records[0] with
            {
                Name = "Build DeviceTests", Result = "succeeded",
                Issues = [new() { Type = "warning", Message = message }]
            }]
        };
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync();
        var row = Assert.Single(view.GetProperty("records").EnumerateArray());
        Assert.Equal(1, row.GetProperty("issueTotal").GetInt32());
        Assert.Equal(1, row.GetProperty("issuePreviewTotal").GetInt32());
        Assert.Equal(1, row.GetProperty("issuePreviews")[0].GetProperty("count").GetInt32());
        Assert.True(row.GetProperty("issuePreviews")[0].GetProperty("messageTruncated").GetBoolean());
        Assert.True(row.GetProperty("previewsTruncated").GetBoolean());
        var action = row.GetProperty("fullAction");
        Assert.Equal(TimelineViewFixture.MonitorTask, action.GetProperty("arguments").GetProperty("recordId").GetString());
        var recovered = await host.ExecuteAsync(action);
        var full = await ReadDeliveredOrInlineAsync(host, recovered);
        Assert.Equal(message, full.GetProperty("records")[0].GetProperty("issues")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Dallas6_FullRealSelection_AndFirstPage_RankFailedMonitorJobBeforeSuccessfulWarningTasks()
    {
        var source = TimelineViewFixture.FullReal(1621192);
        var projected = AzdoTimelineProjector.Project(source, new() { Limit = 2000 });
        var rows = projected.TriageRows!;
        var monitorJobIndex = rows.ToList().FindIndex(r => r.Id == TimelineViewFixture.MonitorJob);
        Assert.True(monitorJobIndex >= 0);
        Assert.Equal(TimelineViewFixture.MonitorTask, rows[0].Id);
        Assert.All(rows.Where(r => r.Result == "succeeded" && r.IssueTotal > 0),
            warning => Assert.True(rows.ToList().IndexOf(warning) > monitorJobIndex));
        await using var host = await TimelineViewHost.StartAsync(source);
        var first = await host.TimelineAsync();
        Assert.Equal(TimelineViewFixture.MonitorTask, first.GetProperty("records")[0].GetProperty("id").GetString());
        Assert.Contains(TimelineViewFixture.MonitorJob, TimelineViewContractTests.Ids(first));
        Assert.InRange(host.LastResponseBytes, 1, 16384);
    }

    [Fact]
    public void Dallas6_FailedMonitorNameHint_OutranksSucceededWarningTask()
    {
        var source = TimelineViewFixture.EscapedLongNameWithSecondRow(1);
        source = source with
        {
            Records = [source.Records[1] with { Result = "succeeded", Issues = [new() { Type = "warning", Message = "successful warning" }] },
                source.Records[0] with { Name = "Monitor Helix Jobs", Type = "Job", Issues = [] }]
        };
        var projected = AzdoTimelineProjector.Project(source, new() { Limit = 100 });
        Assert.Equal(TimelineViewFixture.MonitorTask, projected.TriageRows![0].Id);
        Assert.Equal("nameHint", projected.TriageRows[0].MonitorEvidence);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Dallas7_UnboundedNonzeroIssueOffset_IsIncompleteAndWholeRecordRecoveryExecutes(int offset)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var view = await host.TimelineAsync(new()
        {
            ["recordId"] = TimelineViewFixture.MonitorTask, ["projection"] = "full",
            ["expand"] = "none", ["issueOffset"] = offset
        });
        Assert.False(view.GetProperty("complete").GetBoolean());
        Assert.True(view.GetProperty("truncated").GetBoolean());
        Assert.False(view.GetProperty("issueComplete").GetBoolean());
        Assert.True(view.GetProperty("issueTruncated").GetBoolean());
        Assert.Equal(3, view.GetProperty("issueTotal").GetInt32());
        Assert.Equal(offset == 2 ? 1 : 0, view.GetProperty("issueReturned").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("issueNext").ValueKind);
        var recovered = await host.ExecuteAsync(view.GetProperty("continuation"));
        var full = await ReadDeliveredOrInlineAsync(host, recovered);
        Assert.Equal(3, full.GetProperty("records")[0].GetProperty("issues").GetArrayLength());
    }

    [Fact]
    public async Task Dallas7_UnboundedOffsetZero_RemainsCompleteWithAllThreeIssues()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var view = await host.TimelineAsync(new()
        {
            ["recordId"] = TimelineViewFixture.MonitorTask, ["projection"] = "full", ["expand"] = "none"
        });
        Assert.True(view.GetProperty("complete").GetBoolean());
        Assert.False(view.GetProperty("truncated").GetBoolean());
        Assert.True(view.GetProperty("issueComplete").GetBoolean());
        Assert.Equal(3, view.GetProperty("records")[0].GetProperty("issues").GetArrayLength());
    }

    [Theory]
    [InlineData(1, 8192)]
    [InlineData(4096, 8192)]
    [InlineData(8191, 8192)]
    [InlineData(8192, 8192)]
    [InlineData(12288, 12288)]
    [InlineData(16384, 16384)]
    [InlineData(16385, 16384)]
    public async Task Dallas1_ReaderTargets_ReportRequestedAndEffective_AndBoundFinalFramedResponse(long requested, long effective)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var descriptor = host.PutEvidence(Encoding.UTF8.GetBytes(new string('x', 10000)), "text/plain");
        var read = TimelineViewHost.Payload(await host.CallAsync("hlx_read_evidence", new()
        {
            ["evidenceId"] = descriptor.GetProperty("evidenceId").GetString(),
            ["maxResponseBytes"] = requested, ["lengthBytes"] = 2048
        }));
        Assert.Equal(requested, read.GetProperty("requestedMaxResponseBytes").GetInt64());
        Assert.Equal(effective, read.GetProperty("maxResponseBytes").GetInt64());
        Assert.InRange(host.LastResponseBytes, 1, effective);
        var delivery = read.GetProperty("delivery");
        foreach (var property in new[] { "sourceFingerprint", "backingCacheKey", "capturedAt", "freshness", "sha256", "resourceUri" })
            Assert.True(delivery.TryGetProperty(property, out _), $"Missing provenance {property}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Dallas1_NonpositiveTargets_AreErrorsForTimelineAndReader(long target)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var timeline = await host.CallAsync("azdo_timeline", new() { ["buildIdOrUrl"] = "1621192", ["maxResponseBytes"] = target });
        Assert.True(timeline.IsError);
        var descriptor = host.PutEvidence([1, 2, 3], "application/octet-stream");
        var read = await host.CallAsync("hlx_read_evidence", new()
        {
            ["evidenceId"] = descriptor.GetProperty("evidenceId").GetString(), ["maxResponseBytes"] = target
        });
        Assert.True(read.IsError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Dallas8_NonpositiveReaderLengths_AreRealErrors_NotSilentlyPromoted(long length)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var descriptor = host.PutEvidence([1, 2, 3], "application/octet-stream");
        var result = await host.CallAsync("hlx_read_evidence", new()
        {
            ["evidenceId"] = descriptor.GetProperty("evidenceId").GetString(), ["lengthBytes"] = length
        });
        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData(long.MaxValue - 3)]
    [InlineData(long.MaxValue)]
    public async Task Dallas8_HugePositiveReaderLength_AdvancesOrErrors_NeverRepeatsZeroByteOffset(long length)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var descriptor = host.PutEvidence(Encoding.UTF8.GetBytes(new string('x', 10000)), "text/plain");
        var result = await host.CallAsync("hlx_read_evidence", new()
        {
            ["evidenceId"] = descriptor.GetProperty("evidenceId").GetString(),
            ["lengthBytes"] = length, ["encoding"] = "utf8"
        });
        if (result.IsError is true)
        {
            Assert.NotEmpty(result.Content);
            return;
        }
        var read = TimelineViewHost.Payload(result);
        Assert.InRange(read.GetProperty("returnedBytes").GetInt64(), 1, 10000);
        if (read.GetProperty("next").ValueKind == JsonValueKind.Object)
        {
            var next = read.GetProperty("next");
            Assert.Equal(read.GetProperty("returnedBytes").GetInt64(), next.GetProperty("arguments").GetProperty("offsetBytes").GetInt64());
            var advanced = await host.ExecuteAsync(next);
            Assert.InRange(advanced.GetProperty("returnedBytes").GetInt64(), 1, 10000);
            Assert.True(advanced.GetProperty("offsetBytes").GetInt64() > 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dallas8_ZeroIssueLimit_IsRejectedEvenForEmptyTimeline(bool empty)
    {
        var source = TimelineViewFixture.Real(1621192);
        if (empty) source = source with { Records = [] };
        await using var host = await TimelineViewHost.StartAsync(source);
        var result = await host.CallAsync("azdo_timeline", new()
        {
            ["buildIdOrUrl"] = "1621192", ["recordId"] = TimelineViewFixture.MonitorTask,
            ["projection"] = "full", ["expand"] = "none", ["issueLimit"] = 0
        });
        Assert.True(result.IsError);
    }

    internal static void AssertContinuationSettings(JsonElement page, JsonElement action)
    {
        var args = action.GetProperty("arguments");
        Assert.Equal(page.GetProperty("previewChars").GetInt32(), args.GetProperty("previewChars").GetInt32());
        Assert.Equal(page.GetProperty("previewIssueLimit").GetInt32(), args.GetProperty("previewIssueLimit").GetInt32());
        Assert.Equal(page.GetProperty("maxResponseBytes").GetInt64(), args.GetProperty("maxResponseBytes").GetInt64());
        Assert.Equal(page.GetProperty("viewId").GetString(), args.GetProperty("viewId").GetString());
    }

    internal static async Task<JsonElement> ReadDeliveredOrInlineAsync(TimelineViewHost host, JsonElement payload)
    {
        if (payload.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
        {
            using var data = JsonDocument.Parse(await host.ReadEvidenceAsync(delivery));
            return data.RootElement.Clone();
        }
        return payload;
    }
}
