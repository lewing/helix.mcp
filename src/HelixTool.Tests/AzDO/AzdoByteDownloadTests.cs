using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using HelixTool.Mcp.Tools;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class AzdoByteDownloadTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-runtime", $"azdo-bytes-{Guid.NewGuid():N}"));
    private readonly IAzdoApiClient _client = Substitute.For<IAzdoApiClient>();
    private readonly CacheOptions _options;
    private readonly SqliteCacheStore _cache;
    private readonly FileEvidenceDeliveryStore _delivery;
    private readonly AzdoDownloadService _service;
    private readonly AzdoBuildArtifact _artifact = new()
    {
        Id = 7,
        Name = "drop",
        Resource = new AzdoArtifactResource { Type = "PipelineArtifact", Data = "immutable-ref" }
    };

    public AzdoByteDownloadTests()
    {
        _options = new CacheOptions { CacheRoot = _root, MaxSizeBytes = 1 };
        _cache = new SqliteCacheStore(_options);
        _delivery = new FileEvidenceDeliveryStore(_options);
        _service = new AzdoDownloadService(_client, _cache, _options, _delivery);
        _client.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new[] { _artifact });
    }

    [Fact]
    public async Task ExactMember_UsesNativeTransferAndPublishesVerifiedPairBeyondCacheTarget()
    {
        var bytes = Encoding.UTF8.GetBytes("native member evidence");
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Release/Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(bytes), "application/octet-stream", bytes.Length));
        var report = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPath = "Release/Build.binlog" });
        Assert.True(report.Complete);
        var result = Assert.Single(report.Results);
        var member = Assert.Single(result.Members);
        Assert.Equal("pipeline_file", result.Source!.TransferMode);
        Assert.Null(result.Source.ArchiveSha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(member.LocalPath!));
        Assert.NotNull(await _cache.GetMetadataAsync(member.CacheKey));
        await using var cached = await _cache.GetArtifactAsync(member.CacheKey);
        Assert.NotNull(cached);
        Assert.Equal(bytes.Length, cached.Length);
        Assert.Equal(bytes.Length, report.StreamedBytes);
        Assert.Equal(bytes.Length, report.Bytes);
        await _client.DidNotReceive().OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>());
        var replay = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPath = "Release/Build.binlog" });
        Assert.Equal(0, replay.StreamedBytes);
        Assert.Equal("cached", Assert.Single(replay.Results).Outcome);
    }

    [Fact]
    public async Task Glob_StripsOneWrapperAndRetainsOnlySelectedDecodedMembers()
    {
        var zip = Zip(("drop/Build.binlog", "root"), ("drop/Release/Build.binlog", "nested"), ("drop/Restore.binlog", "unselected"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));
        var report = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPattern = "**/Build.binlog" });
        Assert.True(report.Complete);
        var result = Assert.Single(report.Results);
        Assert.Equal(new[] { "Build.binlog", "Release/Build.binlog" }, result.Members.Select(m => m.MemberPath));
        Assert.Equal(zip.Length, report.StreamedBytes);
        Assert.Equal(10, report.Bytes);
        Assert.Equal(zip.Length, result.Source!.ArchiveBytes);
        Assert.Null(await _cache.GetArtifactAsync("azdo:dnceng-public:public:artifactbytes:v1:42:7:zip"));
        Assert.NotNull(await _cache.GetMetadataAsync("azdo:dnceng-public:public:artifactmembers:v1:42:7:all"));
        var narrow = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPattern = "*/Build.binlog" });
        Assert.Equal("Release/Build.binlog", Assert.Single(Assert.Single(narrow.Results).Members).MemberPath);
        Assert.Equal(0, narrow.StreamedBytes);
        var broader = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPattern = "**/*.binlog" });
        Assert.Equal(broader.Results.SelectMany(r => r.Members).Sum(m => m.Bytes), broader.Bytes);
    }

    [Fact]
    public async Task RawRunAttachment_PreservesActualContentTypeAndBinaryFormat()
    {
        _client.GetRunTestAttachmentsAsync("org", "project", 8, int.MaxValue, Arg.Any<CancellationToken>())
            .Returns(new[] { new AzdoTestAttachment { Id = 9, FileName = "crash.dmp" } });
        _client.OpenTestAttachmentAsync("org", "project", 8, null, 9, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));
        var report = await _service.DownloadTestAttachmentsAsync(8, null, "org", "project", new() { AttachmentId = 9 });
        Assert.True(report.Complete);
        var result = Assert.Single(report.Results);
        Assert.Equal("run", result.Scope);
        Assert.Equal("binary", result.Format);
        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Null(result.Source!.ArchiveBytes);
    }

    [Fact]
    public async Task MemberWindows_CarryAnExactStableResourceViewAndFullReport()
    {
        var zip = Zip(("drop/A.binlog", "one"), ("drop/B.binlog", "two"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPattern = "*.binlog", MemberLimit = 1 };
        var first = await _service.DownloadArtifactsAsync("42", options);
        Assert.False(first.Complete);
        var item = Assert.Single(first.Results);
        Assert.True(item.MembersTruncated);
        Assert.Equal(2, item.MembersTotal);
        Assert.NotNull(item.MemberNext);
        var second = await _service.DownloadArtifactsAsync("42", options with
        {
            MemberOffset = 1,
            ViewId = item.MemberNext.Arguments["viewId"]!.ToString()
        });
        Assert.Equal("B.binlog", Assert.Single(Assert.Single(second.Results).Members).MemberPath);
        Assert.False(second.Complete);
        Assert.NotNull(second.Continuation);
        using var full = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(second.Delivery!.LocalPath));
        Assert.Equal(2, full.RootElement.GetProperty("results")[0].GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task Snapshot_ReplaysOnlySelectedMembersOnCurrentHostWithoutNetwork()
    {
        var zip = Zip(("drop/Build.binlog", "selected"), ("drop/Restore.binlog", "not selected"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));
        var cached = new CachingAzdoApiClient(_client, _cache, _options);
        await cached.GetBuildArtifactsAsync("dnceng-public", "public", 42);
        var selected = new AzdoDownloadOptions { ArtifactName = "drop", MemberPattern = "**/Build.binlog" };
        await _service.DownloadArtifactsAsync("42", selected);
        var snapshot = Path.Combine(_root, "snapshot");
        await SnapshotExporter.ExportAsync(_options.GetEffectiveCacheRoot(), snapshot);
        var before = await File.ReadAllBytesAsync(Path.Combine(snapshot, "cache.db"));
        var evalOptions = new CacheOptions { CacheRoot = snapshot, EvalMode = true };
        using var offlineStore = new SqliteCacheStore(evalOptions);
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), offlineStore, evalOptions);
        var delivery = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = Path.Combine(_root, "current-host-delivery") });
        var replay = new AzdoDownloadService(offline, offlineStore, evalOptions, delivery);
        var available = await replay.DownloadArtifactsAsync("42", selected);
        Assert.True(available.Complete);
        Assert.Equal(0, available.StreamedBytes);
        Assert.Contains("current-host-delivery", Assert.Single(Assert.Single(available.Results).Members).LocalPath);
        var absent = await replay.DownloadArtifactsAsync("42", selected with { MemberPattern = "**/*.binlog" });
        Assert.False(absent.Complete);
        Assert.Equal(HelixTool.Core.Acquisition.AcquisitionErrorKind.NotInSnapshot, Assert.Single(absent.Results).Error!.Kind);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(snapshot, "cache.db")));
    }

    [Fact]
    public async Task SmallTarget_SpoolsReportAndReaderCanReadBinaryChunks()
    {
        _options.AuthTokenHash = "12345678";
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3, 4]), "application/octet-stream", 4));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPath = "Build.binlog", Delivery = "file", MaxResponseBytes = 1 };
        var report = await _service.DownloadArtifactsAsync("42", options);
        var binary = Assert.Single(Assert.Single(report.Results).Members).Delivery!;
        var reader = new HlxEvidenceMcpTools(_delivery, _options);
        var range = await reader.ReadEvidence(binary.EvidenceId, 1, 2, "base64");
        Assert.Equal("AgM=", range.Data);
        var shaped = AzdoDownloadMcpTools.Shape(report, options);
        Assert.Empty(shaped.Results);
        Assert.NotNull(shaped.Continuation);
        Assert.True(McpPresentationBudget.MeasureAny(shaped) <= 8192 - 256);
    }

    [Fact]
    public async Task ExplicitPlanningThreshold_ProducesSameScopeAllowLargeRecovery()
    {
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPath = "Build.binlog", MaxFileBytes = 1 };
        var report = await _service.DownloadArtifactsAsync("42", options);
        Assert.False(report.Complete);
        Assert.Equal("planning_deferred", Assert.Single(report.Results).Skip);
        Assert.Contains("allowLarge", System.Text.Json.JsonSerializer.Serialize(report.Recovery));
        var allowed = await _service.DownloadArtifactsAsync("42", options with { AllowLarge = true });
        Assert.True(allowed.Complete);
    }

    [Fact]
    public async Task DeclinedCachePublication_PreservesVerifiedReadableDeliveryWithoutClaimingCached()
    {
        var declined = Substitute.For<ICacheStore>();
        declined.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        declined.GetArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Stream?)null);
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));
        var service = new AzdoDownloadService(_client, declined, _options, _delivery);
        var report = await service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPath = "Build.binlog" });
        Assert.False(report.Complete);
        var result = Assert.Single(report.Results);
        Assert.Equal("failed", result.Outcome);
        Assert.NotNull(result.Delivery);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.Delivery.LocalPath));
        Assert.Equal(3, report.Bytes);
    }

    [Fact]
    public async Task InterruptedBody_PreservesPrefixReceiptThenRestartsOnlyThatFile()
    {
        var calls = 0;
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(++calls == 1 ? new InterruptedStream() : new MemoryStream([1, 2, 3, 4]),
                "application/octet-stream", 4));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPath = "Build.binlog" };
        var interrupted = await _service.DownloadArtifactsAsync("42", options);
        Assert.False(interrupted.Complete);
        Assert.Equal(2, interrupted.StreamedBytes);
        var receipt = Assert.Single(Directory.GetFiles(_root, "transfer-*.partial.json", SearchOption.AllDirectories));
        Assert.Contains("\"complete\":false", await File.ReadAllTextAsync(receipt));
        var resumed = await _service.DownloadArtifactsAsync("42", options);
        Assert.True(resumed.Complete);
        Assert.Equal(4, resumed.StreamedBytes);
        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFiles(_root, "transfer-*.partial*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../Build.binlog")]
    [InlineData("/Release/Build.binlog")]
    [InlineData("C:/Build.binlog")]
    [InlineData("%2e%2e/Build.binlog")]
    public void UnsafePaths_AreRejected(string path)
        => Assert.Throws<ArgumentException>(() => AzdoDownloadPaths.NormalizeMember(path));

    [Fact]
    public async Task NativeProvider_NormalizesLeadingSlashWithoutForwardingAuthToArtifactHost()
    {
        Uri? requested = null;
        var handler = new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        using var http = new HttpClient(handler);
        var accessor = Substitute.For<IAzdoTokenAccessor>();
        accessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(new AzdoCredential("secret", "Bearer", "test"));
        var provider = new AzdoApiClient(http, accessor);
        await using var stream = await provider.OpenArtifactAsync("org", "project", 42,
            _artifact with { Resource = _artifact.Resource! with { DownloadUrl = "https://artifacts.visualstudio.com/_apis/artifact/opaque/content?format=zip&api-version=7.2-preview.1" } }, "Release/Build.binlog");
        Assert.Contains("format=file", requested!.Query);
        Assert.Equal("/Release/Build.binlog", System.Web.HttpUtility.ParseQueryString(requested.Query)["subPath"]);
    }

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        return stream.ToArray();
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class InterruptedStream() : MemoryStream([1, 2, 3, 4])
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) return ValueTask.FromException<int>(new HttpRequestException("simulated interrupted provider body"));
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
