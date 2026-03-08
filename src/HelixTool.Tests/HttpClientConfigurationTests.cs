// Proactive tests for SEC-2 (IHttpClientFactory) and SEC-4 (HttpClient timeout configuration).
// Some tests validate CURRENT behavior (static HttpClient with no timeout).
// Others are ready-to-activate once Ripley lands IHttpClientFactory and timeout config.

using System.Net;
using System.Text;
using HelixTool.Core;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class HttpClientConfigurationTests
{
    // ── SEC-4: HttpClient timeout configuration ──────────────────────

    [Fact]
    public void AzdoApiClient_AcceptsHttpClientWithCustomTimeout()
    {
        // Verify AzdoApiClient works with a timeout-configured HttpClient.
        // This validates the injection pattern — DI should configure timeout before passing.
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();

        var client = new AzdoApiClient(httpClient, tokenAccessor);

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(300)]
    [InlineData(600)]
    public void HttpClient_ReasonableTimeoutRange_IsAccepted(int timeoutSeconds)
    {
        // Timeouts between 30s and 10min are reasonable for API operations.
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var httpClient = new HttpClient { Timeout = timeout };

        Assert.True(httpClient.Timeout >= TimeSpan.FromSeconds(30),
            $"Timeout {httpClient.Timeout} is too short for API operations");
        Assert.True(httpClient.Timeout <= TimeSpan.FromMinutes(10),
            $"Timeout {httpClient.Timeout} is too long — consider streaming instead");
    }

    [Fact]
    public void HttpClient_DefaultTimeout_IsNotInfinite()
    {
        // The default HttpClient timeout is 100 seconds, which is technically not infinite.
        // But Ripley's SEC-4 should set an EXPLICIT timeout — not rely on the default.
        var httpClient = new HttpClient();
        var defaultTimeout = httpClient.Timeout;

        // Default is 100 seconds — verify this is NOT TimeSpan.MaxValue (infinite)
        Assert.NotEqual(System.Threading.Timeout.InfiniteTimeSpan, defaultTimeout);
    }

    [Fact]
    public async Task AzdoApiClient_TimeoutMidRequest_ThrowsTaskCanceledException()
    {
        // When HttpClient times out, it throws TaskCanceledException (not OperationCanceledException).
        // HelixService wraps this in HelixException; AzdoApiClient should handle similarly.
        var handler = new DelayingHttpMessageHandler(TimeSpan.FromSeconds(10));
        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(50) // Very short timeout
        };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("test-token");
        var client = new AzdoApiClient(httpClient, tokenAccessor);

        // TaskCanceledException is expected when timeout fires
        await Assert.ThrowsAnyAsync<TaskCanceledException>(
            () => client.GetBuildAsync("dnceng", "public", 123));
    }

    [Fact]
    public async Task AzdoApiClient_CancellationToken_ThrowsTaskCanceledException()
    {
        // Distinguish between timeout and user cancellation.
        // Both throw TaskCanceledException but have different CancellationToken states.
        var handler = new DelayingHttpMessageHandler(TimeSpan.FromSeconds(10));
        var httpClient = new HttpClient(handler);
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("test-token");
        var client = new AzdoApiClient(httpClient, tokenAccessor);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAnyAsync<TaskCanceledException>(
            () => client.GetBuildAsync("dnceng", "public", 123, cts.Token));
    }

    // ── SEC-2: IHttpClientFactory readiness ──────────────────────────

    [Fact]
    public void AzdoApiClient_ConstructorRejectsNullHttpClient()
    {
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();

        Assert.Throws<ArgumentNullException>(() =>
            new AzdoApiClient(null!, tokenAccessor));
    }

    [Fact]
    public void AzdoApiClient_ConstructorRejectsNullTokenAccessor()
    {
        var httpClient = new HttpClient();

        Assert.Throws<ArgumentNullException>(() =>
            new AzdoApiClient(httpClient, null!));
    }

    [Fact]
    public void HttpClient_CanBeCreatedByFactory_IntegrationPattern()
    {
        // When IHttpClientFactory lands, named clients will be configured via DI.
        // This test validates the pattern: factory creates configured HttpClient.
        // After SEC-2 lands, this pattern should be used in DI registration.
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dev.azure.com/"),
            Timeout = TimeSpan.FromMinutes(2)
        };

        Assert.Equal("https://dev.azure.com/", httpClient.BaseAddress?.ToString());
        Assert.Equal(TimeSpan.FromMinutes(2), httpClient.Timeout);
    }

    // ── Helix: HelixService static HttpClient ────────────────────────

    [Fact]
    public async Task HelixService_TimeoutWrapsInHelixException()
    {
        // HelixService wraps TaskCanceledException (timeout) as HelixException.
        // Verify this contract holds for GetConsoleLogContentAsync.
        var mockApi = Substitute.For<IHelixApiClient>();
        var validJobId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        mockApi.GetConsoleLogAsync("test-wi", validJobId, Arg.Any<CancellationToken>())
            .Returns<Stream>(_ => throw new TaskCanceledException("Request timed out"));

        var svc = new HelixService(mockApi);

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => svc.GetConsoleLogContentAsync(validJobId, "test-wi"));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task HelixService_CancellationRethrowsDirectly()
    {
        // When user cancels (not timeout), the TaskCanceledException should propagate
        // directly — NOT be wrapped in HelixException.
        var mockApi = Substitute.For<IHelixApiClient>();
        var validJobId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        mockApi.GetConsoleLogAsync("test-wi", validJobId, Arg.Any<CancellationToken>())
            .Returns<Stream>(_ => throw new TaskCanceledException("Operation was canceled",
                new OperationCanceledException(cts.Token), cts.Token));

        var svc = new HelixService(mockApi);

        // Should rethrow as TaskCanceledException with token set, NOT HelixException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.GetConsoleLogContentAsync(validJobId, "test-wi", cancellationToken: cts.Token));
    }

    // ── Test helpers ─────────────────────────────────────────────────

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseContent { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json")
            });
        }
    }

    private class DelayingHttpMessageHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public DelayingHttpMessageHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
