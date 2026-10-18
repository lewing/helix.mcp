using System.Net;
using System.Text;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using HelixTool.Core.Delivery;
using HelixTool.Core.Helix;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed partial class AzdoByteDownloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotOutputGuard_ResolvesSnapshotAndNestedOutputAncestors(bool snapshotAlias)
    {
        var snapshot = Path.Combine(_root, "physical-snapshot");
        Directory.CreateDirectory(Path.Combine(snapshot, "artifacts"));
        var mid = Path.Combine(_root, "mid-link");
        var outer = Path.Combine(_root, "outer-link");
        CreateDirectoryAlias(mid, snapshot);
        CreateDirectoryAlias(outer, Path.Combine(mid, "artifacts"));
        var options = new CacheOptions { EvalMode = true, CacheRoot = snapshotAlias ? mid : snapshot };

        Assert.Throws<ArgumentException>(() => AzdoDownloadPaths.ValidateOutputOutsideSnapshot(
            Path.Combine(outer, "new-directory", "output.bin"), options));
        Assert.Throws<ArgumentException>(() => AzdoDownloadPaths.ValidateOutputOutsideSnapshot(
            Path.Combine(snapshot, "new-output.bin"), options));
        AzdoDownloadPaths.ValidateOutputOutsideSnapshot(Path.Combine(_root, "safe", "output.bin"), options);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EvidenceReadCli_RejectsCorruptBackingFileBeforePublishing(bool overwrite, bool truncated)
    {
        var evidence = _delivery.PutBytes([1, 2, 3], "application/octet-stream", null, null, "public");
        await File.WriteAllBytesAsync(evidence.LocalPath, truncated ? [1] : [3, 2, 1]);
        using var services = new ServiceCollection().AddSingleton(_options)
            .AddSingleton<IEvidenceDeliveryStore>(_delivery)
            .AddSingleton(Substitute.For<IAzdoTokenAccessor>()).BuildServiceProvider();
        var path = Path.Combine(_root, "export.bin");
        if (overwrite) await File.WriteAllBytesAsync(path, [9]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new global::AzdoDownloadCommands(services).ReadEvidence(evidence.EvidenceId, output: path, overwrite: overwrite));

        if (overwrite) Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
        else Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Theory]
    [InlineData("application/json", "{\"message\":\"requested content\"}")]
    [InlineData("text/html", "<html><body>Requested report</body></html>")]
    [InlineData("application/octet-stream", "<!doctype html><html>Requested report</html>")]
    public async Task RequestedFiles_AcceptJsonAndHtmlRegardlessOfMimeOrPrefix(string contentType, string text)
    {
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(text, Encoding.UTF8, contentType)
            };
            return response;
        }));
        var provider = new AzdoApiClient(http, Substitute.For<IAzdoTokenAccessor>());
        var artifact = _artifact with
        {
            Resource = _artifact.Resource! with { DownloadUrl = "https://artifacts.visualstudio.com/content?format=zip" }
        };
        await using var member = await provider.OpenArtifactAsync("org", "project", 42, artifact, "report.html");
        await using var attachment = await provider.OpenTestAttachmentAsync("org", "project", 8, null, 9);
        using var memberReader = new StreamReader(member.Content);
        using var attachmentReader = new StreamReader(attachment.Content);
        Assert.Equal(text, await memberReader.ReadToEndAsync());
        Assert.Equal(text, await attachmentReader.ReadToEndAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation)]
    public async Task RequestedFiles_StillRejectProviderAccessErrors(HttpStatusCode status)
    {
        using var http = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html")
        }));
        var provider = new AzdoApiClient(http, Substitute.For<IAzdoTokenAccessor>());
        await Assert.ThrowsAsync<HelixTool.Core.Acquisition.HlxAcquisitionException>(
            () => provider.OpenTestAttachmentAsync("org", "project", 8, null, 9));
    }

    [Theory]
    [InlineData("*ab", "*b")]
    [InlineData("a/*ab", "**/*b")]
    [InlineData("*a*b", "*b")]
    public void MemberGlob_HandlesLiteralStarsInNames(string member, string pattern)
        => Assert.True(AzdoDownloadPaths.MatchesMember(member, pattern));

    [Theory]
    [InlineData("age")]
    [InlineData("lru")]
    [InlineData("missing")]
    public async Task BytePairs_SurviveDescriptorExpiry_AndAreRemovedTogether(string removal)
    {
        var options = new CacheOptions { CacheRoot = Path.Combine(_root, "pair-cache"), MaxSizeBytes = 3 };
        using var store = new SqliteCacheStore(options);
        await store.StartupMaintenance;
        const string key = "azdo:org:project:artifactbytes:v1:42:7:zip";
        const string orphan = "azdo:org:project:artifactbytes:v1:42:8:zip";
        await store.SetArtifactAsync(key, new MemoryStream([1, 2, 3]));
        await store.SetMetadataAsync(key, "descriptor", TimeSpan.FromHours(-1));
        await store.SetMetadataAsync(orphan, "orphan", TimeSpan.FromHours(-1));
        await store.EvictExpiredAsync();
        Assert.Null(await store.GetMetadataIgnoringTtlAsync(orphan));
        Assert.Equal("descriptor", await store.GetMetadataAsync(key));
        await using (var blob = await store.GetArtifactAsync(key)) Assert.NotNull(blob);
        var files = Directory.GetFiles(Path.Combine(options.GetEffectiveCacheRoot(), "artifacts"), "*", SearchOption.AllDirectories);
        var file = Assert.Single(files);
        Assert.Equal(64, Path.GetFileName(file).Length);
        Assert.All(Path.GetRelativePath(Path.Combine(options.GetEffectiveCacheRoot(), "artifacts"), file).Split(Path.DirectorySeparatorChar),
            segment => Assert.DoesNotContain(':', segment));

        if (removal == "lru")
        {
            await store.SetArtifactAsync("job:other:file", new MemoryStream([4]));
        }
        else if (removal == "age")
        {
            using var connection = CacheTestCleanup.CreatePooledConnection(
                $"Data Source={Path.Combine(options.GetEffectiveCacheRoot(), "cache.db")};Cache=Private");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cache_artifacts SET last_accessed = '2000-01-01T00:00:00.0000000+00:00';";
            command.ExecuteNonQuery();
            await store.EvictExpiredAsync();
        }
        else
        {
            File.Delete(file);
            Assert.Null(await store.GetArtifactAsync(key));
        }
        Assert.Null(await store.GetMetadataIgnoringTtlAsync(key));
        Assert.Null(await store.GetArtifactAsync(key));
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("AUTO", "AUTO", false)]
    [InlineData("SELECTED", "MEMBERS", true)]
    [InlineData("AUTO", "MEMBERS", true)]
    [InlineData("SELECTED", "AUTO", true)]
    public async Task Collector_ByteRoutingIsCaseInsensitive(string scope, string mode, bool selected)
    {
        ConfigureCollector();
        var zip = Zip(("drop/Build.binlog", "evidence"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));
        using var http = new HttpClient();
        var collector = CreateCollector(http);

        var result = await collector.CollectAzdoBuildAsync(BytePolicy() with
        {
            ArtifactByteScope = scope, ArtifactByteMode = mode,
            DownloadAzdoArtifacts = selected ? "drop" : null
        });

        Assert.True(result.Manifest.Complete);
        var downloads = result.Manifest.ByteDownloads ?? [];
        if (selected)
        {
            var resource = Assert.Single(Assert.Single(downloads).Results);
            Assert.Equal(mode == "AUTO" ? "archive" : "members", resource.Format);
            Assert.Equal(mode == "AUTO" ? 0 : 1, resource.Members.Count);
        }
        else Assert.Empty(downloads);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    public async Task Collector_DoesNotReplenishExhaustedBudgetAcrossScopes(long budget, bool streamed, bool allowLarge)
    {
        ConfigureCollector();
        _client.GetTestRunsAsync("dnceng-public", "public", 42, int.MaxValue, Arg.Any<CancellationToken>())
            .Returns(new[] { new AzdoTestRun { Id = 8, State = "completed" }, new AzdoTestRun { Id = 9, State = "completed" } });
        foreach (var id in new[] { 8, 9 })
        {
            _client.GetRunTestAttachmentsAsync("dnceng-public", "public", id, int.MaxValue, Arg.Any<CancellationToken>())
                .Returns(new[] { new AzdoTestAttachment { Id = 1, FileName = "one.bin" } });
            _client.OpenTestAttachmentAsync("dnceng-public", "public", id, null, 1, Arg.Any<CancellationToken>())
                .Returns(_ => new AzdoByteStream(new MemoryStream([1]), "application/octet-stream", 1));
        }
        using var http = new HttpClient();
        var collector = CreateCollector(http);
        var policy = BytePolicy() with
        {
            DownloadTestAttachments = "*.bin", AllowLarge = allowLarge,
            MaxTotalBytes = streamed ? 100 : budget, MaxTotalStreamBytes = streamed ? budget : 100
        };
        var result = await collector.CollectAzdoBuildAsync(policy);

        var reports = result.Manifest.ByteDownloads!;
        Assert.Equal(allowLarge ? 2 : budget, reports.Sum(r => r.Bytes));
        Assert.Equal(allowLarge ? 2 : budget, reports.Sum(r => r.StreamedBytes));
        Assert.Equal(allowLarge ? 0 : 2 - budget,
            result.Manifest.Attempts.Count(a => a.Skip?.Kind == "planning_deferred"));
        Assert.Equal(allowLarge, result.Manifest.Complete);
    }

    [Fact]
    public async Task CachedBytes_StillRespectZeroRetainedBudget()
    {
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1]), "application/octet-stream", 1));
        var selection = new AzdoDownloadOptions { ArtifactName = "drop", MemberPath = "Build.binlog" };
        Assert.True((await _service.DownloadArtifactsAsync("42", selection)).Complete);
        var deferred = await _service.DownloadArtifactsAsync("42", selection with { MaxTotalBytes = 0 });
        Assert.Equal("planning_deferred", Assert.Single(deferred.Results).Skip);
        Assert.Equal(0, deferred.Bytes);
        var allowed = await _service.DownloadArtifactsAsync("42", selection with { MaxTotalBytes = 0, AllowLarge = true });
        Assert.True(allowed.Complete);
        Assert.Equal(1, allowed.Bytes);
        Assert.Equal(0, allowed.StreamedBytes);
    }

    private void ConfigureCollector()
    {
        _client.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });
        _client.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records = [new() { Id = "job", Type = "Job", State = "completed", Result = "succeeded" }]
            });
    }

    private static void CreateDirectoryAlias(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }
        // Junctions exercise Windows reparse-point resolution without symlink privileges.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stdout + stderr);
    }

    private CollectPolicy BytePolicy() => new()
    {
        BuildIdOrUrl = "42", ManifestPath = Path.Combine(_root, "review-manifest.json"),
        TestScope = "none", LogScope = "none", HelixScope = "none"
    };

    private AzdoBuildCollector CreateCollector(HttpClient http)
    {
        var cached = new CachingAzdoApiClient(_client, _cache, _options);
        var helix = Substitute.For<IHelixApiClient>();
        var token = Substitute.For<IAzdoTokenAccessor>();
        token.AuthStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new AzdoAuthStatus { IsAuthenticated = false, Source = "anonymous", Path = "anonymous" });
        return new AzdoBuildCollector(new AzdoService(cached), cached, new HelixService(helix, http), helix,
            token, Substitute.For<IHelixTokenAccessor>(), _options, _cache);
    }
}
