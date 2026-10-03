using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using HelixTool.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineViewContractTests
{
    [Theory]
    [InlineData(1621192)]
    [InlineData(1600801)]
    public async Task TriageDefault_DropsPhase_PreservesFailedJobsTasksLogIdsAndErrorPreviews(int buildId)
    {
        var source = TimelineViewFixture.Real(buildId);
        var original = JsonSerializer.Serialize(source);
        await using var host = await TimelineViewHost.StartAsync(source, buildId);
        var first = await host.TimelineAsync();
        Assert.Equal("triage", first.GetProperty("projection").GetString());
        Assert.False(first.GetProperty("includePhase").GetBoolean());
        Assert.InRange(first.GetProperty("returned").GetInt32(), 1, 10);
        var rows = await host.ReadPagesAsync(first);
        Assert.DoesNotContain(rows, r => r.GetProperty("type").GetString() == "Phase");
        foreach (var record in source.Records.Where(r => r.Type is "Job" or "Task" && r.Result == "failed"))
        {
            var row = Assert.Single(rows, r => r.GetProperty("id").GetString() == record.Id);
            Assert.Equal(record.ParentId, row.GetProperty("parentId").GetString());
            Assert.Equal(record.Attempt, row.GetProperty("attempt").GetInt32());
            Assert.Equal(record.Log!.Id, row.GetProperty("log").GetProperty("id").GetInt32());
        }
        Assert.Equal(original, JsonSerializer.Serialize(source));
        if (buildId == 1621192)
        {
            var task = first.GetProperty("records")[0];
            Assert.Equal(TimelineViewFixture.MonitorTask, task.GetProperty("id").GetString());
            Assert.Equal("parsed", task.GetProperty("monitorEvidence").GetString());
            var preview = task.GetProperty("issuePreviews")[0];
            Assert.Contains("System.Diagnostics.Process.Tests", preview.GetProperty("message").GetString());
            Assert.Equal("warning", preview.GetProperty("type").GetString());
            Assert.Equal(1466, task.GetProperty("log").GetProperty("id").GetInt32());
            var job = Assert.Single(rows, r => r.GetProperty("id").GetString() == TimelineViewFixture.MonitorJob);
            Assert.Equal(TimelineViewFixture.MonitorPhase, job.GetProperty("parentId").GetString());
            Assert.Equal(TimelineViewFixture.MonitorStage, job.GetProperty("contextParentId").GetString());
        }
        else
        {
            Assert.Equal("Task", first.GetProperty("records")[0].GetProperty("type").GetString());
            Assert.Equal("error", first.GetProperty("records")[0].GetProperty("issuePreviews")[0].GetProperty("type").GetString());
            var warning = Assert.Single(rows, r => r.GetProperty("id").GetString() == TimelineViewFixture.WarningTask);
            Assert.Equal(6, warning.GetProperty("issueTotal").GetInt32());
            Assert.Equal(2, warning.GetProperty("issuePreviewTotal").GetInt32());
            var android = Assert.Single(warning.GetProperty("issuePreviews").EnumerateArray(),
                p => p.GetProperty("message").GetString()!.Contains("XA1040"));
            Assert.Equal(5, android.GetProperty("count").GetInt32());
            Assert.Equal(0, android.GetProperty("issueIndex").GetInt32());
            Assert.True(android.GetProperty("messageTruncated").GetBoolean());
            Assert.Equal(200, android.GetProperty("message").GetString()!.EnumerateRunes().Count());
            AssertFullAction(warning, TimelineViewFixture.WarningTask);
        }
    }

    public static IEnumerable<object[]> SelectorCases()
    {
        yield return [new Dictionary<string, object?> { ["recordId"] = TimelineViewFixture.SyntheticId(5) }, new[] { TimelineViewFixture.SyntheticId(5) }];
        yield return [new Dictionary<string, object?> { ["parentId"] = TimelineViewFixture.MonitorJob }, new string[] { TimelineViewFixture.MonitorTask }.Concat(Enumerable.Range(1, 7).Select(TimelineViewFixture.SyntheticId)).ToArray()];
        yield return [new Dictionary<string, object?> { ["type"] = "pHaSe" }, new[] { TimelineViewFixture.MonitorPhase }];
        yield return [new Dictionary<string, object?> { ["result"] = "skipped" }, new[] { TimelineViewFixture.SyntheticId(1) }];
        yield return [new Dictionary<string, object?> { ["state"] = "running" }, new[] { TimelineViewFixture.SyntheticId(2) }];
        yield return [new Dictionary<string, object?> { ["state"] = "pending" }, new[] { TimelineViewFixture.SyntheticId(3) }];
        yield return [new Dictionary<string, object?> { ["result"] = "none" }, new[] { TimelineViewFixture.SyntheticId(2), TimelineViewFixture.SyntheticId(3) }];
        yield return [new Dictionary<string, object?> { ["name"] = "succ*?ask" }, new[] { TimelineViewFixture.SyntheticId(5) }];
        yield return [new Dictionary<string, object?> { ["name"] = "successful" }, new[] { TimelineViewFixture.SyntheticId(5) }];
        yield return [new Dictionary<string, object?> { ["recordId"] = TimelineViewFixture.MonitorTask, ["parentId"] = TimelineViewFixture.MonitorStage }, Array.Empty<string>()];
    }

    [Theory]
    [MemberData(nameof(SelectorCases))]
    public async Task ExplicitSelectors_AndIntersections_BypassOnlyImplicitFailurePreset(
        Dictionary<string, object?> selectors, string[] expectedIds)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.WithSelectorRows());
        selectors["expand"] = "none";
        selectors["limit"] = 100;
        var view = await host.TimelineAsync(selectors);
        Assert.Equal("all", view.GetProperty("effectiveFilter").GetString());
        var rows = await host.ReadPagesAsync(view);
        Assert.Equal(expectedIds.Order(), rows.Select(r => r.GetProperty("id").GetString()).Order());
        Assert.Equal(expectedIds.Length, view.GetProperty("selectedTotal").GetInt32());
        Assert.Equal(expectedIds.Length == view.GetProperty("returned").GetInt32(), view.GetProperty("complete").GetBoolean());
        if (selectors.ContainsKey("type"))
            Assert.True(view.GetProperty("includePhase").GetBoolean());
        if (selectors.ContainsKey("recordId") && expectedIds.Length == 1)
        {
            selectors["filter"] = "failed";
            var deliberate = await host.TimelineAsync(selectors);
            Assert.Empty(Ids(deliberate));
        }
    }

    [Fact]
    public async Task SharedPredicate_PreservesWarningCanceledAndSkipped_ImplicitTriageAloneExcludesBareSkipped()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.WithSelectorRows());
        var implicitView = await host.TimelineAsync(new() { ["expand"] = "none", ["limit"] = 100 });
        Assert.DoesNotContain(TimelineViewFixture.SyntheticId(1), Ids(implicitView));
        Assert.Contains(TimelineViewFixture.SyntheticId(4), Ids(implicitView));
        Assert.Contains(TimelineViewFixture.SyntheticId(6), Ids(implicitView));
        var deliberate = await host.TimelineAsync(new() { ["filter"] = "failed", ["projection"] = "full", ["expand"] = "none", ["limit"] = 100 });
        Assert.Contains(TimelineViewFixture.SyntheticId(1), Ids(deliberate));
        Assert.Contains(TimelineViewFixture.SyntheticId(4), Ids(deliberate));
        Assert.Contains(TimelineViewFixture.SyntheticId(6), Ids(deliberate));
    }

    [Theory]
    [InlineData(true, "unresolved")]
    [InlineData(false, "nameHint")]
    public async Task FailedMonitor_UnresolvedFailureAndNameHints_AreVisibleWithoutInventingParsedHelixEvidence(bool hasHeader, string expected)
    {
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.Real(1621192), TimelineViewFixture.MonitorTask,
            hasHeader ? [new AzdoIssue { Type = "error", Message = "Failed work item information:" }] : []);
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync();
        var task = Assert.Single(view.GetProperty("records").EnumerateArray(),
            r => r.GetProperty("id").GetString() == TimelineViewFixture.MonitorTask);
        Assert.Equal(expected, task.GetProperty("monitorEvidence").GetString());
    }

    [Theory]
    [InlineData("none", 1)]
    [InlineData("ancestors", 4)]
    [InlineData("children", 2)]
    [InlineData("descendants", 2)]
    [InlineData("ancestorsAndChildren", 5)]
    [InlineData("ancestorsAndDescendants", 5)]
    public async Task Expansion_UsesFullGraphBeforePhaseSuppression_AndPreservesMatchKinds(string expand, int expected)
    {
        var source = TimelineViewFixture.Real(1621192);
        var child = source.Records[0] with { Id = TimelineViewFixture.SyntheticId(99), ParentId = TimelineViewFixture.MonitorTask, Name = "Nested task" };
        source = source with { Records = [.. source.Records, child] };
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new()
        {
            ["recordId"] = TimelineViewFixture.MonitorTask, ["projection"] = "compact",
            ["expand"] = expand, ["limit"] = 100
        });
        Assert.Equal(expected, view.GetProperty("selectedTotal").GetInt32());
        Assert.Equal(1, view.GetProperty("matched").GetInt32());
        Assert.Equal("match", view.GetProperty("records").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == TimelineViewFixture.MonitorTask).GetProperty("matchKind").GetString());
        Assert.Equal(expected, Ids(view).Distinct().Count());
    }

    [Theory]
    [InlineData("triage")]
    [InlineData("compact")]
    [InlineData("full")]
    [InlineData("summary")]
    public async Task EveryProjection_HasTruthfulEnvelopeAndMatchedCounts(string projection)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var view = await host.TimelineAsync(new()
        {
            ["filter"] = "all", ["projection"] = projection, ["limit"] = 100
        });
        Assert.True(view.GetProperty("ok").GetBoolean());
        Assert.Equal(4, view.GetProperty("timelineRecords").GetInt32());
        Assert.Equal("matched", view.GetProperty("counts").GetProperty("scope").GetString());
        Assert.True(view.GetProperty("counts").GetProperty("complete").GetBoolean());
        Assert.Equal(view.GetProperty("matched").GetInt32(), view.GetProperty("counts").GetProperty("byTypeResult")
            .EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Equal(64, view.GetProperty("viewId").GetString()!.Length);
        if (projection == "summary")
        {
            Assert.Empty(Ids(view));
            Assert.Equal(0, view.GetProperty("total").GetInt32());
            Assert.Equal(0, view.GetProperty("returned").GetInt32());
            Assert.Equal(3, view.GetProperty("selectedTotal").GetInt32());
            Assert.Equal(3, view.GetProperty("matched").GetInt32());
            Assert.DoesNotContain(view.GetProperty("counts").GetProperty("byTypeResult").EnumerateArray(),
                c => c.GetProperty("type").GetString() == "Phase");
            Assert.True(view.GetProperty("complete").GetBoolean());
            Assert.False(view.GetProperty("truncated").GetBoolean());
        }
        else if (projection == "compact")
        {
            var row = view.GetProperty("records")[0];
            Assert.False(row.TryGetProperty("issues", out _));
            Assert.False(row.TryGetProperty("issuePreviews", out _));
            Assert.Equal(3, row.GetProperty("issueCount").GetInt32());
            Assert.Equal(2, row.GetProperty("errorCount").GetInt32());
            Assert.Equal(1, row.GetProperty("warningCount").GetInt32());
        }
        else if (projection == "full")
        {
            Assert.Contains(TimelineViewFixture.MonitorPhase, Ids(view));
            Assert.Equal(TimelineViewFixture.MonitorMessage, view.GetProperty("records")[0].GetProperty("issues")[0].GetProperty("message").GetString());
        }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("failed")]
    [InlineData("issues")]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData("incomplete")]
    public async Task EveryFilterPreset_RemainsAvailable(string filter)
    {
        var source = TimelineViewFixture.WithSelectorRows();
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new()
        {
            ["filter"] = filter, ["projection"] = "full", ["expand"] = "none", ["limit"] = 100
        });
        IEnumerable<AzdoTimelineRecord> expected = filter switch
        {
            "all" => source.Records,
            "failed" => source.Records.Where(r => r.Result is not null and not "succeeded" || r.Issues?.Count > 0),
            "issues" => source.Records.Where(r => r.Issues?.Count > 0),
            "running" => source.Records.Where(r => r.State == "inProgress"),
            "pending" => source.Records.Where(r => r.State == "pending"),
            _ => source.Records.Where(r => r.State != "completed")
        };
        var rows = await host.ReadPagesAsync(view);
        Assert.Equal(expected.Select(r => r.Id).Order(), rows.Select(r => r.GetProperty("id").GetString()).Order());
    }

    [Fact]
    public async Task PreviewGrouping_BeforeTruncation_DoesNotMergeDistinctSuffixesIdsPathsOrAttempts()
    {
        var prefix = new string('x', 220);
        var issues = new AzdoIssue[]
        {
            new() { Type = "error", Message = prefix + " code AX1 work/item-a path/a.cs" },
            new() { Type = "error", Message = prefix + " code AX2 work/item-b path/b.cs" },
            new() { Type = "error", Message = "code AX3: path/c.cs\nwork item 42" },
            new() { Type = "error", Message = "\u001b[31mcode AX3: path/c.cs\r\nwork item 42\u001b[0m  " },
            new() { Type = "warning", Message = "code AX3: path/c.cs\nwork item 42" }
        };
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.Real(1621192), TimelineViewFixture.MonitorTask, issues);
        var secondAttempt = source.Records[0] with { Id = TimelineViewFixture.SyntheticId(88), Attempt = 2, Log = new() { Id = 888 } };
        source = source with { Records = [.. source.Records, secondAttempt] };
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new() { ["type"] = "Task", ["expand"] = "none", ["previewChars"] = 20, ["limit"] = 100 });
        Assert.Equal(2, view.GetProperty("matched").GetInt32());
        foreach (var row in view.GetProperty("records").EnumerateArray())
        {
            Assert.Equal(5, row.GetProperty("issueTotal").GetInt32());
            Assert.Equal(4, row.GetProperty("issuePreviewTotal").GetInt32());
            var previews = row.GetProperty("issuePreviews").EnumerateArray().ToArray();
            var samePrefix = previews.Where(p => p.GetProperty("issueIndex").GetInt32() is 0 or 1).ToArray();
            Assert.Equal(2, samePrefix.Length);
            Assert.All(samePrefix, p => Assert.Equal(1, p.GetProperty("count").GetInt32()));
            Assert.Equal(samePrefix[0].GetProperty("message").GetString(), samePrefix[1].GetProperty("message").GetString());
            var normalized = Assert.Single(previews, p => p.GetProperty("count").GetInt32() == 2);
            Assert.True(normalized.GetProperty("normalized").GetBoolean());
            Assert.Equal(2, normalized.GetProperty("issueIndex").GetInt32());
            AssertFullAction(row, row.GetProperty("id").GetString()!);
        }
        Assert.Equal(new[] { 1, 2 }, view.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("attempt").GetInt32()).Order());
        Assert.Equal(new[] { 888, 1466 }, view.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("log").GetProperty("id").GetInt32()).Order());
        Assert.Equal(issues[3].Message, source.Records[0].Issues![3].Message);
    }

    [Fact]
    public async Task PreviewLimits_UnicodeScalarClippingAndOmittedGroups_HaveExecutableFullRecordAction()
    {
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.Real(1621192), TimelineViewFixture.MonitorTask,
            new() { Type = "error", Message = string.Concat(Enumerable.Repeat("\U0001f680", 250)) },
            new() { Type = "error", Message = "second distinct error" });
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new() { ["recordId"] = TimelineViewFixture.MonitorTask, ["expand"] = "none", ["previewIssueLimit"] = 1, ["previewChars"] = 199 });
        var row = Assert.Single(view.GetProperty("records").EnumerateArray());
        Assert.Equal(2, row.GetProperty("issuePreviewTotal").GetInt32());
        Assert.True(row.GetProperty("previewsTruncated").GetBoolean());
        var text = Assert.Single(row.GetProperty("issuePreviews").EnumerateArray()).GetProperty("message").GetString()!;
        Assert.Equal(199, text.EnumerateRunes().Count());
        Assert.DoesNotContain('\ufffd', text);
        var full = await host.ExecuteAsync(AssertFullAction(row, TimelineViewFixture.MonitorTask));
        if (full.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
        {
            var payload = await host.ReadEvidenceAsync(delivery);
            Assert.Contains(source.Records[0].Issues![0].Message!, Encoding.UTF8.GetString(payload));
        }
        else
            Assert.Equal(source.Records[0].Issues![0].Message, full.GetProperty("records")[0].GetProperty("issues")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TimestampOnlyNormalization_DeduplicatesWithoutErasingMeaningfulInternalWhitespace()
    {
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.Real(1621192), TimelineViewFixture.MonitorTask,
            new() { Type = "error", Message = "error ABC42: path/a.cs work item 42" },
            new() { Type = "error", Message = "2026-10-02T14:16:00.1500000Z error ABC42: path/a.cs work item 42" },
            new() { Type = "error", Message = "error ABC42: path/a.cs work  item 42" });
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new()
        {
            ["recordId"] = TimelineViewFixture.MonitorTask, ["expand"] = "none"
        });
        var row = Assert.Single(view.GetProperty("records").EnumerateArray());
        Assert.Equal(3, row.GetProperty("issueTotal").GetInt32());
        Assert.Equal(2, row.GetProperty("issuePreviewTotal").GetInt32());
        var grouped = Assert.Single(row.GetProperty("issuePreviews").EnumerateArray(), p => p.GetProperty("count").GetInt32() == 2);
        Assert.True(grouped.GetProperty("normalized").GetBoolean());
        Assert.Equal("error ABC42: path/a.cs work item 42", grouped.GetProperty("message").GetString());
        Assert.Contains(row.GetProperty("issuePreviews").EnumerateArray(),
            p => p.GetProperty("message").GetString()!.Contains("work  item"));
    }

    [Fact]
    public async Task ByteShortenedPaging_NoGapsDuplicates_NextUsesReturned_AndFinalNonzeroPageRemainsIncomplete()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1600801), 1600801);
        var first = await host.TimelineAsync(new() { ["filter"] = "all", ["projection"] = "compact", ["limit"] = 100, ["maxResponseBytes"] = 8192 });
        Assert.InRange(first.GetProperty("returned").GetInt32(), 1, 19);
        var viewId = first.GetProperty("viewId").GetString();
        var rows = await host.ReadPagesAsync(first, page =>
        {
            Assert.InRange(host.LastResponseBytes, 1, 8192);
            Assert.Equal(viewId, page.GetProperty("viewId").GetString());
            Assert.False(page.GetProperty("complete").GetBoolean());
            Assert.True(page.GetProperty("truncated").GetBoolean());
            Assert.Equal(20, page.GetProperty("total").GetInt32());
            Assert.Equal(page.GetProperty("total").GetInt32(), page.GetProperty("totalRecords").GetInt32());
            Assert.True(page.TryGetProperty("continuation", out var continuation) && continuation.ValueKind == JsonValueKind.Object
                || page.TryGetProperty("recovery", out var recovery) && recovery.ValueKind == JsonValueKind.Object);
        });
        Assert.Equal(20, rows.Count);
        Assert.Equal(20, rows.Select(r => r.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(TimelineViewFixture.Real(1600801).Records.Select(r => r.Id), rows.Select(r => r.GetProperty("id").GetString()));
        var differentBudget = await host.TimelineAsync(new() { ["filter"] = "all", ["projection"] = "compact", ["offset"] = 1, ["limit"] = 1, ["maxResponseBytes"] = 12288 });
        Assert.Equal(viewId, differentBudget.GetProperty("viewId").GetString());
        var differentProjection = await host.TimelineAsync(new() { ["filter"] = "all", ["projection"] = "summary" });
        Assert.NotEqual(viewId, differentProjection.GetProperty("viewId").GetString());
    }

    [Fact]
    public async Task StaleViewId_IsDomainError_InsteadOfSilentlyShiftingPage()
    {
        var source = TimelineViewFixture.Real(1621192);
        await using var host = await TimelineViewHost.StartAsync(source);
        var first = await host.TimelineAsync(new() { ["limit"] = 1 });
        host.SetTimeline(source with { Id = Guid.NewGuid().ToString() });
        var result = await host.CallAsync("azdo_timeline", new()
        {
            ["buildIdOrUrl"] = "1621192", ["offset"] = 1,
            ["viewId"] = first.GetProperty("viewId").GetString()
        });
        Assert.True(result.IsError);
        Assert.Contains("view", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task FinalPartialPage_WithNextNull_HasExecutableWholeSelectionRecovery()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var last = await host.TimelineAsync(new()
        {
            ["filter"] = "all", ["projection"] = "compact", ["expand"] = "none", ["offset"] = 3, ["limit"] = 1
        });
        Assert.False(last.GetProperty("complete").GetBoolean());
        Assert.True(last.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next").ValueKind);
        var action = last.TryGetProperty("continuation", out var continuation)
            ? continuation : last.GetProperty("recovery");
        var recovered = await host.ExecuteAsync(action);
        if (recovered.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
        {
            Assert.True(delivery.GetProperty("complete").GetBoolean());
            using var full = JsonDocument.Parse(await host.ReadEvidenceAsync(delivery));
            Assert.Equal(4, full.RootElement.GetProperty("records").GetArrayLength());
        }
        else
        {
            Assert.True(recovered.GetProperty("complete").GetBoolean());
            Assert.Equal(4, recovered.GetProperty("records").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task MalformedGraph_FailsClosedWithTimelineGraphInvalid(string kind)
    {
        var source = TimelineViewFixture.Real(1621192);
        source = kind switch
        {
            "cycle" => source with { Records = source.Records.Select(r => r.Id == TimelineViewFixture.MonitorStage ? r with { ParentId = TimelineViewFixture.MonitorTask } : r).ToArray() },
            "missing" => source with { Records = source.Records.Where(r => r.Id != TimelineViewFixture.MonitorPhase).ToArray() },
            _ => source with { Records = [.. source.Records, source.Records[0]] }
        };
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new() { ["recordId"] = TimelineViewFixture.MonitorTask, ["projection"] = "compact", ["limit"] = 100 });
        Assert.False(view.GetProperty("complete").GetBoolean());
        Assert.True(view.GetProperty("truncated").GetBoolean());
        Assert.Contains(view.GetProperty("incompleteDetails").EnumerateArray(), d => d.GetProperty("code").GetString() == "timeline_graph_invalid");
    }

    [Fact]
    public async Task FullIssueWindows_FailClosedOnFinalPage_AndKeepRawIssueCoordinates()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var view = await host.TimelineAsync(new()
        {
            ["recordId"] = TimelineViewFixture.MonitorTask, ["projection"] = "full",
            ["expand"] = "none", ["issueOffset"] = 2, ["issueLimit"] = 1
        });
        Assert.False(view.GetProperty("complete").GetBoolean());
        Assert.True(view.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, view.GetProperty("issueTotal").GetInt32());
        Assert.Equal(1, view.GetProperty("issueReturned").GetInt32());
        Assert.Equal(2, view.GetProperty("issueOffset").GetInt32());
        Assert.False(view.GetProperty("issueComplete").GetBoolean());
        Assert.True(view.GetProperty("issueTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("issueNext").ValueKind);
        Assert.Equal("Bash exited with code '1'.", view.GetProperty("records")[0].GetProperty("issues")[0].GetProperty("message").GetString());
        Assert.True(view.TryGetProperty("recovery", out _) || view.TryGetProperty("continuation", out _));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("file")]
    [InlineData("inline")]
    [InlineData("chunked")]
    public async Task AccessFirst_OversizedExplicitSuccessfulRecord_IsDeliveredLosslesslyNeverSizeError(string delivery)
    {
        var message = string.Concat(Enumerable.Repeat("escaped \"path\\test\" \U0001f680\n", 45000));
        var source = TimelineViewFixture.WithSelectorRows();
        source = TimelineViewFixture.WithIssues(source, TimelineViewFixture.SyntheticId(5), new AzdoIssue { Type = "warning", Message = message });
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new()
        {
            ["recordId"] = TimelineViewFixture.SyntheticId(5), ["projection"] = "full",
            ["expand"] = "none", ["delivery"] = delivery, ["maxResponseBytes"] = 4096
        });
        Assert.Equal("all", view.GetProperty("effectiveFilter").GetString());
        var descriptor = view.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        Assert.False(view.GetProperty("complete").GetBoolean());
        Assert.True(view.GetProperty("truncated").GetBoolean());
        Assert.True(view.TryGetProperty("continuation", out _));
        var bytes = await host.ReadEvidenceAsync(descriptor);
        using var full = JsonDocument.Parse(bytes);
        var record = Assert.Single(full.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal(TimelineViewFixture.SyntheticId(5), record.GetProperty("id").GetString());
        Assert.Equal(message, record.GetProperty("issues")[0].GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(1621192, 12288)]
    [InlineData(1600801, 12288)]
    [InlineData(1600801, 16384)]
    public async Task HttpWireSize_IncludesSdkTextStructuredDuplicationEscapingAndFraming_OnEveryPage(int buildId, int budget)
    {
        var source = TimelineViewFixture.Real(buildId);
        source = source with
        {
            Records = source.Records.Select(r => r with
            {
                Name = r.Name + string.Concat(Enumerable.Repeat(" \"quoted\\path\"\U0001f680", 15))
            }).ToArray()
        };
        await using var host = await TimelineViewHost.StartAsync(source, buildId);
        var first = await host.TimelineAsync(new() { ["maxResponseBytes"] = budget });
        await host.ReadPagesAsync(first, _ => Assert.InRange(host.LastResponseBytes, 1, budget));
        Assert.InRange(host.LastResponseBytes, 1, budget);
    }

    [Fact]
    public async Task CanonicalResult_SurvivesStrictToolsCallBinding_WhileSearchResultAliasStillWorks()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.WithSelectorRows());
        var view = await host.TimelineAsync(new() { ["result"] = "succeeded", ["expand"] = "none" });
        Assert.Equal(new[] { TimelineViewFixture.SyntheticId(5) }, Ids(view));
        var result = await host.CallAsync("azdo_search_timeline", new()
        {
            ["buildId"] = "1621192", ["pattern"] = "Monitor", ["result"] = "failed"
        });
        Assert.False(result.IsError is true, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AccessFirst_ExplicitAllLargerCountAndPreviews_AreCompleteViaVerifiedFile_NotPolicyMaximum()
    {
        var original = TimelineViewFixture.Real(1621192);
        var source = original with
        {
            Records = Enumerable.Range(1, 250).Select(i => original.Records[0] with
            {
                Id = TimelineViewFixture.SyntheticId(i), ParentId = null, Attempt = i,
                Name = $"Monitor attempt {i}", Log = new() { Id = 3000 + i },
                Issues = Enumerable.Range(1, 8).Select(j => new AzdoIssue
                {
                    Type = "error", Message = $"{new string('x', 1024)} work item {i} code {j}"
                }).ToArray()
            }).ToArray()
        };
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new()
        {
            ["filter"] = "all", ["projection"] = "full", ["expand"] = "none",
            ["limit"] = 250, ["all"] = true, ["delivery"] = "file",
            ["previewIssueLimit"] = 8, ["previewChars"] = 1024
        });
        Assert.Equal(250, view.GetProperty("selectedTotal").GetInt32());
        var descriptor = view.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        using var complete = JsonDocument.Parse(await host.ReadEvidenceAsync(descriptor));
        var records = complete.RootElement.GetProperty("records");
        Assert.Equal(250, records.GetArrayLength());
        Assert.Equal(source.Records.Select(r => r.Id), records.EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.All(records.EnumerateArray(), r => Assert.Equal(8, r.GetProperty("issues").GetArrayLength()));
    }

    [Fact]
    public async Task AccessFirst_OversizedNameAndSelectorAction_UseReadableDeliveryWithBoundedHeader()
    {
        var name = string.Concat(Enumerable.Repeat("quoted \"name\\path\" \U0001f680 ", 1000));
        var source = TimelineViewFixture.Real(1621192) with
        {
            Records = [new()
            {
                Id = TimelineViewFixture.MonitorTask, Name = name, Type = "Task", State = "completed",
                Result = "succeeded", Log = new() { Id = 1466 }, Attempt = 1
            }]
        };
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync(new()
        {
            ["name"] = name, ["expand"] = "none", ["projection"] = "full", ["maxResponseBytes"] = 4096
        });
        Assert.Equal(4096, view.GetProperty("requestedMaxResponseBytes").GetInt64());
        Assert.Equal(8192, view.GetProperty("maxResponseBytes").GetInt64());
        Assert.InRange(host.LastResponseBytes, 1, 8192);
        Assert.True(view.TryGetProperty("continuation", out _));
        using var full = JsonDocument.Parse(await host.ReadEvidenceAsync(view.GetProperty("delivery")));
        Assert.Equal(name, full.RootElement.GetProperty("records")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(400)]
    public async Task DefaultWireBudget_SingleEscapedLongName_IsDeliveredWithinTarget(int repeats)
    {
        var source = TimelineViewFixture.EscapedLongName(repeats);
        var name = source.Records[0].Name;
        await using var host = await TimelineViewHost.StartAsync(source);
        var view = await host.TimelineAsync();
        Assert.InRange(host.LastResponseBytes, 1, 12288);
        if (view.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
        {
            Assert.True(view.TryGetProperty("continuation", out _));
            using var full = JsonDocument.Parse(await host.ReadEvidenceAsync(delivery));
            Assert.Equal(name, full.RootElement.GetProperty("records")[0].GetProperty("name").GetString());
        }
        else
            Assert.Equal(name, Assert.Single(view.GetProperty("records").EnumerateArray()).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(120)]
    [InlineData(130)]
    [InlineData(140)]
    public async Task DefaultWireBudget_PagedEscapedLongName_EveryPageStaysWithinTarget(int repeats)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.EscapedLongNameWithSecondRow(repeats));
        var first = await host.TimelineAsync();
        Assert.InRange(host.LastResponseBytes, 1, 12288);
        if (first.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
        {
            using var full = JsonDocument.Parse(await host.ReadEvidenceAsync(delivery));
            Assert.Equal(2, full.RootElement.GetProperty("records").GetArrayLength());
        }
        else
        {
            var rows = await host.ReadPagesAsync(first, _ => Assert.InRange(host.LastResponseBytes, 1, 12288));
            Assert.Equal(2, rows.Count);
        }
    }

    [Theory]
    [InlineData(1, 8192)]
    [InlineData(4096, 8192)]
    [InlineData(8191, 8192)]
    [InlineData(8192, 8192)]
    [InlineData(12288, 12288)]
    [InlineData(16384, 16384)]
    [InlineData(16385, 16384)]
    [InlineData(100000, 16384)]
    public async Task ResponseTarget_IsShapedNotRejected_ReportsEffectiveTarget(long requested, long effective)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var view = await host.TimelineAsync(new()
        {
            ["projection"] = "summary", ["maxResponseBytes"] = requested
        });
        Assert.Equal(requested, view.GetProperty("requestedMaxResponseBytes").GetInt64());
        Assert.Equal(effective, view.GetProperty("maxResponseBytes").GetInt64());
        Assert.InRange(host.LastResponseBytes, 1, effective);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("base64")]
    public async Task EvidenceReader_LargerExplicitRange_IsShapedWithAdvancingExecutableNext_NotRejected(string encoding)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("escaped \"\\\" \U0001f680\n", 10000)));
        var descriptor = host.PutEvidence(bytes, "text/plain");
        var read = TimelineViewHost.Payload(await host.CallAsync("hlx_read_evidence", new()
        {
            ["evidenceId"] = descriptor.GetProperty("evidenceId").GetString(),
            ["lengthBytes"] = 1024 * 1024, ["encoding"] = encoding, ["maxResponseBytes"] = 4096
        }));
        Assert.Equal(4096, read.GetProperty("requestedMaxResponseBytes").GetInt64());
        Assert.Equal(8192, read.GetProperty("maxResponseBytes").GetInt64());
        Assert.InRange(host.LastResponseBytes, 1, 8192);
        Assert.True(read.GetProperty("sourceComplete").GetBoolean());
        Assert.False(read.GetProperty("rangeComplete").GetBoolean());
        Assert.InRange(read.GetProperty("returnedBytes").GetInt64(), 1, bytes.LongLength - 1);
        var continuation = read.GetProperty("next");
        Assert.Equal("hlx_read_evidence", continuation.GetProperty("tool").GetString());
        var next = await host.ExecuteAsync(continuation);
        Assert.Equal(read.GetProperty("returnedBytes").GetInt64(), next.GetProperty("offsetBytes").GetInt64());
        Assert.Equal(8192, next.GetProperty("maxResponseBytes").GetInt64());
        Assert.InRange(host.LastResponseBytes, 1, 8192);
    }

    [Fact]
    public async Task BinaryAutoEvidenceReader_OffersDelivery_NotUnsolicitedWholeBase64OrUnsupportedBinaryError()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var bytes = Enumerable.Range(0, 100000).Select(i => (byte)(i % 256)).ToArray();
        var descriptor = host.PutEvidence(bytes, "application/octet-stream");
        var read = TimelineViewHost.Payload(await host.CallAsync("hlx_read_evidence", new()
        {
            ["evidenceId"] = descriptor.GetProperty("evidenceId").GetString()
        }));
        Assert.True(read.GetProperty("sourceComplete").GetBoolean());
        Assert.False(read.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String,
            "Binary auto mode must offer a delivery action; base64 requires explicit caller intent.");
        Assert.True(read.GetProperty("delivery").GetProperty("complete").GetBoolean());
        Assert.True(read.TryGetProperty("recovery", out _) || read.TryGetProperty("next", out _));
        Assert.Equal(bytes, await host.ReadEvidenceAsync(descriptor));
    }

    [Fact]
    public async Task SummaryMatchedCounts_DoNotChangeWithAncestorExpansionOrOutputPaging()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1600801), 1600801);
        var first = await host.TimelineAsync(new()
        {
            ["type"] = "Task", ["result"] = "failed", ["projection"] = "compact", ["expand"] = "none", ["limit"] = 1
        });
        var expanded = await host.TimelineAsync(new()
        {
            ["type"] = "Task", ["result"] = "failed", ["projection"] = "compact", ["expand"] = "ancestors", ["offset"] = 1, ["limit"] = 2
        });
        Assert.Equal(3, first.GetProperty("matched").GetInt32());
        Assert.Equal(3, expanded.GetProperty("matched").GetInt32());
        Assert.True(JsonElement.DeepEquals(first.GetProperty("counts"), expanded.GetProperty("counts")));
        Assert.True(expanded.GetProperty("selectedTotal").GetInt32() > first.GetProperty("selectedTotal").GetInt32());
    }

    [Fact]
    public async Task UnknownProviderValues_AreCountedInBoundedOtherBuckets_NotDiscarded()
    {
        var source = new AzdoTimeline
        {
            Id = "83d15a17-0eb8-462b-96df-c4f2310c0f2f",
            Records = Enumerable.Range(1, 100).Select(i => new AzdoTimelineRecord
            {
                Id = TimelineViewFixture.SyntheticId(i), Type = $"FutureType{i}",
                Result = $"FutureResult{i}", State = $"FutureState{i}", Name = $"Future row {i}"
            }).ToArray()
        };
        await using var host = await TimelineViewHost.StartAsync(source);
        var summary = await host.TimelineAsync(new() { ["filter"] = "all", ["projection"] = "summary" });
        Assert.Equal(100, summary.GetProperty("matched").GetInt32());
        var buckets = summary.GetProperty("counts").GetProperty("byTypeResult").EnumerateArray().ToArray();
        Assert.Equal(100, buckets.Sum(c => c.GetProperty("count").GetInt32()));
        Assert.Contains(buckets, c => c.GetProperty("type").GetString() == "other" && c.GetProperty("result").GetString() == "other");
        Assert.InRange(host.LastResponseBytes, 1, 12288);
        var all = await host.TimelineAsync(new()
        {
            ["filter"] = "all", ["projection"] = "compact", ["limit"] = 100
        });
        var rows = await host.ReadPagesAsync(all);
        Assert.Equal(100, rows.Count);
    }

    [Fact]
    public async Task SummaryIncludePhase_IsExplicitAndCountsItWithoutClaimingReturnedRecords()
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        var summary = await host.TimelineAsync(new()
        {
            ["filter"] = "all", ["projection"] = "summary", ["includePhase"] = true
        });
        Assert.True(summary.GetProperty("includePhase").GetBoolean());
        Assert.Equal(4, summary.GetProperty("matched").GetInt32());
        Assert.Equal(4, summary.GetProperty("selectedTotal").GetInt32());
        Assert.Equal(0, summary.GetProperty("total").GetInt32());
        Assert.Empty(Ids(summary));
        Assert.Contains(summary.GetProperty("counts").GetProperty("byTypeResult").EnumerateArray(),
            c => c.GetProperty("type").GetString() == "Phase" && c.GetProperty("count").GetInt32() == 1);
        Assert.True(summary.GetProperty("complete").GetBoolean());
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        yield return [new Dictionary<string, object?> { ["recordId"] = "not-a-guid" }];
        yield return [new Dictionary<string, object?> { ["parentId"] = "not-a-guid" }];
        yield return [new Dictionary<string, object?> { ["projection"] = "countsOnly" }];
        yield return [new Dictionary<string, object?> { ["expand"] = "everything" }];
        yield return [new Dictionary<string, object?> { ["type"] = "inProgress" }];
        yield return [new Dictionary<string, object?> { ["result"] = "completed" }];
        yield return [new Dictionary<string, object?> { ["state"] = "failed" }];
        yield return [new Dictionary<string, object?> { ["offset"] = -1 }];
        yield return [new Dictionary<string, object?> { ["limit"] = 0 }];
        yield return [new Dictionary<string, object?> { ["previewChars"] = 0 }];
        yield return [new Dictionary<string, object?> { ["issueOffset"] = 1 }];
        yield return [new Dictionary<string, object?> { ["type"] = "Phase", ["includePhase"] = false }];
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidSelectorsAndConflictingPhaseSuppression_AreRealValidationErrors(Dictionary<string, object?> arguments)
    {
        await using var host = await TimelineViewHost.StartAsync(TimelineViewFixture.Real(1621192));
        arguments["buildIdOrUrl"] = "1621192";
        var result = await host.CallAsync("azdo_timeline", arguments);
        Assert.True(result.IsError);
    }

    internal static string[] Ids(JsonElement view) =>
        view.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();

    private static JsonElement AssertFullAction(JsonElement row, string recordId)
    {
        var action = row.GetProperty("fullAction");
        Assert.Equal("azdo_timeline", action.GetProperty("tool").GetString());
        var args = action.GetProperty("arguments");
        Assert.Equal(recordId, args.GetProperty("recordId").GetString());
        Assert.Equal("full", args.GetProperty("projection").GetString());
        Assert.True(args.TryGetProperty("buildIdOrUrl", out _));
        Assert.True(args.TryGetProperty("delivery", out _));
        return action;
    }
}

internal sealed class TimelineViewHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _http;
    private readonly HttpClientTransport _transport;
    private readonly McpClient _client;
    private readonly IAzdoApiClient _api;
    private readonly CaptureResponseHandler _capture;
    private readonly int _buildId;
    private readonly string _runtimeRoot;
    private readonly HashSet<string> _deliveryFiles = [];

    private TimelineViewHost(IHost host, HttpClient http, HttpClientTransport transport, McpClient client,
        IAzdoApiClient api, CaptureResponseHandler capture, int buildId, string runtimeRoot)
    {
        _host = host; _http = http; _transport = transport; _client = client;
        _api = api; _capture = capture; _buildId = buildId;
        _runtimeRoot = runtimeRoot;
    }

    public int LastResponseBytes => _capture.LastResponseBytes;

    public JsonElement PutEvidence(byte[] bytes, string mediaType)
    {
        var descriptor = JsonSerializer.SerializeToElement(_host.Services.GetRequiredService<IEvidenceDeliveryStore>()
            .PutBytes(bytes, mediaType, "fixture-fingerprint", "fixture:source", "public"));
        TrackDelivery(descriptor);
        return descriptor;
    }

    public static async Task<TimelineViewHost> StartAsync(AzdoTimeline source, int buildId = 1621192,
        IAzdoApiClient? api = null, CacheOptions? cacheOptions = null)
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"hlx-timeline-host-{Guid.NewGuid():N}");
        cacheOptions ??= new CacheOptions { CacheRoot = runtimeRoot };
        api ??= Substitute.For<IAzdoApiClient>();
        if (api is not HelixTool.Core.AzDO.CachingAzdoApiClient)
            api.GetTimelineAsync("dnceng-public", "public", buildId, Arg.Any<CancellationToken>()).Returns(source);
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(api);
                services.AddSingleton(Substitute.For<IAzdoTokenAccessor>());
                services.AddSingleton(new AzdoService(api));
                services.AddSingleton(cacheOptions);
                services.AddSingleton<IEvidenceDeliveryStore>(new FileEvidenceDeliveryStore(cacheOptions));
                services.AddMcpServer(options =>
                {
                    options.AddBindingErrorFilter();
                    options.AddUnknownParameterFilter(typeof(AzdoMcpTools).Assembly);
                    options.AddAcquisitionErrorFilter();
                })
                    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                    .WithTools<AzdoMcpTools>()
                    .WithTools<HlxEvidenceMcpTools>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapMcp());
            });
        }).StartAsync();
        var capture = new CaptureResponseHandler(host.GetTestServer().CreateHandler());
        var http = new HttpClient(capture);
        var transport = new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/") }, http);
        var client = await McpClient.CreateAsync(transport, new() { ProtocolVersion = "2025-11-25" });
        return new(host, http, transport, client, api, capture, buildId, runtimeRoot);
    }

    public void SetTimeline(AzdoTimeline source) =>
        _api.GetTimelineAsync("dnceng-public", "public", _buildId, Arg.Any<CancellationToken>()).Returns(source);

    public Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> args) => _client.CallToolAsync(tool, args).AsTask();

    public async Task<JsonElement> TimelineAsync(Dictionary<string, object?>? args = null)
    {
        args ??= [];
        args.TryAdd("buildIdOrUrl", _buildId.ToString());
        return TrackPayload(Payload(await CallAsync("azdo_timeline", args)));
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement action) =>
        TrackPayload(Payload(await CallAsync(action.GetProperty("tool").GetString()!,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(action.GetProperty("arguments").GetRawText())!)));

    private JsonElement TrackPayload(JsonElement payload)
    {
        if (payload.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
            TrackDelivery(delivery);
        return payload;
    }

    private void TrackDelivery(JsonElement delivery)
    {
        var path = delivery.GetProperty("localPath").GetString()!;
        if (Path.GetFileName(path) == delivery.GetProperty("evidenceId").GetString() + ".bin")
            _deliveryFiles.Add(path);
    }

    public async Task<List<JsonElement>> ReadPagesAsync(JsonElement first, Action<JsonElement>? inspect = null)
    {
        List<JsonElement> rows = [];
        var page = first;
        for (var i = 0; i < 200; i++)
        {
            inspect?.Invoke(page);
            rows.AddRange(page.GetProperty("records").EnumerateArray().Select(r => r.Clone()));
            var returned = page.GetProperty("returned").GetInt32();
            Assert.Equal(page.GetProperty("records").GetArrayLength(), returned);
            var next = page.GetProperty("next");
            if (next.ValueKind == JsonValueKind.Null)
                return rows;
            Assert.True(returned > 0, "A zero-row repeated page is not an access path.");
            Assert.Equal(page.GetProperty("offset").GetInt32() + returned, next.GetProperty("offset").GetInt32());
            Assert.Equal(page.GetProperty("viewId").GetString(), next.GetProperty("viewId").GetString());
            page = await ExecuteAsync(page.GetProperty("continuation"));
        }
        throw new InvalidOperationException("Timeline continuation did not terminate.");
    }

    public async Task<byte[]> ReadEvidenceAsync(JsonElement delivery)
    {
        var id = delivery.GetProperty("evidenceId").GetString()!;
        using var output = new MemoryStream();
        var offset = 0L;
        while (offset < delivery.GetProperty("bytes").GetInt64())
        {
            var read = Payload(await CallAsync("hlx_read_evidence", new()
            {
                ["evidenceId"] = id, ["offsetBytes"] = offset, ["lengthBytes"] = 2048, ["encoding"] = "base64"
            }));
            Assert.Equal(offset, read.GetProperty("offsetBytes").GetInt64());
            Assert.True(read.GetProperty("sourceComplete").GetBoolean());
            Assert.Equal(delivery.GetProperty("bytes").GetInt64(), read.GetProperty("totalBytes").GetInt64());
            Assert.Equal(delivery.GetProperty("sha256").GetString(), read.GetProperty("sha256").GetString());
            Assert.InRange(LastResponseBytes, 1, 12288);
            var bytes = Convert.FromBase64String(read.GetProperty("data").GetString()!);
            Assert.Equal(bytes.Length, read.GetProperty("returnedBytes").GetInt32());
            Assert.NotEmpty(bytes);
            output.Write(bytes);
            offset += bytes.Length;
            if (read.GetProperty("next").ValueKind == JsonValueKind.Object)
            {
                var next = read.GetProperty("next");
                Assert.Equal("hlx_read_evidence", next.GetProperty("tool").GetString());
                Assert.Equal(offset, next.GetProperty("arguments").GetProperty("offsetBytes").GetInt64());
            }
        }
        var result = output.ToArray();
        Assert.Equal(delivery.GetProperty("bytes").GetInt64(), result.LongLength);
        Assert.Equal(delivery.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(result)));
        return result;
    }

    public static JsonElement Payload(CallToolResult result)
    {
        Assert.False(result.IsError is true,
            result.IsError is true ? JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions) : null);
        Assert.True(result.StructuredContent.HasValue);
        var payload = result.StructuredContent!.Value;
        using var text = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(JsonElement.DeepEquals(payload, text.RootElement), "Text and structured SDK payloads must agree.");
        return payload.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _transport.DisposeAsync();
        _http.Dispose();
        _host.Dispose();
        foreach (var path in _deliveryFiles)
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".meta.json"));
        }
        if (Directory.Exists(_runtimeRoot))
            Directory.Delete(_runtimeRoot, recursive: true);
    }

    internal sealed class CaptureResponseHandler : DelegatingHandler
    {
        public CaptureResponseHandler() { }
        public CaptureResponseHandler(HttpMessageHandler inner) : base(inner) { }

        public int LastResponseBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post && response.StatusCode == HttpStatusCode.OK)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                LastResponseBytes = bytes.Length;
                var replacement = new ByteArrayContent(bytes);
                foreach (var header in response.Content.Headers)
                    replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content.Dispose();
                response.Content = replacement;
            }
            return response;
        }
    }
}
