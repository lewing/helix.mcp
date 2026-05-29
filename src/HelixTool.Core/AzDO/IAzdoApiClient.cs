namespace HelixTool.Core.AzDO;

/// <summary>
/// Mockable boundary for Azure DevOps REST API calls.
/// Mirrors <see cref="IHelixApiClient"/> pattern — this is the only AzDO HTTP boundary for testing.
/// </summary>
public interface IAzdoApiClient
{
    Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default);
    Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default);
    Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, int? startLine = null, int? endLine = null, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAllOutcomesAsync(string org, string project, int runId, int top = 1000, CancellationToken ct = default);
    Task<AzdoTestResult?> GetTestResultWithSubResultsAsync(string org, string project, int runId, int resultId, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(string org, string project, int buildId, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default);

    /// <summary>List all build logs with metadata (line counts) without downloading content.</summary>
    Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(string org, string project, int buildId, CancellationToken ct = default);
}
