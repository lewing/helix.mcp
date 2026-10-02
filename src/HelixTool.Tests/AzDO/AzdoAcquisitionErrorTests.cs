using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class AzdoAcquisitionErrorTests
{
    [Fact]
    public async Task GetBuildLogAsync_Empty200_ReturnsEmptyString()
    {
        var client = CreateClient(_ => TextResponse(string.Empty));

        var content = await client.GetBuildLogAsync("dnceng-public", "public", 12345, 7);

        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public async Task GetBuildLogAsync_404_ThrowsNotFoundWithResource()
    {
        var client = CreateClient(_ => JsonResponse("""{"message":"missing"}""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildLogAsync("dnceng-public", "public", 12345, 7));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "azdo", "get_build_log", 404);
        AcquisitionAssertions.Resource(error, "org", "dnceng-public");
        AcquisitionAssertions.Resource(error, "project", "public");
        AcquisitionAssertions.Resource(error, "buildId", 12345);
        AcquisitionAssertions.Resource(error, "logId", 7);
    }

    [Fact]
    public async Task GetTimelineAsync_204_ThrowsNotFoundNoContent()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetTimelineAsync("dnceng-public", "public", 12345));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "azdo", "get_timeline", 204);
        AcquisitionAssertions.Resource(error, "buildId", 12345);
    }

    [Fact]
    public async Task GetBuildChangesAsync_204_ThrowsInvalidResponseForListEndpoint()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildChangesAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "azdo", "list_build_changes", 204);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    public async Task GetBuildAsync_AuthFailures_ThrowAccessDeniedWithStatus(HttpStatusCode statusCode, int expectedStatus)
    {
        var client = CreateClient(_ => JsonResponse("""{"message":"auth"}""", statusCode));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.AccessDenied, "azdo", "get_build", expectedStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, AcquisitionErrorKind.NotFound, 404)]
    [InlineData(HttpStatusCode.RequestTimeout, AcquisitionErrorKind.Timeout, 408)]
    [InlineData(HttpStatusCode.GatewayTimeout, AcquisitionErrorKind.Timeout, 504)]
    [InlineData(HttpStatusCode.InternalServerError, AcquisitionErrorKind.TransportError, 500)]
    [InlineData(HttpStatusCode.BadGateway, AcquisitionErrorKind.TransportError, 502)]
    public async Task GetBuildAsync_ClassifiesHttpStatusRows(HttpStatusCode statusCode, AcquisitionErrorKind kind, int expectedStatus)
    {
        var client = CreateClient(_ => JsonResponse("""{"message":"provider failure"}""", statusCode));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, kind, "azdo", "get_build", expectedStatus);
    }

    [Fact]
    public async Task GetBuildLogAsync_429WithRetryAfterSeconds_ThrowsRateLimited()
    {
        var client = CreateClient(_ =>
        {
            var response = JsonResponse("""{"message":"slow down"}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildLogAsync("dnceng-public", "public", 12345, 7));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.RateLimited, "azdo", "get_build_log", 429, 30);
    }

    [Fact]
    public async Task GetBuildLogAsync_429WithRetryAfterHttpDate_ThrowsRateLimited()
    {
        var client = CreateClient(_ =>
        {
            var response = JsonResponse("""{"message":"slow down"}""", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(60));
            return response;
        });

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildLogAsync("dnceng-public", "public", 12345, 7));

        var error = ex.Error;
        Assert.Equal(AcquisitionErrorKind.RateLimited, error.Kind);
        Assert.Equal("azdo", error.Provider);
        Assert.Equal("get_build_log", error.Operation);
        Assert.Equal(429, error.HttpStatus);
        Assert.InRange(error.RetryAfterSeconds.GetValueOrDefault(), 1, 120);
    }

    [Fact]
    public async Task GetBuildLogAsync_429WithoutRetryAfter_ThrowsRateLimitedWithoutRetryAfter()
    {
        var client = CreateClient(_ => JsonResponse("""{"message":"slow down"}""", HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildLogAsync("dnceng-public", "public", 12345, 7));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.RateLimited, "azdo", "get_build_log", 429);
    }

    [Fact]
    public async Task GetBuildAsync_HttpRequestExceptionWithoutStatus_ThrowsTransportError()
    {
        var client = CreateClient(_ => throw new HttpRequestException("DNS failure"));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.TransportError, "azdo", "get_build");
    }

    [Fact]
    public async Task GetBuildAsync_TaskCanceledWithoutRequestedToken_ThrowsTimeout()
    {
        var client = CreateClient(_ => throw new TaskCanceledException("request timed out"));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.Timeout, "azdo", "get_build");
    }

    [Fact]
    public async Task GetBuildAsync_TaskCanceledWithRequestedToken_PropagatesCancellation()
    {
        var client = CreateClient((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetBuildAsync("dnceng-public", "public", 12345, cts.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    public async Task GetTimelineAsync_MalformedOrEmptyJson_ThrowsInvalidResponse(string body)
    {
        var client = CreateClient(_ => JsonResponse(body));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetTimelineAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "azdo", "get_timeline", 200);
    }

    [Fact]
    public async Task GetTimelineAsync_MissingRequiredFields_ThrowsInvalidResponse()
    {
        var client = CreateClient(_ => JsonResponse("""{"id":"timeline-without-records"}"""));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetTimelineAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "azdo", "get_timeline", 200);
    }

    [Fact]
    public async Task GetBuildAsync_WrongContentType_ThrowsInvalidResponse()
    {
        var client = CreateClient(_ => TextResponse("""{"id":12345}""", "text/plain"));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "azdo", "get_build", 200);
    }

    [Fact]
    public async Task GetBuildChangesAsync_ValidEmptyValue_ReturnsEmptyList()
    {
        var client = CreateClient(_ => JsonResponse("""{"count":0,"value":[]}"""));

        var changes = await client.GetBuildChangesAsync("dnceng-public", "public", 12345);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task GetBuildChangesAsync_404_ThrowsNotFoundNotEmptyList()
    {
        var client = CreateClient(_ => JsonResponse("""{"message":"missing"}""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildChangesAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "azdo", "list_build_changes", 404);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"count":0}""")]
    public async Task GetBuildChangesAsync_EmptyOrMissingValueWrapper_ThrowsInvalidResponse(string body)
    {
        var client = CreateClient(_ => JsonResponse(body));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildChangesAsync("dnceng-public", "public", 12345));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "azdo", "list_build_changes", 200);
    }

    [Fact]
    public async Task CachingAzdoApiClient_DoesNotStoreAcquisitionFailuresAsSuccess()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        inner.GetBuildLogAsync("org", "proj", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string?>(AcquisitionAssertions.Exception(
                AcquisitionErrorKind.NotFound,
                "azdo",
                "get_build_log",
                new Dictionary<string, object?> { ["buildId"] = 1, ["logId"] = 5 },
                404)));
        var sut = new CachingAzdoApiClient(inner, cache, new CacheOptions { MaxSizeBytes = 1024 * 1024 });

        await Assert.ThrowsAsync<HlxAcquisitionException>(() => sut.GetBuildLogAsync("org", "proj", 1, 5));
        await Assert.ThrowsAsync<HlxAcquisitionException>(() => sut.GetBuildLogAsync("org", "proj", 1, 5));

        await inner.Received(2).GetBuildLogAsync("org", "proj", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SetMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachingAzdoApiClient_EvalCorruptJson_ThrowsCacheInvalidResponse()
    {
        var cache = Substitute.For<ICacheStore>();
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("{not json");
        var sut = new CachingAzdoApiClient(
            new OfflineAzdoApiClient(),
            cache,
            new CacheOptions { MaxSizeBytes = 1024 * 1024, EvalMode = true });

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            sut.GetTimelineAsync("org", "proj", 1));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "cache", "deserialize_cache_entry");
    }

    private static AzdoApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => CreateClient((request, _) => Task.FromResult(responseFactory(request)));

    private static AzdoApiClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    {
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new AzdoCredential("test-token", "Bearer", "test credential"));
        return new AzdoApiClient(new HttpClient(new LambdaHttpMessageHandler(responseFactory)), tokenAccessor);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage TextResponse(string text, string mediaType = "text/plain")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, mediaType)
        };

    private sealed class LambdaHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responseFactory(request, cancellationToken);
    }
}
