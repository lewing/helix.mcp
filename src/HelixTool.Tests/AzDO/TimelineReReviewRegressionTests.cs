using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelixTool.Core.AzDO;
using ModelContextProtocol.Client;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class TimelineReReviewRegressionTests
{
    [Theory]
    [InlineData(1600801)]
    [InlineData(1621192)]
    public async Task ReReview2_ProductionHttpDefault_AllPagesFitEffectiveTargetWithNullablePayloadFields(int buildId)
    {
        using var factory = new TimelineAuthenticatedHttpRegressionTests.AuthenticatedTimelineFactory(buildId);
        var capture = new TimelineViewHost.CaptureResponseHandler();
        using var http = factory.CreateAuthenticatedClient("alpha", capture);
        await using var transport = new HttpClientTransport(new() { Endpoint = http.BaseAddress! }, http);
        await using var client = await McpClient.CreateAsync(transport);
        var page = TimelineViewHost.Payload(await client.CallToolAsync("azdo_timeline",
            new Dictionary<string, object?> { ["buildIdOrUrl"] = buildId.ToString() }));
        var viewId = page.GetProperty("viewId").GetString();
        HashSet<string> ids = [];
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(12288, page.GetProperty("requestedMaxResponseBytes").GetInt64());
            Assert.Equal(12288, page.GetProperty("maxResponseBytes").GetInt64());
            Assert.InRange(capture.LastResponseBytes, 1, page.GetProperty("maxResponseBytes").GetInt64());
            Assert.Equal(viewId, page.GetProperty("viewId").GetString());
            foreach (var row in page.GetProperty("records").EnumerateArray())
            {
                // Ordinary nullable fields remain in production serialization; do not
                // hide this regression by migrating the host globally to null omission.
                Assert.True(row.TryGetProperty("monitorEvidence", out _));
                Assert.True(row.TryGetProperty("parentId", out _));
                Assert.True(row.TryGetProperty("contextParentId", out _));
                Assert.True(row.TryGetProperty("log", out _));
                Assert.True(ids.Add(row.GetProperty("id").GetString()!));
            }
            if (page.GetProperty("next").ValueKind == JsonValueKind.Null)
            {
                Assert.Equal(page.GetProperty("selectedTotal").GetInt32(), ids.Count);
                return;
            }
            var action = page.GetProperty("continuation");
            page = TimelineViewHost.Payload(await client.CallToolAsync(action.GetProperty("tool").GetString()!,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(action.GetProperty("arguments").GetRawText())!));
        }
        Assert.Fail("Production HTTP timeline continuation did not terminate.");
    }

    [Theory]
    [InlineData(1600801)]
    [InlineData(1621192)]
    public async Task ReReview2_ProductionStdioDefault_FullFixturesFitEffectiveTargetOnEveryPage(int buildId)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.AssertStdioDefaultPagesAsync(TimelineViewFixture.FullReal(buildId), buildId);
    }

    [Fact]
    public async Task ReReview3_NonJsonFileDelivery_PrintsReadablePathHashAndReadAction()
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.FullReal(1621192));
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--filter", "all",
            "--projection", "full", "--expand", "none", "--delivery", "file");
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout), "Successful human file delivery must not be silent.");
        Assert.True(result.Stdout.Contains("hlx_read_evidence", StringComparison.Ordinal)
            || result.Stdout.Contains("hlx-evidence://", StringComparison.Ordinal));
        var pathMatch = Regex.Match(result.Stdout, """(?<path>(?:[A-Za-z]:\\|/)[^\r\n"']+\.bin)""");
        Assert.True(pathMatch.Success, $"No readable evidence path in: {result.Stdout}");
        var path = pathMatch.Groups["path"].Value;
        Assert.True(File.Exists(path));
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Stdout, StringComparison.OrdinalIgnoreCase);
        using var contents = JsonDocument.Parse(bytes);
        var records = contents.RootElement.TryGetProperty("results", out var results) ? results : contents.RootElement.GetProperty("records");
        Assert.Equal(1524, records.GetArrayLength());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ReReview3_IncompleteGraphFileDelivery_RemainsExitTwoUnlessExplicitlyAccepted(bool output, bool allowTruncated)
    {
        using var fixture = new TimelineViewReplayTests();
        var source = TimelineViewFixture.Real(1621192);
        source = source with { Records = source.Records.Where(r => r.Id != TimelineViewFixture.MonitorPhase).ToArray() };
        await fixture.SeedAsync(source);
        List<string> args = ["azdo", "timeline", "1621192", "--filter", "all", "--projection", "full",
            "--expand", "ancestors", "--all", "--delivery", "file", "--json"];
        if (output) args.AddRange(["--output", Path.Combine(fixture.TemporaryRoot, "incomplete.json")]);
        if (allowTruncated) args.Add("--allow-truncated");
        var result = await fixture.RunCliAsync(args.ToArray());
        using var receipt = JsonDocument.Parse(result.Stdout);
        var descriptor = receipt.RootElement.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        using var contents = JsonDocument.Parse(await File.ReadAllBytesAsync(descriptor.GetProperty("localPath").GetString()!));
        Assert.False(contents.RootElement.GetProperty("complete").GetBoolean());
        Assert.Contains(contents.RootElement.GetProperty("incompleteDetails").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "timeline_graph_invalid");
        Assert.Equal(allowTruncated ? 0 : 2, result.ExitCode);
    }

    [Fact]
    public async Task ReReview3_AutoMaterializedPartialPage_RemainsExitTwoDespiteVerifiedBytes()
    {
        using var fixture = new TimelineViewReplayTests();
        var source = TimelineViewFixture.EscapedLongNameWithSecondRow(1000);
        await fixture.SeedAsync(source);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--filter", "all",
            "--projection", "full", "--expand", "none", "--limit", "1", "--json");
        using var receipt = JsonDocument.Parse(result.Stdout);
        var descriptor = receipt.RootElement.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(descriptor.GetProperty("localPath").GetString()!));
        Assert.False(data.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(2, data.RootElement.GetProperty("selectedTotal").GetInt32());
        Assert.Equal(1, data.RootElement.GetProperty("returned").GetInt32());
        Assert.Equal(2, result.ExitCode);
    }

    [UnixSnapshotLinkFact]
    public async Task ReReview4_OutsideDirectorySymlinkIntoSnapshot_IsRefusedAndSnapshotEvidenceIsUnchanged()
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.Real(1621192));
        var alias = Path.Combine(fixture.TemporaryRoot, "outside-snapshot-link");
        Directory.CreateSymbolicLink(alias, fixture.SnapshotPath);
        var destination = Path.Combine(alias, "derived.json");
        var before = await HashSnapshotAsync(fixture.SnapshotPath);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--filter", "all",
            "--projection", "full", "--all", "--output", destination, "--json");
        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(fixture.SnapshotPath, "derived.json")));
        Assert.Equal(before.OrderBy(p => p.Key), (await HashSnapshotAsync(fixture.SnapshotPath)).OrderBy(p => p.Key));
        using var refusal = JsonDocument.Parse(result.Stdout);
        var argv = refusal.RootElement.GetProperty("recovery").GetProperty("argv").EnumerateArray()
            .Select(a => a.GetString()!).ToArray();
        var outputIndex = Array.IndexOf(argv, "--output");
        Assert.InRange(outputIndex, 1, argv.Length - 2);
        var safePath = Path.GetFullPath(argv[outputIndex + 1]);
        Assert.StartsWith("..", Path.GetRelativePath(fixture.SnapshotPath, safePath));
        Assert.StartsWith("..", Path.GetRelativePath(alias, safePath));
    }

    [Fact]
    public async Task ReReview5_FailedMonitorNameHint_PrecedesGenericFailedTaskError_InCoreAndHttp()
    {
        var source = TimelineViewFixture.EscapedLongNameWithSecondRow(1);
        source = source with
        {
            Records = [source.Records[1] with
            {
                Name = "Generic failed task", Issues = [new() { Type = "error", Message = "Bash exited with code '1'." }]
            }, source.Records[0] with
            {
                Name = "Monitor Helix Jobs", Type = "Job", Issues = []
            }]
        };
        var core = AzdoTimelineProjector.Project(source, new() { Limit = 100 });
        Assert.Equal(TimelineViewFixture.MonitorTask, core.TriageRows![0].Id);
        Assert.Equal("nameHint", core.TriageRows[0].MonitorEvidence);
        await using var host = await TimelineViewHost.StartAsync(source);
        var response = await host.TimelineAsync();
        Assert.Equal(TimelineViewFixture.MonitorTask, response.GetProperty("records")[0].GetProperty("id").GetString());
        Assert.Equal("nameHint", response.GetProperty("records")[0].GetProperty("monitorEvidence").GetString());
    }

    private static async Task<Dictionary<string, string>> HashSnapshotAsync(string root)
    {
        Dictionary<string, string> result = [];
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(root, path)] = await SnapshotEvalTestHarness.HashSharedAsync(path);
        return result;
    }

    private sealed class UnixSnapshotLinkFactAttribute : FactAttribute
    {
        public UnixSnapshotLinkFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Creating Windows directory symlinks requires an explicitly privileged test host.";
        }
    }
}
