using System.ComponentModel;
using HelixTool.Core.AzDO;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class AzdoDownloadMcpTools(AzdoDownloadService service)
{
    [McpServerTool(Name = "azdo_download_artifacts", Title = "Download AzDO Artifacts", ReadOnly = false,
        Destructive = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(MinimalObjectSchema)),
     Description("Download AzDO artifacts or selected members to disk/cache. Known member paths use native transfer; globs use selective ZIP retention. Return SHA, paths and bounded receipts; read evidence with hlx_read_evidence. Large explicit requests stream, not size refusals. Binlogs stay download-only.")]
    public async Task<AzdoDownloadReport> Artifacts(
        [Description("AzDO build ID string or full build URL, not a Helix GUID")] string buildIdOrUrl,
        [Description("Exact artifact name; exclusive with pattern")] string? artifactName = null,
        [Description("Explicit artifact-name glob; no implicit failed-leg/type restriction")] string? pattern = null,
        [Description("Exact relative member path, e.g. Release/Build.binlog; exclusive with memberPattern")] string? memberPath = null,
        [Description("Relative member glob; **/Build.binlog includes the root; no selectors downloads the original archive")] string? memberPattern = null,
        int memberOffset = 0, int memberLimit = 10,
        [Description("Progress/routing label only: auto, logs, binlog, dump, other")] string contentKind = "auto",
        int offset = 0, int limit = 10, string? viewId = null,
        [Description("Optional explicit planning threshold; no inferred per-file size gate")] long? maxFileBytes = null,
        long? maxArchiveBytes = null, long? maxTotalBytes = null, long? maxTotalStreamBytes = null,
        [Description("Waive logical planning thresholds, never disk/auth/integrity protections")] bool allowLarge = false,
        bool all = false, bool allMembers = false,
        [Description("auto, inline, file, chunked; large representations always have full file access")] string delivery = "auto",
        [Description("Reuse verified completed files; unsupported upstream Range restarts only the current file")] bool resume = true,
        [Description("Positive inline shaping target, not payload ceiling")] long maxResponseBytes = 12288,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var options = Options(artifactName, null, pattern, memberPath, memberPattern, memberOffset, memberLimit,
            contentKind, offset, limit, viewId, maxFileBytes, maxArchiveBytes, maxTotalBytes, maxTotalStreamBytes,
            allowLarge, all, allMembers, delivery, resume, maxResponseBytes);
        var report = await McpExceptionHandler.RunServiceCallAsync(
            () => service.DownloadArtifactsAsync(buildIdOrUrl, options, Notify(progress), ct), "download artifacts");
        return Shape(report, options);
    }

    [McpServerTool(Name = "azdo_download_test_attachments", Title = "Download AzDO Test Attachments", ReadOnly = false,
        Destructive = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(MinimalObjectSchema)),
     Description("Download run/result attachments and dumps by ID or filename glob to disk/cache. Optional ZIP member selection; original binaries are not automatically extracted. Return SHA, paths and bounded receipts; hlx_read_evidence reads chunks. Analyze dumps externally.")]
    public async Task<AzdoDownloadReport> TestAttachments(
        [Description("Positive test run ID from azdo_test_runs")] int runId,
        [Description("Omit for run-level attachments; otherwise a positive test result ID")] int? resultId = null,
        [Description("Exact attachment ID; exclusive with pattern")] int? attachmentId = null,
        string? pattern = null, string? memberPath = null, string? memberPattern = null,
        int memberOffset = 0, int memberLimit = 10, string contentKind = "auto",
        string org = "dnceng-public", string project = "public",
        int offset = 0, int limit = 10, string? viewId = null,
        long? maxFileBytes = null, long? maxArchiveBytes = null, long? maxTotalBytes = null, long? maxTotalStreamBytes = null,
        bool allowLarge = false, bool all = false, bool allMembers = false, string delivery = "auto",
        bool resume = true, long maxResponseBytes = 12288,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var options = Options(null, attachmentId, pattern, memberPath, memberPattern, memberOffset, memberLimit,
            contentKind, offset, limit, viewId, maxFileBytes, maxArchiveBytes, maxTotalBytes, maxTotalStreamBytes,
            allowLarge, all, allMembers, delivery, resume, maxResponseBytes);
        var report = await McpExceptionHandler.RunServiceCallAsync(
            () => service.DownloadTestAttachmentsAsync(runId, resultId, org, project, options, Notify(progress), ct), "download test attachments");
        return Shape(report, options);
    }

    private static Action<string> Notify(IProgress<ProgressNotificationValue>? progress)
        => message => progress?.Report(new ProgressNotificationValue { Progress = 0, Message = message });

    public static AzdoDownloadOptions Options(string? artifactName, int? attachmentId, string? pattern,
        string? memberPath, string? memberPattern, int memberOffset, int memberLimit, string contentKind,
        int offset, int limit, string? viewId, long? maxFileBytes, long? maxArchiveBytes, long? maxTotalBytes,
        long? maxTotalStreamBytes, bool allowLarge, bool all, bool allMembers, string delivery, bool resume, long maxResponseBytes)
        => new()
        {
            ArtifactName = artifactName,
            AttachmentId = attachmentId,
            Pattern = pattern,
            MemberPath = memberPath,
            MemberPattern = memberPattern,
            MemberOffset = memberOffset,
            MemberLimit = memberLimit,
            ContentKind = contentKind,
            Offset = offset,
            Limit = limit,
            ViewId = viewId,
            MaxFileBytes = maxFileBytes,
            MaxArchiveBytes = maxArchiveBytes,
            MaxTotalBytes = maxTotalBytes,
            MaxTotalStreamBytes = maxTotalStreamBytes,
            AllowLarge = allowLarge,
            All = all,
            AllMembers = allMembers,
            Delivery = delivery,
            Resume = resume,
            MaxResponseBytes = maxResponseBytes
        };

    public static AzdoDownloadReport Shape(AzdoDownloadReport report, AzdoDownloadOptions options)
    {
        var target = McpPresentationBudget.ClampMaxResponseBytes(options.MaxResponseBytes);
        report = report with { MaxResponseBytes = target, RequestedMaxResponseBytes = options.MaxResponseBytes };
        if (options.Delivery is not ("file" or "chunked") &&
            McpPresentationBudget.MeasureAny(report) <= target - McpPresentationBudget.FramingReserveBytes)
            return report;
        // The complete acquisition manifest is already verified, including all nested member outcomes.
        return report with
        {
            Results = [],
            Returned = 0,
            Complete = false,
            Truncated = report.Total > 0,
            Next = null,
            Planning = null,
            Headroom = null,
            Cache = null,
            IncompleteDetails = [],
            Recovery = report.Continuation,
            Note = "Complete download report is in delivery. Continue with hlx_read_evidence; source completeness is recorded in that manifest."
        };
    }
}
