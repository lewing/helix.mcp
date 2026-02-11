using Microsoft.DotNet.Helix.Client.Models;

namespace HelixTool.Core;

/// <summary>
/// Thin abstraction over the Helix SDK API calls used by <see cref="HelixService"/>.
/// This is the only mockable boundary for unit testing (decision D9).
/// </summary>
public interface IHelixApiClient
{
    /// <summary>Retrieve top-level details (name, queue, creator, timestamps) for a Helix job.</summary>
    Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default);

    /// <summary>List all work item summaries belonging to a Helix job.</summary>
    Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default);

    /// <summary>Get execution details (exit code) for a single work item.</summary>
    Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default);

    /// <summary>
    /// List uploaded files for a work item using the <c>ListFiles</c> endpoint.
    /// This avoids the broken URIs from the <c>Details</c> endpoint (dnceng#6072).
    /// </summary>
    Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default);

    /// <summary>Get the console log stream for a work item.</summary>
    Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default);

    /// <summary>Download a specific uploaded file by name from a work item.</summary>
    Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default);
}

/// <summary>Mockable projection of Helix SDK JobDetails.</summary>
public interface IJobDetails
{
    string? Name { get; }
    string? QueueId { get; }
    string? Creator { get; }
    string? Source { get; }
    string? Created { get; }
    string? Finished { get; }
}

/// <summary>Mockable projection of Helix SDK WorkItemSummary.</summary>
public interface IWorkItemSummary
{
    string Name { get; }
}

/// <summary>Mockable projection of Helix SDK WorkItemDetails.</summary>
public interface IWorkItemDetails
{
    int? ExitCode { get; }
    string? State { get; }
    string? MachineName { get; }
    DateTimeOffset? Started { get; }
    DateTimeOffset? Finished { get; }
}

/// <summary>Mockable projection of Helix SDK UploadedFile.</summary>
public interface IWorkItemFile
{
    string Name { get; }
    string? Link { get; }
}
