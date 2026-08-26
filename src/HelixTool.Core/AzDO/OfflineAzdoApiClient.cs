namespace HelixTool.Core.AzDO;

/// <summary>
/// Eval-mode stub for <see cref="IAzdoApiClient"/>.
/// Every method throws <see cref="InvalidOperationException"/> so that cache misses in eval mode
/// surface as an explicit, descriptive error rather than silently falling through to live AzDO.
/// </summary>
public sealed class OfflineAzdoApiClient : IAzdoApiClient
{
    private static InvalidOperationException Blocked() =>
        new("Network blocked: eval mode. Cache key not found in snapshot.");

    public Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default)
        => throw Blocked();

    public Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked();

    public Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, int? startLine = null, int? endLine = null, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, string? outcomes = null, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default)
        => throw Blocked();

    public Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(string org, string project, int buildId, CancellationToken ct = default)
        => throw Blocked();
}
