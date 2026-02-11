// NOTE: These tests depend on IHelixApiClient and HelixException from Ripley's P0 implementation.
// They will compile once the interface and exception type are in HelixTool.Core.
// Tests are written against the design review decisions D1-D8.

using System.Net;
using HelixTool.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests;

public class HelixServiceDITests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public HelixServiceDITests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    // --- Happy path: GetJobStatusAsync aggregates job details + work items ---

    [Fact]
    public async Task GetJobStatusAsync_HappyPath_ReturnsAggregatedSummary()
    {
        // Arrange: mock job details
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/12345");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        // Arrange: mock work item list
        var wi1 = Substitute.For<IWorkItemSummary>();
        wi1.Name.Returns("workitem1");
        var wi2 = Substitute.For<IWorkItemSummary>();
        wi2.Name.Returns("workitem2");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi1, wi2 });

        // Arrange: mock work item details
        var detail1 = Substitute.For<IWorkItemDetails>();
        detail1.ExitCode.Returns(0);
        detail1.State.Returns("Finished");
        detail1.MachineName.Returns("helix-win-01");
        detail1.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail1.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 2, 34, TimeSpan.Zero));
        var detail2 = Substitute.For<IWorkItemDetails>();
        detail2.ExitCode.Returns(1);
        detail2.State.Returns("Finished");
        detail2.MachineName.Returns("helix-linux-03");
        detail2.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail2.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 45, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync("workitem1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail1);
        _mockApi.GetWorkItemDetailsAsync("workitem2", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail2);

        // Act
        var result = await _svc.GetJobStatusAsync(ValidJobId);

        // Assert
        Assert.Equal("test-job", result.Name);
        Assert.Equal("windows.10.amd64", result.QueueId);
        Assert.Equal(2, result.TotalCount);
        Assert.Single(result.Passed);
        Assert.Single(result.Failed);
        Assert.Equal("workitem1", result.Passed[0].Name);
        Assert.Equal("workitem2", result.Failed[0].Name);
        Assert.Equal(1, result.Failed[0].ExitCode);
    }

    // --- Error: 404 HttpRequestException → HelixException with "not found" ---

    [Fact]
    public async Task GetJobStatusAsync_NotFound_ThrowsHelixExceptionWithNotFoundMessage()
    {
        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetJobStatusAsync(ValidJobId));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Error: non-404 HttpRequestException → HelixException with "API error" ---

    [Fact]
    public async Task GetJobStatusAsync_ServerError_ThrowsHelixExceptionWithApiErrorMessage()
    {
        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetJobStatusAsync(ValidJobId));

        Assert.Contains("API error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Error: TaskCanceledException (timeout, not cancellation) → HelixException with "timed out" ---

    [Fact]
    public async Task GetJobStatusAsync_Timeout_ThrowsHelixExceptionWithTimedOutMessage()
    {
        // TaskCanceledException with a non-matching CancellationToken = HTTP timeout
        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetJobStatusAsync(ValidJobId));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Cancellation: canceled CancellationToken → OperationCanceledException propagates ---

    [Fact]
    public async Task GetJobStatusAsync_Cancellation_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("canceled", null, cts.Token));

        // Per D6: cancellation propagates as OperationCanceledException (or subclass)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _svc.GetJobStatusAsync(ValidJobId, cts.Token));
    }

    // --- Input validation: null/empty/whitespace jobId → ArgumentException ---

    [Fact]
    public async Task GetJobStatusAsync_NullJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetJobStatusAsync(null!));
    }

    [Fact]
    public async Task GetJobStatusAsync_EmptyJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetJobStatusAsync(""));
    }

    [Fact]
    public async Task GetJobStatusAsync_WhitespaceJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetJobStatusAsync("   "));
    }

    // --- Input validation: workItem parameter ---

    [Fact]
    public async Task GetWorkItemFilesAsync_NullWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetWorkItemFilesAsync(ValidJobId, null!));
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_EmptyWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetWorkItemFilesAsync(ValidJobId, ""));
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_WhitespaceWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetWorkItemFilesAsync(ValidJobId, "   "));
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_NullWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetConsoleLogContentAsync(ValidJobId, null!));
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_EmptyWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetConsoleLogContentAsync(ValidJobId, ""));
    }

    // --- Constructor rejects null IHelixApiClient ---

    [Fact]
    public void Constructor_NullApiClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new HelixService(null!));
    }

    // --- Error handling on GetWorkItemFilesAsync ---

    [Fact]
    public async Task GetWorkItemFilesAsync_NotFound_ThrowsHelixExceptionWithNotFoundMessage()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetWorkItemFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_ServerError_ThrowsHelixExceptionWithApiErrorMessage()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetWorkItemFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("API error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
