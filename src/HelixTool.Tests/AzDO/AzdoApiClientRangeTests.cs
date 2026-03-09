using System.Net;
using System.Text;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Tests for URL construction with startLine/endLine query parameters
/// on <see cref="AzdoApiClient.GetBuildLogAsync"/>.
/// </summary>
public class AzdoApiClientRangeTests
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly AzdoApiClient _client;

    public AzdoApiClientRangeTests()
    {
        var mockToken = Substitute.For<IAzdoTokenAccessor>();
        mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("test-token");
        _handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(_handler);
        _client = new AzdoApiClient(httpClient, mockToken);
    }

    // A-1: No range params → URL has no startLine/endLine (backward compat)
    [Fact]
    public async Task GetBuildLogAsync_NoRange_UrlHasNoStartLineEndLine()
    {
        _handler.ResponseContent = "log output";

        await _client.GetBuildLogAsync("org", "proj", 42, 5);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("build/builds/42/logs/5", url);
        Assert.Contains("api-version=7.0", url);
        Assert.DoesNotContain("startLine", url);
        Assert.DoesNotContain("endLine", url);
    }

    // A-2: startLine=100 → URL includes startLine=100
    [Fact]
    public async Task GetBuildLogAsync_StartLine_UrlIncludesStartLine()
    {
        _handler.ResponseContent = "partial log";

        await _client.GetBuildLogAsync("org", "proj", 42, 5, startLine: 100);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("startLine=100", url);
        Assert.DoesNotContain("endLine", url);
        Assert.Contains("api-version=7.0", url);
    }

    // A-3: endLine=200 → URL includes endLine=200
    [Fact]
    public async Task GetBuildLogAsync_EndLine_UrlIncludesEndLine()
    {
        _handler.ResponseContent = "partial log";

        await _client.GetBuildLogAsync("org", "proj", 42, 5, endLine: 200);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("endLine=200", url);
        Assert.DoesNotContain("startLine", url);
        Assert.Contains("api-version=7.0", url);
    }

    // A-4: Both startLine=100 and endLine=200 → URL includes both
    [Fact]
    public async Task GetBuildLogAsync_BothParams_UrlIncludesBoth()
    {
        _handler.ResponseContent = "range log";

        await _client.GetBuildLogAsync("org", "proj", 42, 5, startLine: 100, endLine: 200);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("startLine=100", url);
        Assert.Contains("endLine=200", url);
        Assert.Contains("api-version=7.0", url);
    }

    // A-5: Range request returning 404 → returns null
    [Fact]
    public async Task GetBuildLogAsync_Range404_ReturnsNull()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.GetBuildLogAsync("org", "proj", 42, 5, startLine: 100, endLine: 200);

        Assert.Null(result);
    }

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseContent { get; set; } = "{}";
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json")
            });
        }
    }
}
