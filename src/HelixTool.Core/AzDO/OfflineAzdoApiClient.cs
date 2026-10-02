using HelixTool.Core.Acquisition;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Eval-mode stub for <see cref="IAzdoApiClient"/>.
/// Every method throws <see cref="InvalidOperationException"/> so that cache misses in eval mode
/// surface as an explicit, descriptive error rather than silently falling through to live AzDO.
/// </summary>
public sealed class OfflineAzdoApiClient : IAzdoApiClient
{
    private static HlxAcquisitionException Blocked(
        string operation,
        IReadOnlyDictionary<string, object?> resource)
        => new(AcquisitionErrorFactory.Create(
            AcquisitionErrorKind.NotFound,
            "cache",
            operation,
            resource,
            "Network blocked: eval mode. Cache key not found in snapshot."));

    private static IReadOnlyDictionary<string, object?> Resource(
        string org,
        string project,
        params (string Name, object? Value)[] values)
    {
        var resource = new Dictionary<string, object?>
        {
            ["org"] = org,
            ["project"] = project
        };
        foreach (var (name, value) in values)
            resource[name] = value;
        return resource;
    }

    public Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked("get_build", Resource(org, project, ("buildId", buildId)));

    public Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default)
        => throw Blocked("list_builds", Resource(org, project));

    public Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked("get_timeline", Resource(org, project, ("buildId", buildId)));

    public Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, int? startLine = null, int? endLine = null, CancellationToken ct = default)
        => throw Blocked("get_build_log", Resource(org, project, ("buildId", buildId), ("logId", logId)));

    public Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
        => throw Blocked("list_build_changes", Resource(org, project, ("buildId", buildId)));

    public Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
        => throw Blocked("list_test_runs", Resource(org, project, ("buildId", buildId)));

    public Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, string? outcomes = null, CancellationToken ct = default)
        => throw Blocked("list_test_results", Resource(org, project, ("runId", runId)));

    public Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked("list_artifacts", Resource(org, project, ("buildId", buildId)));

    public Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default)
        => throw Blocked("list_test_attachments", Resource(org, project, ("runId", runId), ("resultId", resultId)));

    public Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked("list_build_logs", Resource(org, project, ("buildId", buildId)));
}
