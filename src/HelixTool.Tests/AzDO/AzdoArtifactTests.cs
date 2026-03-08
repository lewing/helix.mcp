using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Tests for build artifact and test attachment APIs across all layers:
/// API client, caching, service, and MCP tools.
/// </summary>
public class AzdoArtifactTests
{
    private readonly IAzdoApiClient _mockApi;
    private readonly AzdoService _svc;
    private readonly AzdoMcpTools _tools;

    public AzdoArtifactTests()
    {
        _mockApi = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_mockApi);
        _tools = new AzdoMcpTools(_svc);
    }

    // ── API Client: GetBuildArtifactsAsync ───────────────────────────

    [Fact]
    public async Task GetBuildArtifactsAsync_ReturnsArtifactsForValidBuild()
    {
        var artifacts = new List<AzdoBuildArtifact>
        {
            new()
            {
                Id = 1,
                Name = "drop",
                Resource = new AzdoArtifactResource
                {
                    Type = "Container",
                    DownloadUrl = "https://dev.azure.com/org/proj/_apis/build/builds/42/artifacts?artifactName=drop"
                }
            },
            new()
            {
                Id = 2,
                Name = "logs",
                Resource = new AzdoArtifactResource
                {
                    Type = "Container",
                    DownloadUrl = "https://dev.azure.com/org/proj/_apis/build/builds/42/artifacts?artifactName=logs"
                }
            }
        };

        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await _svc.GetBuildArtifactsAsync("42");

        Assert.Equal(2, result.Count);
        Assert.Equal("drop", result[0].Name);
        Assert.Equal("logs", result[1].Name);
    }

    [Fact]
    public async Task GetBuildArtifactsAsync_ReturnsEmptyListWhenNoArtifacts()
    {
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        var result = await _svc.GetBuildArtifactsAsync("42");

        Assert.Empty(result);
    }

    // ── API Client: GetTestAttachmentsAsync ──────────────────────────

    [Fact]
    public async Task GetTestAttachmentsAsync_ReturnsAttachmentsForValidResult()
    {
        var attachments = new List<AzdoTestAttachment>
        {
            new()
            {
                Id = 10,
                FileName = "screenshot.png",
                Size = 1024,
                Comment = "Failure screenshot",
                Url = "https://dev.azure.com/org/proj/_apis/test/runs/1/results/2/attachments/10"
            }
        };

        _mockApi.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await _svc.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2);

        Assert.Single(result);
        Assert.Equal("screenshot.png", result[0].FileName);
        Assert.Equal(1024, result[0].Size);
    }

    [Fact]
    public async Task GetTestAttachmentsAsync_ReturnsEmptyListWhenNoAttachments()
    {
        _mockApi.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestAttachment>());

        var result = await _svc.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2);

        Assert.Empty(result);
    }

    // ── Service Layer: URL Resolution ────────────────────────────────

    [Fact]
    public async Task GetBuildArtifactsAsync_ResolvesOrgProjectFromUrl()
    {
        _mockApi.GetBuildArtifactsAsync("myorg", "myproj", 99, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        await _svc.GetBuildArtifactsAsync(
            "https://dev.azure.com/myorg/myproj/_build/results?buildId=99");

        await _mockApi.Received(1).GetBuildArtifactsAsync(
            "myorg", "myproj", 99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildArtifactsAsync_PlainIdUsesDefaults()
    {
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        await _svc.GetBuildArtifactsAsync("42");

        await _mockApi.Received(1).GetBuildArtifactsAsync(
            "dnceng-public", "public", 42, Arg.Any<CancellationToken>());
    }

    // ── Service Layer: TestAttachments top parameter ─────────────────

    [Fact]
    public async Task GetTestAttachmentsAsync_RespectsTopParameter()
    {
        var attachments = Enumerable.Range(1, 10)
            .Select(i => new AzdoTestAttachment { Id = i, FileName = $"file{i}.txt", Size = i * 100 })
            .ToList();

        _mockApi.GetTestAttachmentsAsync("org", "proj", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await _svc.GetTestAttachmentsAsync("org", "proj", 1, 2, top: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal("file1.txt", result[0].FileName);
        Assert.Equal("file3.txt", result[2].FileName);
    }

    [Fact]
    public async Task GetTestAttachmentsAsync_TopExceedsCount_ReturnsAll()
    {
        var attachments = new List<AzdoTestAttachment>
        {
            new() { Id = 1, FileName = "a.txt", Size = 100 },
            new() { Id = 2, FileName = "b.txt", Size = 200 }
        };

        _mockApi.GetTestAttachmentsAsync("org", "proj", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await _svc.GetTestAttachmentsAsync("org", "proj", 1, 2, top: 100);

        Assert.Equal(2, result.Count);
    }

    // ── Caching: GetBuildArtifactsAsync ──────────────────────────────

    [Fact]
    public async Task CachingClient_GetBuildArtifacts_CacheMissCallsInnerAndCaches()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "drop" }
        };
        inner.GetBuildArtifactsAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await sut.GetBuildArtifactsAsync("org", "proj", 42);

        Assert.Single(result);
        Assert.Equal("drop", result[0].Name);
        await inner.Received(1).GetBuildArtifactsAsync("org", "proj", 42, Arg.Any<CancellationToken>());
        await cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingClient_GetBuildArtifacts_CacheHitSkipsInner()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        var cached = JsonSerializer.Serialize(new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "cached-artifact" }
        });
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cached);

        var result = await sut.GetBuildArtifactsAsync("org", "proj", 42);

        Assert.Single(result);
        Assert.Equal("cached-artifact", result[0].Name);
        await inner.DidNotReceive().GetBuildArtifactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingClient_GetBuildArtifacts_UsesImmutableTtl()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildArtifactsAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        await sut.GetBuildArtifactsAsync("org", "proj", 42);

        // Artifacts are immutable once published — should use 4h TTL
        await cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingClient_GetBuildArtifacts_CacheKeyHasAzdoPrefix()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildArtifactsAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        await sut.GetBuildArtifactsAsync("org", "proj", 42);

        await cache.Received(1).SetMetadataAsync(
            Arg.Is<string>(k => k.StartsWith("azdo:")),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── Caching: GetTestAttachmentsAsync ─────────────────────────────

    [Fact]
    public async Task CachingClient_GetTestAttachments_CacheMissCallsInnerAndCaches()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var attachments = new List<AzdoTestAttachment>
        {
            new() { Id = 1, FileName = "trace.log", Size = 512 }
        };
        inner.GetTestAttachmentsAsync("org", "proj", 5, 10, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await sut.GetTestAttachmentsAsync("org", "proj", 5, 10);

        Assert.Single(result);
        Assert.Equal("trace.log", result[0].FileName);
        await inner.Received(1).GetTestAttachmentsAsync("org", "proj", 5, 10, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingClient_GetTestAttachments_CacheHitSkipsInner()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        var cached = JsonSerializer.Serialize(new List<AzdoTestAttachment>
        {
            new() { Id = 1, FileName = "cached.log" }
        });
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cached);

        var result = await sut.GetTestAttachmentsAsync("org", "proj", 5, 10);

        Assert.Single(result);
        Assert.Equal("cached.log", result[0].FileName);
        await inner.DidNotReceive().GetTestAttachmentsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingClient_GetTestAttachments_UsesTestTtl()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetTestAttachmentsAsync("org", "proj", 5, 10, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestAttachment>());

        await sut.GetTestAttachmentsAsync("org", "proj", 5, 10);

        // Test attachments use 1h TTL (stable after test run)
        await cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(1),
            Arg.Any<CancellationToken>());
    }

    // ── Caching: disabled ───────────────────────────────────────────

    [Fact]
    public async Task CachingClient_Disabled_ArtifactsPassThrough()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        inner.GetBuildArtifactsAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact> { new() { Id = 1, Name = "art" } });

        var result = await sut.GetBuildArtifactsAsync("org", "proj", 42);

        Assert.Single(result);
        await cache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingClient_Disabled_AttachmentsPassThrough()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        inner.GetTestAttachmentsAsync("org", "proj", 5, 10, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestAttachment> { new() { Id = 1, FileName = "f.txt" } });

        var result = await sut.GetTestAttachmentsAsync("org", "proj", 5, 10);

        Assert.Single(result);
        await cache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── MCP Tool: azdo_artifacts ─────────────────────────────────────

    [Fact]
    public async Task Artifacts_ReturnsArtifactList()
    {
        var artifacts = new List<AzdoBuildArtifact>
        {
            new()
            {
                Id = 1,
                Name = "Helix_Logs",
                Resource = new AzdoArtifactResource
                {
                    Type = "Container",
                    DownloadUrl = "https://dev.azure.com/org/proj/_apis/build/builds/42/artifacts?artifactName=Helix_Logs&%24format=zip"
                }
            }
        };

        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await _tools.Artifacts("42");

        Assert.Single(result);
        Assert.Equal("Helix_Logs", result[0].Name);
        Assert.NotNull(result[0].Resource);
        Assert.Equal("Container", result[0].Resource!.Type);
    }

    [Fact]
    public async Task Artifacts_AcceptsUrl()
    {
        _mockApi.GetBuildArtifactsAsync("myorg", "myproj", 99, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        await _tools.Artifacts("https://dev.azure.com/myorg/myproj/_build/results?buildId=99");

        await _mockApi.Received(1).GetBuildArtifactsAsync(
            "myorg", "myproj", 99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Artifacts_EmptyList_ReturnsEmpty()
    {
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        var result = await _tools.Artifacts("42");

        Assert.Empty(result);
    }

    [Fact]
    public async Task Artifacts_UsesCamelCaseJsonPropertyNames()
    {
        var artifact = new AzdoBuildArtifact
        {
            Id = 1,
            Name = "drop",
            Resource = new AzdoArtifactResource
            {
                Type = "Container",
                DownloadUrl = "https://example.com/download",
                Data = "#/123/drop",
                Url = "https://example.com/resource"
            }
        };

        var json = JsonSerializer.Serialize(artifact);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify camelCase property names from [JsonPropertyName] attributes
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("drop", root.GetProperty("name").GetString());

        var resource = root.GetProperty("resource");
        Assert.Equal("Container", resource.GetProperty("type").GetString());
        Assert.Equal("https://example.com/download", resource.GetProperty("downloadUrl").GetString());
        Assert.Equal("#/123/drop", resource.GetProperty("data").GetString());
        Assert.Equal("https://example.com/resource", resource.GetProperty("url").GetString());
    }

    // ── MCP Tool: azdo_test_attachments ──────────────────────────────

    [Fact]
    public async Task TestAttachments_ReturnsAttachmentList()
    {
        var attachments = new List<AzdoTestAttachment>
        {
            new()
            {
                Id = 1,
                FileName = "screenshot.png",
                Size = 2048,
                Comment = "UI failure capture",
                Url = "https://dev.azure.com/org/proj/_apis/test/runs/1/results/2/attachments/1"
            },
            new()
            {
                Id = 2,
                FileName = "crash.dmp",
                Size = 1048576,
                Url = "https://dev.azure.com/org/proj/_apis/test/runs/1/results/2/attachments/2"
            }
        };

        _mockApi.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await _tools.TestAttachments(1, 2);

        Assert.Equal(2, result.Count);
        Assert.Equal("screenshot.png", result[0].FileName);
        Assert.Equal(2048, result[0].Size);
        Assert.Equal("crash.dmp", result[1].FileName);
    }

    [Fact]
    public async Task TestAttachments_RespectsTopParameter()
    {
        var attachments = Enumerable.Range(1, 10)
            .Select(i => new AzdoTestAttachment { Id = i, FileName = $"file{i}.txt", Size = i * 100 })
            .ToList();

        _mockApi.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await _tools.TestAttachments(1, 2, top: 5);

        Assert.Equal(5, result.Count);
        Assert.Equal("file1.txt", result[0].FileName);
        Assert.Equal("file5.txt", result[4].FileName);
    }

    [Fact]
    public async Task TestAttachments_CustomOrgAndProject()
    {
        _mockApi.GetTestAttachmentsAsync("custom-org", "custom-proj", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestAttachment>());

        await _tools.TestAttachments(1, 2, org: "custom-org", project: "custom-proj");

        await _mockApi.Received(1).GetTestAttachmentsAsync(
            "custom-org", "custom-proj", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestAttachments_EmptyList_ReturnsEmpty()
    {
        _mockApi.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestAttachment>());

        var result = await _tools.TestAttachments(1, 2);

        Assert.Empty(result);
    }

    [Fact]
    public async Task TestAttachments_UsesCamelCaseJsonPropertyNames()
    {
        var attachment = new AzdoTestAttachment
        {
            Id = 1,
            FileName = "test.log",
            Size = 4096,
            Comment = "Test output",
            Url = "https://example.com/attachment",
            CreatedDate = new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(attachment);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify camelCase property names from [JsonPropertyName] attributes
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("test.log", root.GetProperty("fileName").GetString());
        Assert.Equal(4096, root.GetProperty("size").GetInt64());
        Assert.Equal("Test output", root.GetProperty("comment").GetString());
        Assert.Equal("https://example.com/attachment", root.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("createdDate").ValueKind);
    }

    // ── Edge Cases ──────────────────────────────────────────────────

    [Fact]
    public async Task Artifacts_InvalidBuildId_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _tools.Artifacts("not-a-valid-id"));
    }

    [Fact]
    public async Task GetBuildArtifactsAsync_EmptyString_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetBuildArtifactsAsync(""));
    }

    [Fact]
    public async Task Artifacts_MultipleArtifactsWithResources()
    {
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "drop", Resource = new AzdoArtifactResource { Type = "Container", DownloadUrl = "https://example.com/drop" } },
            new() { Id = 2, Name = "binlog", Resource = new AzdoArtifactResource { Type = "Container", DownloadUrl = "https://example.com/binlog" } },
            new() { Id = 3, Name = "testresults", Resource = null }
        };

        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await _tools.Artifacts("1");

        Assert.Equal(3, result.Count);
        Assert.NotNull(result[0].Resource);
        Assert.Equal("https://example.com/drop", result[0].Resource!.DownloadUrl);
        Assert.Null(result[2].Resource);
    }

    [Fact]
    public async Task TestAttachments_LargeFileSize()
    {
        var attachments = new List<AzdoTestAttachment>
        {
            new() { Id = 1, FileName = "large-dump.dmp", Size = 2_147_483_648L } // 2 GB
        };

        _mockApi.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await _tools.TestAttachments(1, 2);

        Assert.Single(result);
        Assert.Equal(2_147_483_648L, result[0].Size);
    }

    [Fact]
    public async Task TestAttachments_WithCreatedDate()
    {
        var created = new DateTimeOffset(2025, 7, 18, 14, 30, 0, TimeSpan.Zero);
        var attachments = new List<AzdoTestAttachment>
        {
            new() { Id = 1, FileName = "output.log", Size = 100, CreatedDate = created }
        };

        _mockApi.GetTestAttachmentsAsync("dnceng-public", "public", 1, 2, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(attachments);

        var result = await _tools.TestAttachments(1, 2);

        Assert.Equal(created, result[0].CreatedDate);
    }

    // ── Model serialization round-trip ──────────────────────────────

    [Fact]
    public void AzdoBuildArtifact_RoundTripsViaJson()
    {
        var original = new AzdoBuildArtifact
        {
            Id = 42,
            Name = "TestArtifact",
            Resource = new AzdoArtifactResource
            {
                Type = "Container",
                Data = "#/12345/TestArtifact",
                DownloadUrl = "https://dev.azure.com/org/proj/_apis/build/builds/42/artifacts?artifactName=TestArtifact",
                Url = "https://dev.azure.com/org/proj/_apis/build/builds/42/artifacts/TestArtifact"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AzdoBuildArtifact>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized!.Id);
        Assert.Equal(original.Name, deserialized.Name);
        Assert.NotNull(deserialized.Resource);
        Assert.Equal(original.Resource.Type, deserialized.Resource!.Type);
        Assert.Equal(original.Resource.DownloadUrl, deserialized.Resource.DownloadUrl);
    }

    [Fact]
    public void AzdoTestAttachment_RoundTripsViaJson()
    {
        var created = new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero);
        var original = new AzdoTestAttachment
        {
            Id = 7,
            FileName = "screenshot.png",
            Size = 65536,
            Comment = "Test failure screenshot",
            Url = "https://dev.azure.com/org/proj/_apis/test/runs/1/results/2/attachments/7",
            CreatedDate = created
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AzdoTestAttachment>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized!.Id);
        Assert.Equal(original.FileName, deserialized.FileName);
        Assert.Equal(original.Size, deserialized.Size);
        Assert.Equal(original.Comment, deserialized.Comment);
        Assert.Equal(original.CreatedDate, deserialized.CreatedDate);
    }
}
