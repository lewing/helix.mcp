using HelixTool.Core.Acquisition;
using Microsoft.DotNet.Helix.Client.Models;

namespace HelixTool.Core.Helix;

/// <summary>
/// Eval-mode stub for <see cref="IHelixApiClient"/>.
/// Every method throws <see cref="HlxAcquisitionException"/> so snapshot misses in eval mode
/// surface as explicit <see cref="AcquisitionErrorKind.NotInSnapshot"/> errors rather than
/// silently falling through to live Helix.
/// </summary>
public sealed class OfflineHelixApiClient : IHelixApiClient
{
    private static HlxAcquisitionException Blocked(
        string operation,
        IReadOnlyDictionary<string, object?> resource)
        => new(AcquisitionErrorFactory.Create(
            AcquisitionErrorKind.NotInSnapshot,
            "cache",
            operation,
            resource,
            $"Snapshot does not contain cache entry for {operation}.") with
        {
            Source = "snapshot"
        });

    public Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default)
        => throw Blocked("get_helix_job", HelixAcquisition.Resource(("jobId", jobId)));

    public Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default)
        => throw Blocked("list_helix_work_items", HelixAcquisition.Resource(("jobId", jobId)));

    public Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked("get_helix_work_item", HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName)));

    public Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked("list_helix_work_item_files", HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName)));

    public Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked("get_helix_console_log", HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName)));

    public Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked("download_helix_file", HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName), ("fileName", fileName)));

    /// <inheritdoc />
    public Task<IReadOnlyList<IHelixJobSummary>> ListJobsByBuildAsync(
        string source, string buildId, int count = 100_000, CancellationToken ct = default)
        => throw Blocked("list_helix_jobs_by_build", HelixAcquisition.Resource(("source", source), ("buildId", buildId), ("count", count)));
}
