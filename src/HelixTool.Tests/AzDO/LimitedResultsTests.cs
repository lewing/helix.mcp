using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class LimitedResultsTests
{
    [Fact]
    public void Serialize_TruncatedResults_WritesResultsTruncatedAndNote()
    {
        var results = new LimitedResults<string>(["first", "second"], truncated: true, note: "Results limited to 2.");

        var json = JsonSerializer.Serialize(results);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("results").GetArrayLength());
        Assert.Equal("first", root.GetProperty("results")[0].GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal("Results limited to 2.", root.GetProperty("note").GetString());
    }

    [Fact]
    public void Serialize_NonTruncatedResults_OmitsNote()
    {
        var results = new LimitedResults<string>(["only"], truncated: false);

        var json = JsonSerializer.Serialize(results);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("results").GetArrayLength());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.False(root.TryGetProperty("note", out _));
    }

    [Fact]
    public void RoundTrip_PreservesAllProperties()
    {
        var original = new LimitedResults<AzdoBuildArtifact>(
            [
                new AzdoBuildArtifact
                {
                    Id = 42,
                    Name = "drop",
                    Resource = new AzdoArtifactResource
                    {
                        Type = "Container",
                        DownloadUrl = "https://example.com/download",
                        Data = "#/42/drop",
                        Url = "https://example.com/resource"
                    }
                }
            ],
            truncated: true,
            total: 5,
            note: "Results limited to 1."
        );

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<LimitedResults<AzdoBuildArtifact>>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.Truncated);
        Assert.Equal(5, deserialized.Total);
        Assert.Equal("Results limited to 1.", deserialized.Note);
        Assert.Single(deserialized.Results);
        Assert.Equal(42, deserialized[0].Id);
        Assert.Equal("drop", deserialized[0].Name);
        var resource = deserialized[0].Resource;
        Assert.NotNull(resource);
        Assert.Equal("Container", resource!.Type);
        Assert.Equal("https://example.com/download", resource.DownloadUrl);
    }

    [Fact]
    public void Serialize_WithTotal_WritesTotalProperty()
    {
        var results = new LimitedResults<int>([1, 2], truncated: false, total: 7);

        var json = JsonSerializer.Serialize(results);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(7, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Serialize_WithoutTotal_OmitsTotalProperty()
    {
        var results = new LimitedResults<int>([1, 2], truncated: false, total: null);

        var json = JsonSerializer.Serialize(results);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("total", out _));
    }

    [Fact]
    public void IReadOnlyListErgonomics_ExposeCountAndIndexer()
    {
        var results = new LimitedResults<string>(["alpha", "beta"], truncated: false);

        Assert.Equal(2, results.Count);
        Assert.Equal("alpha", results[0]);
        Assert.Equal("beta", results[1]);
    }

    [Fact]
    public async Task Builds_WhenResultCountMatchesTop_SetsTruncatedMetadata()
    {
        var mockApi = Substitute.For<IAzdoApiClient>();
        mockApi.ListBuildsAsync("dnceng-public", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns([
                new AzdoBuild { Id = 1, BuildNumber = "b1" },
                new AzdoBuild { Id = 2, BuildNumber = "b2" },
                new AzdoBuild { Id = 3, BuildNumber = "b3" }
            ]);

        var tools = CreateTools(mockApi);

        var result = await tools.Builds(top: 3);

        Assert.True(result.Truncated);
        Assert.NotNull(result.Note);
        Assert.Contains("limited to 3", result.Note);
        Assert.Contains("higher 'top' value", result.Note);
    }

    [Fact]
    public async Task Builds_WhenResultCountIsBelowTop_DoesNotSetTruncatedMetadata()
    {
        var mockApi = Substitute.For<IAzdoApiClient>();
        mockApi.ListBuildsAsync("dnceng-public", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns([
                new AzdoBuild { Id = 1, BuildNumber = "b1" },
                new AzdoBuild { Id = 2, BuildNumber = "b2" }
            ]);

        var tools = CreateTools(mockApi);

        var result = await tools.Builds(top: 3);

        Assert.False(result.Truncated);
        Assert.Null(result.Note);
    }

    private static AzdoMcpTools CreateTools(IAzdoApiClient mockApi)
        => new(new AzdoService(mockApi), Substitute.For<IAzdoTokenAccessor>());
}
