using Microsoft.DotNet.Helix.Client.Models;

namespace HelixTool.Core.Helix;

/// <summary>
/// Eval-mode stub for <see cref="IHelixApiClient"/>.
/// Every method throws <see cref="InvalidOperationException"/> so that cache misses in eval mode
/// surface as an explicit, descriptive error rather than silently falling through to live Helix.
/// </summary>
public sealed class OfflineHelixApiClient : IHelixApiClient
{
    private static InvalidOperationException Blocked() =>
        new("Network blocked: eval mode. Cache key not found in snapshot.");

    public Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default)
        => throw Blocked();

    public Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked();

    public Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked();

    public Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default)
        => throw Blocked();

    /// <inheritdoc />
    public Task<IReadOnlyList<IHelixJobSummary>> ListJobsByBuildAsync(
        string source, string buildId, int count = 100_000, CancellationToken ct = default)
        => throw Blocked();
}
