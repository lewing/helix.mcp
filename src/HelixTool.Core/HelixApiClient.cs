using Microsoft.DotNet.Helix.Client;
using Microsoft.DotNet.Helix.Client.Models;

namespace HelixTool.Core;

/// <summary>
/// Default implementation of <see cref="IHelixApiClient"/> wrapping the Helix SDK.
/// This is the single instantiation point for <c>HelixApi</c> in the codebase (decision D2).
/// Registered as a singleton in both CLI and MCP hosts.
/// </summary>
public sealed class HelixApiClient : IHelixApiClient
{
    private readonly HelixApi _api;

    public HelixApiClient(string? accessToken = null)
    {
        _api = !string.IsNullOrEmpty(accessToken)
            ? new HelixApi(new HelixApiOptions(new HelixApiTokenCredential(accessToken)))
            : new HelixApi();
    }

    /// <inheritdoc />
    public async Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default)
    {
        var details = await _api.Job.DetailsAsync(jobId, ct);
        return new JobDetailsAdapter(details);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default)
    {
        var items = await _api.WorkItem.ListAsync(jobId, ct);
        return items.Select(wi => (IWorkItemSummary)new WorkItemSummaryAdapter(wi)).ToList();
    }

    /// <inheritdoc />
    public async Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        var details = await _api.WorkItem.DetailsAsync(workItemName, jobId, ct);
        return new WorkItemDetailsAdapter(details);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        var files = await _api.WorkItem.ListFilesAsync(workItemName, jobId, cancellationToken: ct);
        return files.Select(f => (IWorkItemFile)new WorkItemFileAdapter(f)).ToList();
    }

    /// <inheritdoc />
    public Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default)
        => _api.WorkItem.ConsoleLogAsync(workItemName, jobId, ct);

    /// <inheritdoc />
    public Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default)
        => _api.WorkItem.GetFileAsync(fileName, workItemName, jobId, cancellationToken: ct);

    // Adapters to bridge SDK concrete types to our mockable interfaces

    private sealed class JobDetailsAdapter(JobDetails details) : IJobDetails
    {
        public string? Name => details.Name;
        public string? QueueId => details.QueueId;
        public string? Creator => details.Creator;
        public string? Source => details.Source;
        public string? Created => details.Created;
        public string? Finished => details.Finished;
    }

    private sealed class WorkItemSummaryAdapter(WorkItemSummary summary) : IWorkItemSummary
    {
        public string Name => summary.Name;
    }

    private sealed class WorkItemDetailsAdapter(WorkItemDetails details) : IWorkItemDetails
    {
        public int? ExitCode => details.ExitCode;
        public string? State => details.State;
        public string? MachineName => details.MachineName;
        public DateTimeOffset? Started => details.Started;
        public DateTimeOffset? Finished => details.Finished;
    }

    private sealed class WorkItemFileAdapter(UploadedFile file) : IWorkItemFile
    {
        public string Name => file.Name;
        public string? Link => file.Link;
    }
}
