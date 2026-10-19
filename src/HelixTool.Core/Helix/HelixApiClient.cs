using System.Net;
using HelixTool.Core.Acquisition;
using Microsoft.DotNet.Helix.Client;
using Microsoft.DotNet.Helix.Client.Models;
using Newtonsoft.Json.Linq;

namespace HelixTool.Core.Helix;

/// <summary>
/// Default implementation of <see cref="IHelixApiClient"/> wrapping the Helix SDK.
/// This is the single instantiation point for <c>HelixApi</c> in the codebase (decision D2).
/// Registered as a singleton in both CLI and MCP hosts.
/// </summary>
public sealed class HelixApiClient : IHelixApiClient, IResolvedHelixFileClient
{
    private static readonly HttpClient s_defaultDownloadClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HelixApi _api;
    private readonly HttpClient _downloadClient;

    static HelixApiClient() => HelixToolUserAgent.Apply(s_defaultDownloadClient);

    public HelixApiClient(string? accessToken = null, HttpClient? downloadClient = null)
    {
        var options = !string.IsNullOrEmpty(accessToken)
            ? new HelixApiOptions(new HelixApiTokenCredential(accessToken))
            : new HelixApiOptions();
        HelixToolUserAgent.Apply(options);
        _api = new HelixApi(options);
        _downloadClient = downloadClient ?? s_defaultDownloadClient;
    }

    private HelixApiClient(HelixApi api, HttpClient? downloadClient)
    {
        _api = api;
        _downloadClient = downloadClient ?? s_defaultDownloadClient;
    }

    /// <summary>
    /// Test-only injection seam: constructs the client around caller-supplied
    /// <see cref="HelixApiOptions"/> (e.g. a fake <c>HttpPipelineTransport</c> and
    /// <c>Retry.MaxRetries = 0</c>) so tests can exercise the real <see cref="HelixApi"/> SDK and
    /// this class's exception classification without reflection into a private field.
    /// </summary>
    internal static HelixApiClient CreateForTesting(HelixApiOptions options, HttpClient? downloadClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HelixApiClient(new HelixApi(options), downloadClient);
    }

    /// <inheritdoc />
    public async Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("jobId", jobId));
        var details = await ClassifyAsync(() => _api.Job.DetailsAsync(jobId, ct), "get_helix_job", resource, ct);
        return new JobDetailsAdapter(details);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("jobId", jobId));
        var items = await ClassifyAsync(() => _api.WorkItem.ListAsync(jobId, ct), "list_helix_work_items", resource, ct);
        return items.Select(wi => (IWorkItemSummary)new WorkItemSummaryAdapter(wi)).ToList();
    }

    /// <inheritdoc />
    public async Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName));
        var details = await ClassifyAsync(() => _api.WorkItem.DetailsAsync(workItemName, jobId, ct), "get_helix_work_item", resource, ct);
        return new WorkItemDetailsAdapter(details);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName));
        var files = await ClassifyAsync(() => _api.WorkItem.ListFilesAsync(workItemName, jobId, cancellationToken: ct), "list_helix_work_item_files", resource, ct);
        return files.Select(f => (IWorkItemFile)new WorkItemFileAdapter(f)).ToList();
    }

    /// <inheritdoc />
    public Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName));
        return ClassifyAsync(() => _api.WorkItem.ConsoleLogAsync(workItemName, jobId, ct), "get_helix_console_log", resource, ct);
    }

    /// <inheritdoc />
    public Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName), ("fileName", fileName));
        return ClassifyAsync(async () =>
        {
            // The SDK's GetFile endpoint can redirect to a blob path on the Helix host,
            // returning HTML instead of file bytes. ListFiles provides the absolute blob URL.
            var files = await _api.WorkItem.ListFilesAsync(workItemName, jobId, cancellationToken: ct);
            var file = files.FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.Ordinal));
            if (file is null)
                throw new HttpRequestException("The requested uploaded file was not found.", null, HttpStatusCode.NotFound);

            return await GetFileAsync(new WorkItemFileAdapter(file), workItemName, jobId, ct);
        }, "download_helix_file", resource, ct);
    }

    /// <inheritdoc />
    public Task<Stream> GetFileAsync(IWorkItemFile file, string workItemName, string jobId, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("jobId", jobId), ("workItem", workItemName), ("fileName", file.Name));
        return ClassifyAsync(async () =>
        {
            if (!Uri.TryCreate(file.Link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.InvalidResponse, "helix", "download_helix_file", resource,
                    "Helix returned an uploaded file without an absolute HTTP download URL."));

            return await _downloadClient.GetStreamAsync(uri, ct);
        }, "download_helix_file", resource, ct);
    }

    /// <summary>
    /// Classifies raw SDK/HTTP exceptions from any Helix API call into <see cref="HlxAcquisitionException"/>
    /// at the client boundary, so decorators (caching) and callers never see provider-specific exception types.
    /// Caller cancellation (<paramref name="ct"/> signaled) still propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<T> ClassifyAsync<T>(
        Func<Task<T>> call,
        string operation,
        IReadOnlyDictionary<string, object?> resource,
        CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw HelixAcquisition.Timeout(ex, operation, resource);
        }
        catch (HttpRequestException ex)
        {
            throw HelixAcquisition.FromHttp(ex, operation, resource);
        }
        catch (RestApiException ex)
        {
            throw HelixAcquisition.FromRestApi(ex, operation, resource);
        }
        catch (Azure.RequestFailedException ex)
        {
            // The underlying Azure.Core pipeline can surface transport-level failures (e.g. a raw
            // HttpRequestException from the handler) wrapped as RequestFailedException instead of
            // the Helix SDK's own RestApiException. Classify those too so none of them escape as
            // an unclassified provider exception.
            throw HelixAcquisition.FromRequestFailed(ex, operation, resource);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IHelixJobSummary>> ListJobsByBuildAsync(
        string source, string buildId, int count = 100_000, CancellationToken ct = default)
    {
        var resource = HelixAcquisition.Resource(("source", source), ("buildId", buildId), ("count", count));
        // count: 100_000 cap mirrors the arcade HelixService reference implementation.
        // In practice a single AzDO build submits ~1k–5k Helix jobs; the cap is generous.
        var jobs = await ClassifyAsync(
            () => _api.Job.ListAsync(source: source, count: count, cancellationToken: ct),
            "list_helix_jobs_by_build", resource, ct);
        return jobs
            .Select(j => new JobSummaryAdapter(j))
            .Where(j => j.HasBuildId(buildId))
            .Select(j => (IHelixJobSummary)j)
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

    private sealed class JobSummaryAdapter(JobSummary summary) : IHelixJobSummary
    {
        private JObject? Properties => summary.Properties as JObject;

        public string Name => summary.Name;
        public string? QueueId => summary.QueueId;
        public string? Source => summary.Source;
        public string? Created => summary.Created;
        public string? Finished => summary.Finished;
        public int? InitialWorkItemCount => summary.InitialWorkItemCount;
        public string? FailureReason => summary.FailureReason.ToString();
        public string? PhaseName => GetProperty("System.PhaseName");
        public string? JobDisplayName => GetProperty("System.JobDisplayName");
        public string? JobName => GetProperty("System.JobName");
        public string? PreviousHelixJobName => GetProperty("PreviousHelixJobName");

        public bool HasBuildId(string buildId) =>
            string.Equals(GetProperty("BuildId"), buildId, StringComparison.Ordinal);

        private string? GetProperty(string name) =>
            Properties?.TryGetValue(name, out var value) == true
                ? value.Type == JTokenType.Null ? null : value.ToString()
                : null;
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
