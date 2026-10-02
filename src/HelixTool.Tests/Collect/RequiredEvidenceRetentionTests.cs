using System.Text;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.Collect;

public sealed partial class IndependentReviewRegressionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task RequiredEvidenceSurvivesOptionalDownloads_DallasGate(int concurrency)
    {
        const int consoleBytes = 600 * 1024;
        const int optionalBytes = 700 * 1024;
        using var fixture = new Fixture(maxSizeBytes: 1024 * 1024);
        fixture.Helix.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(ConsoleStream(consoleBytes)));
        var files = new[] { FileEntry("optional.bin") };
        fixture.Helix.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(files);
        fixture.Helix.GetFileAsync("optional.bin", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[optionalBytes])));

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            ExportPath = fixture.SnapshotPath,
            DownloadHelixFiles = "*.bin",
            MaxFileBytes = 1024 * 1024,
            MaxTotalBytes = 1024 * 1024,
            MaxConcurrency = concurrency
        });

        Assert.True(result.Manifest.Complete, string.Join("\n", result.Manifest.IncompleteDetails.Select(d => d.Message)));
        Assert.Equal(0, result.Manifest.ExitCode);
        Assert.All(result.Manifest.Attempts.Where(a => a.Required), a => Assert.Equal("ok", a.Outcome));
        var optional = Assert.Single(result.Manifest.Attempts, a => a.Operation == "download_helix_file");
        Assert.Equal("skipped", optional.Outcome);
        Assert.Equal("total_size_limit", optional.Skip?.Kind);
        Assert.DoesNotContain(result.Manifest.IncompleteDetails, d => d.Code == "artifact_missing");
        await using var console = await fixture.Store.GetArtifactAsync(ConsoleKey);
        Assert.NotNull(console);
        Assert.Equal(consoleBytes, console.Length);
        Assert.Equal(consoleBytes, (await fixture.Store.GetStatusAsync()).TotalSizeBytes);
        await AssertConsoleSnapshotAsync(fixture.SnapshotPath, [(WorkItem, consoleBytes)]);
    }

    [Fact]
    public async Task ConcurrentOptionalDownloads_WaitForAllRequiredEvidenceAndShareHeadroom_DallasGate()
    {
        const int consoleBytes = 300 * 1024;
        const int optionalBytes = 350 * 1024;
        const string secondWorkItem = "System.Runtime.Tests";
        using var fixture = new Fixture(maxSizeBytes: 1024 * 1024);
        fixture.Handler.HelixWorkItems = [WorkItem, secondWorkItem];
        var bothDownloadsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadsStarted = 0;
        foreach (var workItem in fixture.Handler.HelixWorkItems)
        {
            var details = Substitute.For<IWorkItemDetails>();
            details.State.Returns("Finished");
            details.ExitCode.Returns(-3);
            fixture.Helix.GetWorkItemDetailsAsync(workItem, JobId, Arg.Any<CancellationToken>()).Returns(details);
            fixture.Helix.GetConsoleLogAsync(workItem, JobId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<Stream>(ConsoleStream(consoleBytes)));
            var files = new[] { FileEntry("optional.bin") };
            fixture.Helix.ListWorkItemFilesAsync(workItem, JobId, Arg.Any<CancellationToken>())
                .Returns(files);
            fixture.Helix.GetFileAsync("optional.bin", workItem, JobId, Arg.Any<CancellationToken>())
                .Returns(call => OpenOptionalAsync(call.Arg<CancellationToken>()));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            ExportPath = fixture.SnapshotPath,
            DownloadHelixFiles = "*.bin",
            MaxFileBytes = 1024 * 1024,
            MaxTotalBytes = 1024 * 1024,
            MaxConcurrency = 6
        }, ct: timeout.Token);

        Assert.True(result.Manifest.Complete, string.Join("\n", result.Manifest.IncompleteDetails.Select(d => d.Message)));
        Assert.Equal(0, result.Manifest.ExitCode);
        Assert.Equal(2, downloadsStarted);
        Assert.All(result.Manifest.Attempts.Where(a => a.Required), a => Assert.Equal("ok", a.Outcome));
        var downloads = result.Manifest.Attempts.Where(a => a.Operation == "download_helix_file").ToList();
        var accepted = Assert.Single(downloads, a => a.Outcome == "ok");
        Assert.Equal(optionalBytes, accepted.Bytes);
        var skipped = Assert.Single(downloads, a => a.Outcome == "skipped");
        Assert.Equal("total_size_limit", skipped.Skip?.Kind);
        Assert.Equal(2 * consoleBytes + optionalBytes, (await fixture.Store.GetStatusAsync()).TotalSizeBytes);
        await AssertConsoleSnapshotAsync(fixture.SnapshotPath, [(WorkItem, consoleBytes), (secondWorkItem, consoleBytes)]);
        var options = new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var replay = new CachingHelixApiClient(new OfflineHelixApiClient(), store, options);
        await using var optional = await replay.GetFileAsync("optional.bin", accepted.Resource["workItem"]!.ToString()!, JobId);
        Assert.Equal(optionalBytes, optional.Length);

        async Task<Stream> OpenOptionalAsync(CancellationToken ct)
        {
            foreach (var requiredWorkItem in fixture.Handler.HelixWorkItems)
            {
                await using var required = await fixture.Store.GetArtifactAsync(
                    $"job:{JobId}:wi:{requiredWorkItem}:console");
                Assert.NotNull(required);
                Assert.Equal(consoleBytes, required.Length);
            }
            if (Interlocked.Increment(ref downloadsStarted) == 2)
                bothDownloadsStarted.SetResult();
            await bothDownloadsStarted.Task.WaitAsync(ct);
            return new MemoryStream(new byte[optionalBytes]);
        }
    }

    [Fact]
    public async Task OptionalDownload_ExactCacheBoundaryAndResumeDoNotDoubleCount_DallasGate()
    {
        const int consoleBytes = 600 * 1024;
        const int optionalBytes = 424 * 1024;
        using var fixture = new Fixture(maxSizeBytes: 1024 * 1024);
        fixture.Helix.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(ConsoleStream(consoleBytes)));
        var files = new[] { FileEntry("optional.bin") };
        fixture.Helix.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(files);
        fixture.Helix.GetFileAsync("optional.bin", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[optionalBytes])));
        var policy = fixture.Policy with
        {
            DownloadHelixFiles = "*.bin",
            MaxFileBytes = optionalBytes,
            MaxTotalBytes = optionalBytes,
            MaxConcurrency = 6
        };

        var first = await fixture.Collector.CollectAzdoBuildAsync(policy);
        var resumed = await fixture.Collector.CollectAzdoBuildAsync(
            policy with { Resume = true, ExportPath = fixture.SnapshotPath });

        Assert.True(first.Manifest.Complete);
        Assert.Equal("ok", Assert.Single(first.Manifest.Attempts, a => a.Operation == "download_helix_file").Outcome);
        Assert.True(resumed.Manifest.Complete, string.Join("\n", resumed.Manifest.IncompleteDetails.Select(d => d.Message)));
        Assert.Equal(0, resumed.Manifest.ExitCode);
        Assert.Equal("cached", Assert.Single(resumed.Manifest.Attempts, a => a.Operation == "download_helix_file").Outcome);
        Assert.Equal(1024 * 1024, (await fixture.Store.GetStatusAsync()).TotalSizeBytes);
        await fixture.Helix.Received(1).GetFileAsync("optional.bin", WorkItem, JobId, Arg.Any<CancellationToken>());
        await AssertConsoleSnapshotAsync(fixture.SnapshotPath, [(WorkItem, consoleBytes)]);
        var options = new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var replay = new CachingHelixApiClient(new OfflineHelixApiClient(), store, options);
        await using var optional = await replay.GetFileAsync("optional.bin", WorkItem, JobId);
        Assert.Equal(optionalBytes, optional.Length);
    }

    private static MemoryStream ConsoleStream(int bytes) => new(Encoding.UTF8.GetBytes(new string('c', bytes)));

    [Fact]
    public async Task OptionalDownload_ReservesExistingArtifactsEvenWhenTheyBecomeMostRecentlyUsed_DallasGate()
    {
        const int consoleBytes = 600 * 1024;
        const int existingBytes = 256 * 1024;
        const string existingKey = "job:11111111-2222-3333-4444-555555555555:wi:other:console";
        using var fixture = new Fixture(maxSizeBytes: 1024 * 1024);
        await fixture.Store.StartupMaintenance;
        await using (var existing = new MemoryStream(new byte[existingBytes]))
            await fixture.Store.SetArtifactAsync(existingKey, existing);
        fixture.Helix.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(ConsoleStream(consoleBytes)));
        var files = new[] { FileEntry("optional.bin") };
        fixture.Helix.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>()).Returns(files);
        fixture.Helix.GetFileAsync("optional.bin", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => OpenOptionalAsync());

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            ExportPath = fixture.SnapshotPath,
            DownloadHelixFiles = "*.bin",
            MaxFileBytes = 1024 * 1024,
            MaxTotalBytes = 1024 * 1024,
            MaxConcurrency = 6
        });

        Assert.True(result.Manifest.Complete, string.Join("\n", result.Manifest.IncompleteDetails.Select(d => d.Message)));
        Assert.Equal(0, result.Manifest.ExitCode);
        var optional = Assert.Single(result.Manifest.Attempts, a => a.Operation == "download_helix_file");
        Assert.Equal("skipped", optional.Outcome);
        Assert.Equal("total_size_limit", optional.Skip?.Kind);
        Assert.Equal(consoleBytes + existingBytes, (await fixture.Store.GetStatusAsync()).TotalSizeBytes);
        await using var retained = await fixture.Store.GetArtifactAsync(existingKey);
        Assert.NotNull(retained);
        Assert.Equal(existingBytes, retained.Length);
        await AssertConsoleSnapshotAsync(fixture.SnapshotPath, [(WorkItem, consoleBytes)]);

        async Task<Stream> OpenOptionalAsync()
        {
            await using var existing = await fixture.Store.GetArtifactAsync(existingKey);
            Assert.NotNull(existing);
            return new MemoryStream(new byte[200 * 1024]);
        }
    }

    private static async Task AssertConsoleSnapshotAsync(string snapshot, (string WorkItem, int Bytes)[] consoles)
    {
        var validation = await SnapshotValidator.ValidateAsync(snapshot);
        Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        var options = new CacheOptions { CacheRoot = snapshot, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var replay = new CachingHelixApiClient(new OfflineHelixApiClient(), store, options);
        foreach (var (workItem, bytes) in consoles)
        {
            await using var console = await replay.GetConsoleLogAsync(workItem, JobId);
            using var reader = new StreamReader(console);
            Assert.Equal(new string('c', bytes), await reader.ReadToEndAsync());
        }
    }
}
