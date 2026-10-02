using System.Net;
using HelixTool.Core.Acquisition;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.Helix;

public sealed class HelixAcquisitionErrorTests
{
    private const string JobId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly IHelixApiClient _api;
    private readonly HelixService _svc;

    public HelixAcquisitionErrorTests()
    {
        _api = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_api, new HttpClient());
    }

    [Fact]
    public async Task GetJobStatusAsync_Http404_ThrowsNotFoundWithHelixResource()
    {
        _api.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IJobDetails>(
                new HttpRequestException("missing", inner: null, statusCode: HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => _svc.GetJobStatusAsync(JobId));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "helix", "get_helix_job", 404);
        AcquisitionAssertions.Resource(error, "jobId", JobId);
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_Http403_ThrowsAccessDenied()
    {
        _api.ListWorkItemFilesAsync("work-item", JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<IWorkItemFile>>(
                new HttpRequestException("forbidden", inner: null, statusCode: HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            _svc.GetWorkItemFilesAsync(JobId, "work-item"));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.AccessDenied, "helix", "list_helix_work_item_files", 403);
        AcquisitionAssertions.Resource(error, "jobId", JobId);
        AcquisitionAssertions.Resource(error, "workItem", "work-item");
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_Http429_ThrowsRateLimitedWithRetryAfter()
    {
        _api.ListWorkItemFilesAsync("work-item", JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<IWorkItemFile>>(
                new HttpRequestException("rate limited", inner: null, statusCode: HttpStatusCode.TooManyRequests)));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            _svc.GetWorkItemFilesAsync(JobId, "work-item"));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.RateLimited, "helix", "list_helix_work_item_files", 429);
    }

    [Fact]
    public async Task DownloadConsoleLogAsync_TaskCanceledWithoutRequestedToken_ThrowsTimeout()
    {
        _api.GetConsoleLogAsync("work-item", JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Stream>(new TaskCanceledException("request timed out")));

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            _svc.DownloadConsoleLogAsync(JobId, "work-item"));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.Timeout, "helix", "get_helix_console_log");
    }

    [Fact]
    public async Task DownloadConsoleLogAsync_UserCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _api.GetConsoleLogAsync("work-item", JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromCanceled<Stream>(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _svc.DownloadConsoleLogAsync(JobId, "work-item", cts.Token));
    }

    [Fact]
    public async Task GetWorkItemFilesAsync_ListFilesEmpty_ReturnsEmptySuccess()
    {
        _api.ListWorkItemFilesAsync("work-item", JobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IWorkItemFile>>([]));

        var files = await _svc.GetWorkItemFilesAsync(JobId, "work-item");

        Assert.Empty(files);
    }
}
