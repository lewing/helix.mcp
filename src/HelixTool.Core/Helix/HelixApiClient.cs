using Microsoft.DotNet.Helix.Client;
using Microsoft.DotNet.Helix.Client.Models;

namespace HelixTool.Core.Helix;

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
        var options = !string.IsNullOrEmpty(accessToken)
            ? new HelixApiOptions(new HelixApiTokenCredential(accessToken))
            : new HelixApiOptions();
        HelixToolUserAgent.Apply(options);
        _api = new HelixApi(options);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListJobNamesByBuildAsync(
        string source, string buildId, int count = 100_000, CancellationToken ct = default)
    {
        // count: 100_000 cap mirrors the arcade HelixService reference implementation.
        // In practice a single AzDO build submits ~1k–5k Helix jobs; the cap is generous.
        var jobs = await _api.Job.ListAsync(source: source, count: count, cancellationToken: ct);
        return jobs
            .Where(j => j.Properties is Newtonsoft.Json.Linq.JObject props
                && props.TryGetValue("BuildId", out var id)
                && id.ToString() == buildId)
            .Select(j => j.Name)
            .ToList();
    }

    // Adapters to bridge SDK concrete types to our mockable interfaces

    private sealed class JobDetailsAdapter(JobDetails details) : IJobDetails
    {
        public string? Name => details.Name;
        public string? QueueId => details.QueueId;
        public string? QueueAlias => details.QueueAlias;
        public string? Creator => details.Creator;
        public string? Source => details.Source;
        public string? Created => details.Created;
        public string? Finished => details.Finished;
        public string? DockerTag => details.DockerTag;
    }

    private sealed class WorkItemSummaryAdapter(WorkItemSummary summary) : IWorkItemSummary
    {
        public string Name => summary.Name;
        public int? ExitCode => summary.ExitCode;
        public string? ConsoleOutputUri => summary.ConsoleOutputUri;
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
