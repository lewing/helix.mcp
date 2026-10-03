using System.Security.Cryptography;
using System.Text.Json;
using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineThirdReviewRegressionTests
{
    [UnixAncestorLinkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThirdReview1_SymlinkedAncestorAboveExistingDirectoryOrFile_IsRefusedWithoutSnapshotMutation(bool existingFile)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.Real(1621192));
        var artifacts = Path.Combine(fixture.SnapshotPath, "artifacts");
        Directory.CreateDirectory(artifacts);
        var fileName = existingFile ? "existing.json" : "nested-derived.json";
        var physical = Path.Combine(artifacts, fileName);
        if (existingFile)
            await File.WriteAllTextAsync(physical, """{"original":"immutable snapshot evidence"}""");
        var outside = Path.Combine(fixture.TemporaryRoot, "outside-link");
        Directory.CreateSymbolicLink(outside, fixture.SnapshotPath);
        var destination = Path.Combine(outside, "artifacts", fileName);
        var before = await HashSnapshotAsync(fixture.SnapshotPath);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--filter", "all",
            "--projection", "full", "--all", "--output", destination, "--json");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(before.OrderBy(p => p.Key), (await HashSnapshotAsync(fixture.SnapshotPath)).OrderBy(p => p.Key));
        if (existingFile)
            Assert.Equal("""{"original":"immutable snapshot evidence"}""", await File.ReadAllTextAsync(physical));
        else
            Assert.False(File.Exists(physical));
        using var refusal = JsonDocument.Parse(result.Stdout);
        var argv = refusal.RootElement.GetProperty("recovery").GetProperty("argv").EnumerateArray()
            .Select(a => a.GetString()!).ToArray();
        var outputIndex = Array.IndexOf(argv, "--output");
        Assert.InRange(outputIndex, 1, argv.Length - 2);
        var recovered = await fixture.RunCliAsync(argv.Skip(1).ToArray());
        var safePath = argv[outputIndex + 1];
        try
        {
            Assert.Equal(0, recovered.ExitCode);
            Assert.True(File.Exists(safePath));
            Assert.Equal(before.OrderBy(p => p.Key), (await HashSnapshotAsync(fixture.SnapshotPath)).OrderBy(p => p.Key));
        }
        finally
        {
            File.Delete(safePath);
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(1048576)]
    public async Task ThirdReview2_ExactCompleteSuccessfulRecord_StaysExitZeroWhenOnlyDeliveryMechanismChanges(int chars)
    {
        using var fixture = new TimelineViewReplayTests();
        var message = new string('x', chars);
        var source = TimelineViewFixture.FullIssueLookup();
        source = TimelineViewFixture.WithIssues(source, TimelineViewFixture.SyntheticId(5),
            new AzdoIssue { Type = "warning", Message = message });
        await fixture.SeedAsync(source);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.SyntheticId(5),
            "--projection", "full", "--expand", "none", "--json");
        using var response = JsonDocument.Parse(result.Stdout);
        var root = response.RootElement;
        var data = root;
        JsonDocument? backing = null;
        try
        {
            if (chars > 12288)
            {
                var delivery = root.GetProperty("delivery");
                Assert.True(delivery.GetProperty("complete").GetBoolean());
                Assert.False(root.GetProperty("complete").GetBoolean());
                var bytes = await File.ReadAllBytesAsync(delivery.GetProperty("localPath").GetString()!);
                Assert.Equal(delivery.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                backing = JsonDocument.Parse(bytes);
                data = backing.RootElement;
            }
            Assert.True(data.GetProperty("complete").GetBoolean());
            Assert.False(data.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, data.GetProperty("selectedTotal").GetInt32());
            Assert.Equal(1, data.GetProperty("returned").GetInt32());
            Assert.True(data.GetProperty("issueComplete").GetBoolean());
            Assert.Equal(1, data.GetProperty("issueTotal").GetInt32());
            var rows = data.TryGetProperty("results", out var results) ? results : data.GetProperty("records");
            Assert.Equal(message, Assert.Single(rows.EnumerateArray()).GetProperty("issues")[0].GetProperty("message").GetString());
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            backing?.Dispose();
        }
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("inline")]
    [InlineData("file")]
    [InlineData("chunked")]
    public async Task ThirdReview2_PartialIssueWindow_RemainsExitTwoDespiteVerifiedFileInEveryDeliveryMode(string delivery)
    {
        using var fixture = new TimelineViewReplayTests();
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.Real(1621192), TimelineViewFixture.MonitorTask,
            new() { Type = "error", Message = new string('x', 1024 * 1024) },
            new() { Type = "warning", Message = "second raw issue remains outside the output window" });
        await fixture.SeedAsync(source);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.MonitorTask,
            "--projection", "full", "--expand", "none", "--issue-limit", "1", "--delivery", delivery, "--json");
        using var receipt = JsonDocument.Parse(result.Stdout);
        var descriptor = receipt.RootElement.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        using var contents = JsonDocument.Parse(await File.ReadAllBytesAsync(descriptor.GetProperty("localPath").GetString()!));
        Assert.Equal(1, contents.RootElement.GetProperty("selectedTotal").GetInt32());
        Assert.False(contents.RootElement.GetProperty("complete").GetBoolean());
        Assert.False(contents.RootElement.GetProperty("issueComplete").GetBoolean());
        Assert.Equal(2, contents.RootElement.GetProperty("issueTotal").GetInt32());
        Assert.Equal(1, contents.RootElement.GetProperty("issueReturned").GetInt32());
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("inline")]
    public async Task ThirdReview2_PartialRecordPage_RemainsExitTwoForAutoOrInlineMaterialization(string delivery)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.EscapedLongNameWithSecondRow(1000));
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--filter", "all",
            "--projection", "full", "--expand", "none", "--limit", "1", "--delivery", delivery, "--json");
        using var receipt = JsonDocument.Parse(result.Stdout);
        var descriptor = receipt.RootElement.GetProperty("delivery");
        Assert.True(descriptor.GetProperty("complete").GetBoolean());
        using var contents = JsonDocument.Parse(await File.ReadAllBytesAsync(descriptor.GetProperty("localPath").GetString()!));
        Assert.Equal(2, contents.RootElement.GetProperty("selectedTotal").GetInt32());
        Assert.Equal(1, contents.RootElement.GetProperty("returned").GetInt32());
        Assert.False(contents.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(2, result.ExitCode);
    }

    private static async Task<Dictionary<string, string>> HashSnapshotAsync(string root)
    {
        Dictionary<string, string> hashes = [];
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            hashes[Path.GetRelativePath(root, path)] = await SnapshotEvalTestHarness.HashSharedAsync(path);
        return hashes;
    }

    private sealed class UnixAncestorLinkTheoryAttribute : TheoryAttribute
    {
        public UnixAncestorLinkTheoryAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Creating Windows directory symlinks requires an explicitly privileged test host.";
        }
    }
}
