using System.Net;
using HelixTool.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests;

public class HelixAuthTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    // --- HelixApiClient constructor: no exceptions for various token values ---

    [Fact]
    public void Constructor_NullToken_DoesNotThrow()
    {
        var client = new HelixApiClient(null);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_EmptyToken_DoesNotThrow()
    {
        var client = new HelixApiClient("");
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_WhitespaceToken_DoesNotThrow()
    {
        var client = new HelixApiClient("   ");
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_FakeToken_DoesNotThrow()
    {
        var client = new HelixApiClient("fake-access-token-12345");
        Assert.NotNull(client);
    }

    // --- HelixService: 401 Unauthorized → HelixException with "Access denied" and "HELIX_ACCESS_TOKEN" ---

    [Fact]
    public async Task GetJobStatusAsync_Unauthorized_ThrowsHelixExceptionWithAccessDeniedMessage()
    {
        var mockApi = Substitute.For<IHelixApiClient>();
        var svc = new HelixService(mockApi);

        mockApi.GetJobDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => svc.GetJobStatusAsync(ValidJobId));

        Assert.Contains("Access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HELIX_ACCESS_TOKEN", ex.Message);
    }

    [Fact]
    public async Task GetJobStatusAsync_Forbidden_ThrowsHelixExceptionWithAccessDeniedMessage()
    {
        var mockApi = Substitute.For<IHelixApiClient>();
        var svc = new HelixService(mockApi);

        mockApi.GetJobDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => svc.GetJobStatusAsync(ValidJobId));

        Assert.Contains("Access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HELIX_ACCESS_TOKEN", ex.Message);
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_Unauthorized_ThrowsHelixExceptionWithAccessDeniedMessage()
    {
        var mockApi = Substitute.For<IHelixApiClient>();
        var svc = new HelixService(mockApi);

        mockApi.ListWorkItemFilesAsync("wi1", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => svc.GetWorkItemFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("Access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HELIX_ACCESS_TOKEN", ex.Message);
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_Forbidden_ThrowsHelixExceptionWithAccessDeniedMessage()
    {
        var mockApi = Substitute.For<IHelixApiClient>();
        var svc = new HelixService(mockApi);

        mockApi.ListWorkItemFilesAsync("wi1", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => svc.GetWorkItemFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("Access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HELIX_ACCESS_TOKEN", ex.Message);
    }

    // --- Verify inner exception is preserved ---

    [Fact]
    public async Task GetJobStatusAsync_Unauthorized_PreservesInnerException()
    {
        var mockApi = Substitute.For<IHelixApiClient>();
        var svc = new HelixService(mockApi);

        var inner = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        mockApi.GetJobDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(inner);

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => svc.GetJobStatusAsync(ValidJobId));

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_Forbidden_PreservesInnerException()
    {
        var mockApi = Substitute.For<IHelixApiClient>();
        var svc = new HelixService(mockApi);

        var inner = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);
        mockApi.ListWorkItemFilesAsync("wi1", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(inner);

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => svc.GetWorkItemFilesAsync(ValidJobId, "wi1"));

        Assert.Same(inner, ex.InnerException);
    }
}
