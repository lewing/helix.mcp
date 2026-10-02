using System.Net;
using HelixTool.Core.Acquisition;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.Helix;

public sealed class HelixOperationClassificationRegressionTests
{
    private const string JobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItem = "System.Runtime.Tests";

    [Fact]
    public async Task GetJobStatusAsync_ListWorkItemsFailure_ReportsListWorkItemsOperation()
    {
        var api = Substitute.For<IHelixApiClient>();
        var job = Substitute.For<IJobDetails>();
        api.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>()).Returns(job);
        api.ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<IWorkItemSummary>>(
                new HttpRequestException("missing work items", null, HttpStatusCode.NotFound)));
        var service = new HelixService(api, new HttpClient());

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => service.GetJobStatusAsync(JobId));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "helix", "list_helix_work_items", 404);
        AcquisitionAssertions.Resource(error, "jobId", JobId);
        Assert.False(error.Resource.ContainsKey("workItem"));
    }

    [Fact]
    public async Task GetJobStatusAsync_WorkItemDetailsFailure_ReportsWorkItemOperationAndResource()
    {
        var api = Substitute.For<IHelixApiClient>();
        var job = Substitute.For<IJobDetails>();
        var summary = Substitute.For<IWorkItemSummary>();
        summary.Name.Returns(WorkItem);
        summary.ExitCode.Returns(1);
        api.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>()).Returns(job);
        api.ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { summary });
        api.GetWorkItemDetailsAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IWorkItemDetails>(
                new HttpRequestException("work item details failed", null, HttpStatusCode.Forbidden)));
        var service = new HelixService(api, new HttpClient());

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => service.GetJobStatusAsync(JobId));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.AccessDenied, "helix", "get_helix_work_item", 403);
        AcquisitionAssertions.Resource(error, "jobId", JobId);
        AcquisitionAssertions.Resource(error, "workItem", WorkItem);
    }

    [Fact]
    public async Task DownloadFilesAsync_GetFileFailure_ReportsDownloadFileOperationAndResource()
    {
        var api = Substitute.For<IHelixApiClient>();
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns("results.trx");
        file.Link.Returns("https://helix.dot.net/files/results.trx");
        api.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });
        api.GetFileAsync("results.trx", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Stream>(
                new HttpRequestException("file download failed", null, HttpStatusCode.NotFound)));
        var service = new HelixService(api, new HttpClient());

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => service.DownloadFilesAsync(JobId, WorkItem, "*.trx"));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "helix", "download_helix_file", 404);
        AcquisitionAssertions.Resource(error, "jobId", JobId);
        AcquisitionAssertions.Resource(error, "workItem", WorkItem);
        AcquisitionAssertions.Resource(error, "fileName", "results.trx");
    }

    [Fact]
    public async Task ParseTrxResultsAsync_DownloadMatchingFilesFailure_ReportsDownloadFileOperationAndResource()
    {
        var api = Substitute.For<IHelixApiClient>();
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns("results.trx");
        file.Link.Returns("https://helix.dot.net/files/results.trx");
        api.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });
        api.GetFileAsync("results.trx", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Stream>(
                new HttpRequestException("denied", null, HttpStatusCode.Forbidden)));
        var service = new HelixService(api, new HttpClient());

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => service.ParseTrxResultsAsync(JobId, WorkItem));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.AccessDenied, "helix", "download_helix_file", 403);
        AcquisitionAssertions.Resource(error, "jobId", JobId);
        AcquisitionAssertions.Resource(error, "workItem", WorkItem);
        AcquisitionAssertions.Resource(error, "fileName", "results.trx");
    }
}
