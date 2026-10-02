using System.Net;
using System.Globalization;
using System.Text;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TestResultsSilentEmptyTests
{
    [Fact]
    public async Task GetTestRunsAsync_DeserializesUnanalyzedTestsAsFailedCount()
    {
        var client = CreateClient(new SequenceHttpMessageHandler(_ => JsonResponse(
            """
            {
              "value": [
                {
                  "id": 44793916,
                  "name": "x",
                  "state": "Completed",
                  "totalTests": 6745,
                  "passedTests": 6653,
                  "unanalyzedTests": 9,
                  "incompleteTests": 0,
                  "notApplicableTests": 83
                }
              ],
              "count": 1
            }
            """)));

        var runs = await client.GetTestRunsAsync("dnceng-public", "public", buildId: 123);

        var run = Assert.Single(runs);
        Assert.Equal(44793916, run.Id);
        Assert.Equal("x", run.Name);
        Assert.Equal(6745, run.TotalTests);
        Assert.Equal(6653, run.PassedTests);
        Assert.Equal(9, run.FailedTests);
        Assert.Equal(0, run.IncompleteTests);
        Assert.Equal(83, run.NotApplicableTests);
    }

    [Theory]
    [MemberData(nameof(AuthFailureResponses))]
    public async Task GetTestResultsAsync_AuthLikeResponse_ThrowsAuthError(string name, Func<HttpResponseMessage> responseFactory)
    {
        var client = CreateClient(new SequenceHttpMessageHandler(_ => responseFactory()));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetTestResultsAsync("dnceng-public", "public", runId: 44793916));

        AssertAuthGuidance(name, ex);
    }

    [Theory]
    [MemberData(nameof(AuthFailureResponses))]
    public async Task GetTestRunsAsync_AuthLikeResponse_ThrowsAuthError(string name, Func<HttpResponseMessage> responseFactory)
    {
        var client = CreateClient(new SequenceHttpMessageHandler(_ => responseFactory()));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetTestRunsAsync("dnceng-public", "public", buildId: 1618757));

        AssertAuthGuidance(name, ex);
    }

    [Theory]
    [MemberData(nameof(AuthFailureResponses))]
    public async Task GetBuildAsync_AuthLikeResponse_ThrowsAuthError(string name, Func<HttpResponseMessage> responseFactory)
    {
        var client = CreateClient(new SequenceHttpMessageHandler(_ => responseFactory()));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildAsync("dnceng-public", "public", buildId: 1618757));

        AssertAuthGuidance(name, ex);
    }

    [Fact]
    public async Task GetTestResultsAsync_404_ThrowsRunNotFoundInsteadOfReturningEmptyList()
    {
        var client = CreateClient(new SequenceHttpMessageHandler(_ => JsonResponse(
            """{"message":"The test run was not found or has been deleted."}""",
            HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetTestResultsAsync("dnceng-public", "public", runId: 44793916));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "azdo", "list_test_results", 404);
    }

    [Fact]
    public async Task GetTestResultsAsync_TopAboveAzdoLimit_NeverSendsTopAbove10000()
    {
        var handler = new SequenceHttpMessageHandler(request =>
            GetQueryInt(request.RequestUri!, "$skip") == 10_000
                ? JsonResponse(BuildTestResultsPayload(start: 10_000, count: 1))
                : JsonResponse(BuildTestResultsPayload(start: 0, count: 10_000)));
        var client = CreateClient(handler);

        var results = await client.GetTestResultsAsync("dnceng-public", "public", runId: 44793916, top: 10001);

        Assert.Equal(10_001, results.Count);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(10_000, GetQueryInt(handler.RequestUris[1], "$skip"));
        Assert.All(handler.RequestUris, AssertTopDoesNotExceedAzdoLimit);
    }

    [Fact]
    public async Task GetTestRunsAsync_TopAboveAzdoLimit_NeverSendsTopAbove10000()
    {
        var handler = new SequenceHttpMessageHandler(request =>
            GetQueryInt(request.RequestUri!, "$skip") == 10_000
                ? JsonResponse(BuildTestRunsPayload(start: 10_000, count: 1))
                : JsonResponse(BuildTestRunsPayload(start: 0, count: 10_000)));
        var client = CreateClient(handler);

        var runs = await client.GetTestRunsAsync("dnceng-public", "public", buildId: 1618757, top: 10001);

        Assert.Equal(10_001, runs.Count);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(10_000, GetQueryInt(handler.RequestUris[1], "$skip"));
        Assert.All(handler.RequestUris, AssertTopDoesNotExceedAzdoLimit);
    }

    [Fact]
    public async Task CachingAzdoApiClient_GetTestResultsAsync_DoesNotCacheAuthFailures()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        inner.GetTestResultsAsync("org", "proj", 77, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<AzdoTestResult>>(CreateAuthException()));
        var sut = new CachingAzdoApiClient(inner, cache, new CacheOptions { MaxSizeBytes = 1024 * 1024 });

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.GetTestResultsAsync("org", "proj", 77));
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.GetTestResultsAsync("org", "proj", 77));

        await inner.Received(2).GetTestResultsAsync("org", "proj", 77, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SetMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingAzdoApiClient_GetTestRunsAsync_DoesNotCacheAuthFailures()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        inner.GetTestRunsAsync("org", "proj", 1, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<AzdoTestRun>>(CreateAuthException()));
        var sut = new CachingAzdoApiClient(inner, cache, new CacheOptions { MaxSizeBytes = 1024 * 1024 });

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.GetTestRunsAsync("org", "proj", 1));
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.GetTestRunsAsync("org", "proj", 1));

        await inner.Received(2).GetTestRunsAsync("org", "proj", 1, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SetMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<string, Func<HttpResponseMessage>> AuthFailureResponses => new()
    {
        {
            "203 NonAuthoritativeInformation",
            () => HtmlResponse("<html><title>Sign in</title></html>", HttpStatusCode.NonAuthoritativeInformation)
        },
        {
            "200 text/html",
            () => HtmlResponse("<html><title>Sign in</title></html>", HttpStatusCode.OK)
        },
        {
            "302 sign-in redirect",
            () =>
            {
                var response = HtmlResponse("", HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://spsprodcus4.vssps.visualstudio.com/_signin?realm=dev.azure.com");
                return response;
            }
        }
    };

    private static AzdoApiClient CreateClient(HttpMessageHandler handler)
    {
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns((AzdoCredential?)null);
        return new AzdoApiClient(new HttpClient(handler), tokenAccessor);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage HtmlResponse(string html, HttpStatusCode statusCode)
        => new(statusCode)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };

    private static HttpRequestException CreateAuthException()
        => new("Can't access org/proj — authentication required. Run 'az login' or set AZDO_TOKEN.", inner: null, statusCode: HttpStatusCode.Unauthorized);

    private static void AssertAuthGuidance(string name, HlxAcquisitionException ex)
    {
        Assert.Equal(AcquisitionErrorKind.AccessDenied, ex.Error.Kind);
        Assert.Equal("azdo", ex.Error.Provider);
        Assert.True(
            ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("sign", StringComparison.OrdinalIgnoreCase),
            $"{name} should produce auth guidance, but message was: {ex.Message}");
    }

    private static void AssertTopDoesNotExceedAzdoLimit(Uri uri)
    {
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = parameter.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) != "$top")
                continue;

            Assert.True(int.TryParse(parts[1], out var top), $"Could not parse $top in {uri}");
            Assert.InRange(top, 1, 10_000);
        }
    }

    private static int? GetQueryInt(Uri uri, string name)
    {
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = parameter.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) != name)
                continue;

            Assert.True(int.TryParse(parts[1], out var value), $"Could not parse {name} in {uri}");
            return value;
        }

        return null;
    }

    private static string BuildTestResultsPayload(int start, int count)
    {
        var builder = new StringBuilder("""{"value":[""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(',');
            var id = start + i;
            builder.Append(CultureInfo.InvariantCulture, $$"""{"id":{{id}},"testCaseTitle":"test {{id}}","outcome":"Failed"}""");
        }

        builder.Append(CultureInfo.InvariantCulture, $$"""],"count":{{count}}}""");
        return builder.ToString();
    }

    private static string BuildTestRunsPayload(int start, int count)
    {
        var builder = new StringBuilder("""{"value":[""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(',');
            var id = start + i;
            builder.Append(CultureInfo.InvariantCulture, $$"""{"id":{{id}},"name":"run {{id}}","state":"Completed","totalTests":1,"passedTests":0,"unanalyzedTests":1}""");
        }

        builder.Append(CultureInfo.InvariantCulture, $$"""],"count":{{count}}}""");
        return builder.ToString();
    }

    private sealed class SequenceHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(request));
        }
    }
}
