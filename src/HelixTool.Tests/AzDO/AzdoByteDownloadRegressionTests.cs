using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed partial class AzdoByteDownloadTests
{
    [Fact]
    public async Task AllMembers_WithoutMemberSelector_PreservesWholeArchiveAndItsHash()
    {
        var zip = Zip(("drop/Build.binlog", "one"), ("drop/other.txt", "two"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));

        var report = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", AllMembers = true });

        Assert.True(report.Complete);
        var result = Assert.Single(report.Results);
        Assert.Equal("archive", result.Format);
        Assert.Empty(result.Members);
        Assert.Equal(zip, await File.ReadAllBytesAsync(result.LocalPath!));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(zip)), result.Sha256);
        Assert.Equal(result.Sha256, result.Source!.ArchiveSha256);
        Assert.Equal(zip.Length, result.Source.ArchiveBytes);
        await using var blob = await _cache.GetArtifactAsync(result.CacheKey!);
        Assert.NotNull(blob);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task AllMembers_WithoutMemberSelector_PreservesRawAttachment(int? resultId)
    {
        var attachments = new[] { new AzdoTestAttachment { Id = 9, FileName = "crash.dmp" } };
        if (resultId is int id)
            _client.GetTestAttachmentsAsync("org", "project", 8, id, int.MaxValue, Arg.Any<CancellationToken>()).Returns(attachments);
        else
            _client.GetRunTestAttachmentsAsync("org", "project", 8, int.MaxValue, Arg.Any<CancellationToken>()).Returns(attachments);
        _client.OpenTestAttachmentAsync("org", "project", 8, resultId, 9, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));

        var report = await _service.DownloadTestAttachmentsAsync(8, resultId, "org", "project",
            new() { AttachmentId = 9, AllMembers = true });

        Assert.True(report.Complete);
        var result = Assert.Single(report.Results);
        Assert.Equal("binary", result.Format);
        Assert.Empty(result.Members);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.LocalPath!));
        Assert.Equal(resultId is null ? "run" : "result", result.Scope);
    }

    [Fact]
    public async Task UnknownLengthArchive_EnforcesRetainedTotalAndAllowLargeResumesOriginalArchive()
    {
        var zip = Zip(("drop/Build.binlog", "evidence"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip"));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MaxTotalBytes = 1 };

        var deferred = await _service.DownloadArtifactsAsync("42", options);

        Assert.False(deferred.Complete);
        Assert.Equal("planning_deferred", Assert.Single(deferred.Results).Skip);
        Assert.Equal(zip.Length, deferred.StreamedBytes);
        Assert.Equal(0, deferred.Bytes);
        var resumed = await _service.DownloadArtifactsAsync("42", options with { AllowLarge = true });
        Assert.True(resumed.Complete);
        Assert.Equal("archive", Assert.Single(resumed.Results).Format);
        Assert.Equal(zip.Length, resumed.Bytes);
    }

    [Fact]
    public async Task Recovery_DropsObservationCursorsAndPreservesSelection()
    {
        var zip = Zip(("drop/A.binlog", "one"), ("drop/B.binlog", "two"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPattern = "*.binlog", MaxFileBytes = 1, MemberLimit = 1 };
        var first = await _service.DownloadArtifactsAsync("42", options);
        var pagedFailure = await _service.DownloadArtifactsAsync("42", options with { Offset = 1, ViewId = first.ViewId });
        var recovery = JsonSerializer.SerializeToElement(pagedFailure.Recovery, AzdoDownloadService.JsonOptions);
        var arguments = recovery.GetProperty("arguments");
        var retry = arguments.Deserialize<AzdoDownloadOptions>(AzdoDownloadService.JsonOptions)!;

        Assert.Null(retry.ViewId);
        Assert.Equal(0, retry.Offset);
        Assert.Equal(0, retry.MemberOffset);
        Assert.True(retry.AllowLarge);
        Assert.Equal(options.ArtifactName, retry.ArtifactName);
        Assert.Equal(options.MemberPattern, retry.MemberPattern);
        var completed = await _service.DownloadArtifactsAsync("42", retry);
        Assert.True(completed.Complete);
        Assert.Equal(2, Assert.Single(completed.Results).Members.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_ReacquiresCorruptBlobOrDescriptorInLiveMode(bool corruptDescriptor)
    {
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPath = "Build.binlog" };
        var initial = await _service.DownloadArtifactsAsync("42", options);
        var key = Assert.Single(Assert.Single(initial.Results).Members).CacheKey;
        if (corruptDescriptor)
            await _cache.SetMetadataAsync(key, "{invalid", TimeSpan.FromHours(4));
        else
        {
            await using var damaged = new MemoryStream([3, 2, 1]);
            await _cache.SetArtifactAsync(key, damaged);
        }

        var resumed = await _service.DownloadArtifactsAsync("42", options);

        Assert.True(resumed.Complete);
        Assert.Equal(3, resumed.StreamedBytes);
        var member = Assert.Single(Assert.Single(resumed.Results).Members);
        Assert.Equal("ok", member.Outcome);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(member.LocalPath!));
        await _client.Received(2).OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongDeclaredLength_PreservesPartialButNeverPublishesCompleteCachePair(bool allowLarge)
    {
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2]), "application/octet-stream", 4));
        var report = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPath = "Build.binlog", AllowLarge = allowLarge });

        Assert.False(report.Complete);
        Assert.Equal(AcquisitionErrorKind.InvalidResponse, Assert.Single(report.Results).Error!.Kind);
        var key = AzdoCacheKeys.MetadataKey(_options, "dnceng-public", "public",
            $"artifactmemberbytes:v1:42:7:{AzdoDownloadPaths.Hash("Build.binlog")}:content");
        Assert.Null(await _cache.GetMetadataAsync(key));
        Assert.Null(await _cache.GetArtifactAsync(key));
        Assert.Single(Directory.GetFiles(_root, "transfer-*.partial.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escape.binlog")]
    [InlineData("drop/../../escape.binlog")]
    [InlineData("/escape.binlog")]
    [InlineData("drop/C:/escape.binlog")]
    [InlineData("drop/a\\escape.binlog")]
    [InlineData("drop/%2e%2e/escape.binlog")]
    [InlineData("drop/%252e%252e/escape.binlog")]
    public async Task UnsafeZipMember_IsNotExtractedAndOpaqueArchiveRemainsReadable(string member)
    {
        var zip = Zip((member, "unsafe"), ("drop/Build.binlog", "safe"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));

        var report = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPattern = "**/*.binlog" });

        Assert.False(report.Complete);
        var result = Assert.Single(report.Results);
        Assert.Equal(AcquisitionErrorKind.InvalidResponse, result.Error!.Kind);
        Assert.Empty(result.Members);
        Assert.NotNull(result.Delivery);
        Assert.Equal(Convert.ToBase64String(zip), _delivery.Read(result.Delivery.EvidenceId, 0, zip.Length, "base64", "public").Data);
        Assert.Empty(Directory.GetFiles(_root, "escape.binlog", SearchOption.AllDirectories));
        Assert.Null(await _cache.GetMetadataAsync("azdo:dnceng-public:public:artifactmembers:v1:42:7:all"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollidingOrSymlinkZipMembers_AreNotMaterialized(bool symlink)
    {
        var zip = Zip(("drop/Build.binlog", "one"), ("drop/build.binlog", "two"));
        if (symlink)
        {
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("drop/Build.binlog");
                entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("../../target");
            }
            zip = bytes.ToArray();
        }
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));

        var report = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPattern = "**/*.binlog" });

        Assert.False(report.Complete);
        Assert.Empty(Assert.Single(report.Results).Members);
        Assert.NotNull(Assert.Single(report.Results).Delivery);
    }

    [Fact]
    public async Task BadMemberCrc_FailsWithoutCachingSelectedBytes()
    {
        var zip = Zip(("drop/Build.binlog", "evidence"), ("drop/Other.binlog", "valid"));
        var centralHeader = zip.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(centralHeader >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(centralHeader + 16), 0);
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));

        var report = await _service.DownloadArtifactsAsync("42", new() { ArtifactName = "drop", MemberPattern = "**/*.binlog", AllowLarge = true });

        Assert.False(report.Complete);
        var members = Assert.Single(report.Results).Members;
        Assert.Equal(2, members.Count);
        var member = Assert.Single(members, m => m.Outcome == "failed");
        var success = Assert.Single(members, m => m.Outcome == "ok");
        Assert.Equal("Other.binlog", success.MemberPath);
        Assert.Equal("valid", await File.ReadAllTextAsync(success.LocalPath!));
        Assert.Equal(5, report.Bytes);
        Assert.Equal(AcquisitionErrorKind.InvalidResponse, member.Error!.Kind);
        Assert.Null(member.Delivery);
        Assert.Null(await _cache.GetMetadataAsync(member.CacheKey));
        Assert.Null(await _cache.GetArtifactAsync(member.CacheKey));
    }

    [Theory]
    [InlineData("PipelineArtifact", "format")]
    [InlineData("Container", "$format")]
    public async Task NativeProvider_AddsExactlyOneLeadingSlashAndPreservesEncodedMember(string type, string format)
    {
        Uri? requested = null;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        }));
        var provider = new AzdoApiClient(http, Substitute.For<IAzdoTokenAccessor>());
        var artifact = _artifact with { Resource = new() { Type = type, DownloadUrl = $"https://artifacts.visualstudio.com/content?{format}=zip&token=opaque" } };

        await using var stream = await provider.OpenArtifactAsync("org", "project", 42, artifact, "Release/a #+.binlog");

        var query = System.Web.HttpUtility.ParseQueryString(requested!.Query);
        Assert.Equal("/Release/a #+.binlog", query["subPath"]);
        Assert.Equal("file", query[format]);
        Assert.Equal("opaque", query["token"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task UnsolicitedPartialHttpBody_IsRejected(HttpStatusCode status, bool contentRange)
    {
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Null(request.Headers.Range);
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([1, 2]) };
            if (contentRange) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 4);
            return response;
        }));
        var provider = new AzdoApiClient(http, Substitute.For<IAzdoTokenAccessor>());
        var artifact = _artifact with { Resource = _artifact.Resource! with { DownloadUrl = "https://artifacts.visualstudio.com/content?format=zip" } };

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.OpenArtifactAsync("org", "project", 42, artifact, "Build.binlog"));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"fingerprint\":\"invalid\",\"members\":[],\"source\":{\"transferMode\":\"zip_selective\",\"sourceComplete\":true}}")]
    public async Task Resume_ReacquiresInvalidCatalogInLiveMode(string damaged)
    {
        var zip = Zip(("drop/Build.binlog", "evidence"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPattern = "**/Build.binlog" };
        await _service.DownloadArtifactsAsync("42", options);
        var catalogKey = AzdoCacheKeys.MetadataKey(_options, "dnceng-public", "public", "artifactmembers:v1:42:7:all");
        await _cache.SetMetadataAsync(catalogKey, damaged, TimeSpan.FromHours(4));

        var resumed = await _service.DownloadArtifactsAsync("42", options);

        Assert.True(resumed.Complete);
        Assert.Equal(zip.Length, resumed.StreamedBytes);
        Assert.Equal("evidence", await File.ReadAllTextAsync(Assert.Single(Assert.Single(resumed.Results).Members).LocalPath!));
        Assert.NotEqual(damaged, await _cache.GetMetadataAsync(catalogKey));
    }

    [Fact]
    public async Task Replay_RepairsCorruptDeliveryFromVerifiedCacheWithoutNetwork()
    {
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPath = "Build.binlog" };
        var first = await _service.DownloadArtifactsAsync("42", options);
        var member = Assert.Single(Assert.Single(first.Results).Members);
        await File.WriteAllBytesAsync(member.LocalPath!, [3, 2, 1]);

        var replay = await _service.DownloadArtifactsAsync("42", options);

        Assert.True(replay.Complete);
        Assert.Equal(0, replay.StreamedBytes);
        var restored = Assert.Single(Assert.Single(replay.Results).Members);
        Assert.Equal(member.Delivery!.EvidenceId, restored.Delivery!.EvidenceId);
        Assert.Equal("AQID", _delivery.Read(restored.Delivery.EvidenceId, 0, 3, "base64", "public").Data);
        await _client.Received(1).OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CorruptSnapshotBlob_FailsShaVerificationWithoutNetworkOrSnapshotWrites()
    {
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream([1, 2, 3]), "application/octet-stream", 3));
        var metadata = new CachingAzdoApiClient(_client, _cache, _options);
        await metadata.GetBuildArtifactsAsync("dnceng-public", "public", 42);
        var options = new AzdoDownloadOptions { ArtifactName = "drop", MemberPath = "Build.binlog", AllowLarge = true };
        await _service.DownloadArtifactsAsync("42", options);
        var snapshot = Path.Combine(_root, "snapshot");
        await SnapshotExporter.ExportAsync(_options.GetEffectiveCacheRoot(), snapshot);
        Assert.True((await SnapshotValidator.ValidateAsync(snapshot)).IsValid);
        var blob = Assert.Single(Directory.GetFiles(Path.Combine(snapshot, "artifacts"), "*", SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(blob, [3, 2, 1]);
        var validation = await SnapshotValidator.ValidateAsync(snapshot);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("SHA-256", StringComparison.Ordinal));
        var database = await File.ReadAllBytesAsync(Path.Combine(snapshot, "cache.db"));
        var evalOptions = new CacheOptions { CacheRoot = snapshot, EvalMode = true };
        using var store = new SqliteCacheStore(evalOptions);
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, evalOptions);
        var delivery = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = Path.Combine(_root, "offline-delivery") });
        var service = new AzdoDownloadService(offline, store, evalOptions, delivery);

        var replay = await service.DownloadArtifactsAsync("42", options);

        Assert.False(replay.Complete);
        Assert.Equal(0, replay.StreamedBytes);
        Assert.Equal(AcquisitionErrorKind.InvalidResponse, Assert.Single(replay.Results).Error!.Kind);
        Assert.Equal(database, await File.ReadAllBytesAsync(Path.Combine(snapshot, "cache.db")));
        Assert.Equal(new byte[] { 3, 2, 1 }, await File.ReadAllBytesAsync(blob));
        await _client.Received(1).OpenArtifactAsync("dnceng-public", "public", 42, _artifact, "Build.binlog", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvidenceReadCli_CopiesWholeBinaryNotJustRequestedChunk_AndVerifiesResume()
    {
        var bytes = Enumerable.Range(0, 16384).Select(i => (byte)i).ToArray();
        var evidence = _delivery.PutBytes(bytes, "application/octet-stream", null, null, "public");
        using var services = new ServiceCollection().AddSingleton(_options)
            .AddSingleton<IEvidenceDeliveryStore>(_delivery)
            .AddSingleton(Substitute.For<IAzdoTokenAccessor>()).BuildServiceProvider();
        var command = new global::AzdoDownloadCommands(services);
        var path = Path.Combine(_root, "export", "evidence.dmp");
        await command.ReadEvidence(evidence.EvidenceId, offsetBytes: 1, lengthBytes: 2, output: path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        await command.ReadEvidence(evidence.EvidenceId, output: path);
        await File.WriteAllBytesAsync(path, [0]);
        await Assert.ThrowsAsync<IOException>(() => command.ReadEvidence(evidence.EvidenceId, output: path));
        await command.ReadEvidence(evidence.EvidenceId, output: path, overwrite: true);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial"));
    }
}
