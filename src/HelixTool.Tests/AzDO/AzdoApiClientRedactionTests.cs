// Tests for AzdoApiClient error-body redaction behavior.

using System.Net;
using System.Text;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoApiClientRedactionTests
{
    [Fact]
    public async Task GetBuildAsync_500_RedactsJwtLikeContent()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature_value_123";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient($"Request failed with token {jwt}").GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains("[REDACTED-JWT]", ex.Message);
        Assert.DoesNotContain(jwt, ex.Message);
    }

    [Theory]
    [InlineData("token", "token-value-123")]
    [InlineData("key", "key-value-123")]
    [InlineData("password", "password-value-123")]
    [InlineData("secret", "secret-value-123")]
    public async Task GetBuildAsync_500_RedactsSecretAssignments(string name, string value)
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient($"Auth failure: {name}={value}").GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains($"{name}=[REDACTED]", ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public async Task GetBuildAsync_500_RedactsLongBase64LikeContent()
    {
        const string base64Secret = "QWxhZGRpbjpPcGVuU2VzYW1lMTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient($"Credential blob: {base64Secret}").GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains("[REDACTED-SECRET]", ex.Message);
        Assert.DoesNotContain(base64Secret, ex.Message);
    }

    [Fact]
    public async Task GetBuildAsync_500_PreservesNormalErrorMessages()
    {
        const string body = "Build validation failed for pipeline 123 because reason=timeout.";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(body).GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains(body, ex.Message);
        Assert.DoesNotContain("[REDACTED]", ex.Message);
        Assert.DoesNotContain("[REDACTED-JWT]", ex.Message);
        Assert.DoesNotContain("[REDACTED-SECRET]", ex.Message);
    }

    [Fact]
    public async Task GetBuildAsync_500_RedactsSensitiveSegmentsAndKeepsNormalContent()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature_value_123";
        const string base64Secret = "QWxhZGRpbjpPcGVuU2VzYW1lMTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";
        var body = $"Failed deployment for run 42; token=abc123; detail=missing permission; payload={base64Secret}; session={jwt}";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(body).GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains("Failed deployment for run 42", ex.Message);
        Assert.Contains("detail=missing permission", ex.Message);
        Assert.Contains("token=[REDACTED]", ex.Message);
        Assert.Contains("payload=[REDACTED-SECRET]", ex.Message);
        Assert.Contains("session=[REDACTED-JWT]", ex.Message);
        Assert.DoesNotContain("abc123", ex.Message);
        Assert.DoesNotContain(base64Secret, ex.Message);
        Assert.DoesNotContain(jwt, ex.Message);
    }

    private static AzdoApiClient CreateClient(string responseContent)
    {
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns((AzdoCredential?)null);
        return new AzdoApiClient(new HttpClient(new StaticResponseHandler(responseContent)), tokenAccessor);
    }

    private sealed class StaticResponseHandler(string responseContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "text/plain")
            });
    }
}
