using System.Text.Json;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineDallasCliRegressionTests
{
    [Theory]
    [InlineData(true, "auto")]
    [InlineData(false, "file")]
    [InlineData(false, "chunked")]
    public async Task Dallas3_CliDeliveryFlagAndAll_ReturnComplete1524RecordsWithSmallLimit(bool all, string delivery)
    {
        using var fixture = new TimelineViewReplayTests();
        var source = TimelineViewFixture.FullReal(1621192);
        await fixture.SeedAsync(source);
        var args = new List<string>
        {
            "azdo", "timeline", "1621192", "--filter", "all", "--projection", "full",
            "--expand", "none", "--limit", "1", "--delivery", delivery, "--json"
        };
        if (all) args.Add("--all");
        var result = await fixture.RunCliAsync(args.ToArray());
        Assert.True(result.ExitCode == 0, $"Exit={result.ExitCode}; stderr={result.Stderr}; stdout={result.Stdout[..Math.Min(1500, result.Stdout.Length)]}");
        using var receipt = JsonDocument.Parse(result.Stdout);
        var descriptor = receipt.RootElement.GetProperty("delivery");
        var path = descriptor.GetProperty("localPath").GetString()!;
        try
        {
            Assert.True(descriptor.GetProperty("complete").GetBoolean());
            using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            var root = data.RootElement;
            var records = root.TryGetProperty("results", out var results) ? results : root.GetProperty("records");
            Assert.Equal(1524, records.GetArrayLength());
            Assert.Equal(source.Records.Select(r => r.Id), records.EnumerateArray().Select(r => r.GetProperty("id").GetString()));
            Assert.True(root.GetProperty("complete").GetBoolean());
            Assert.False(root.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".meta.json"));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Dallas7_CliUnboundedNonzeroIssueOffset_IsIncompleteAndCarriesRecovery(int offset)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.Real(1621192));
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.MonitorTask,
            "--projection", "full", "--expand", "none", "--issue-offset", offset.ToString(), "--json");
        Assert.Equal(2, result.ExitCode);
        using var data = JsonDocument.Parse(result.Stdout);
        var root = data.RootElement;
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.False(root.GetProperty("issueComplete").GetBoolean());
        Assert.True(root.GetProperty("issueTruncated").GetBoolean());
        Assert.Equal(3, root.GetProperty("issueTotal").GetInt32());
        Assert.True(root.TryGetProperty("continuation", out _) || root.TryGetProperty("recovery", out _));
    }

    public static IEnumerable<object[]> InvalidEnums()
    {
        foreach (var flag in new[] { "--projection", "--expand", "--result", "--state", "--delivery" })
        {
            yield return [flag, false];
            yield return [flag, true];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidEnums))]
    public async Task Dallas8_CliInvalidEnums_AreErrorsEvenWithEmptyTimeline(string flag, bool empty)
    {
        using var fixture = new TimelineViewReplayTests();
        var source = TimelineViewFixture.Real(1621192);
        if (empty) source = source with { Records = [] };
        await fixture.SeedAsync(source);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", flag, "bogus", "--json");
        Assert.True(result.ExitCode == 1, $"Invalid {flag} returned {result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr) && string.IsNullOrWhiteSpace(result.Stdout));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Dallas8_CliInvalidIssueLimit_IsError_NotNonadvancingContinuation(int limit)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.Real(1621192));
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.MonitorTask,
            "--projection", "full", "--expand", "none", "--issue-limit", limit.ToString(), "--json");
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("derived-record.json")]
    [InlineData("cache.db")]
    public async Task Dallas9_CliOutputInsideEvalSnapshot_IsRefusedWithoutMutation_AndOutsideRecoveryExecutes(string fileName)
    {
        using var fixture = new TimelineViewReplayTests();
        await fixture.SeedAsync(TimelineViewFixture.Real(1621192));
        var before = await CaptureSnapshotAsync(fixture.SnapshotPath);
        var unsafeOutput = Path.Combine(fixture.SnapshotPath, fileName);
        var result = await fixture.RunCliAsync("azdo", "timeline", "1621192", "--filter", "all",
            "--projection", "full", "--all", "--output", unsafeOutput, "--json");
        Assert.Equal(1, result.ExitCode);
        if (fileName != "cache.db")
            Assert.False(File.Exists(unsafeOutput));
        Assert.Equal(before.OrderBy(p => p.Key), (await CaptureSnapshotAsync(fixture.SnapshotPath)).OrderBy(p => p.Key));
        using var response = JsonDocument.Parse(result.Stdout);
        var recovery = response.RootElement.GetProperty("recovery");
        var argv = recovery.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Equal("hlx", Path.GetFileNameWithoutExtension(argv[0]));
        var outputIndex = Array.IndexOf(argv, "--output");
        Assert.InRange(outputIndex, 1, argv.Length - 2);
        var safeOutput = Path.GetFullPath(argv[outputIndex + 1]);
        Assert.StartsWith("..", Path.GetRelativePath(fixture.SnapshotPath, safeOutput));
        Assert.DoesNotContain(argv, a => a.Contains('<') || a.Contains('>'));
        var retried = await fixture.RunCliAsync(argv.Skip(1).ToArray());
        try
        {
            Assert.Equal(0, retried.ExitCode);
            Assert.True(File.Exists(safeOutput));
            using var contents = JsonDocument.Parse(await File.ReadAllBytesAsync(safeOutput));
            var records = contents.RootElement.TryGetProperty("results", out var rows) ? rows : contents.RootElement.GetProperty("records");
            Assert.Equal(4, records.GetArrayLength());
            Assert.Equal(before.OrderBy(p => p.Key), (await CaptureSnapshotAsync(fixture.SnapshotPath)).OrderBy(p => p.Key));
        }
        finally
        {
            File.Delete(safeOutput);
        }
    }

    private static async Task<Dictionary<string, string>> CaptureSnapshotAsync(string directory)
    {
        Dictionary<string, string> files = [];
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(directory, path)] = await SnapshotEvalTestHarness.HashSharedAsync(path);
        return files;
    }
}
