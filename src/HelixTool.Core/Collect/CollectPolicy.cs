using HelixTool.Core.Acquisition;

namespace HelixTool.Core.Collect;

public sealed record CollectPolicy
{
    internal CollectSetBudget? SetBudget { get; init; }
    internal string? RecoveryExportPath { get; init; }
    internal bool PersistManifest { get; init; } = true;
    internal IReadOnlyDictionary<string, CollectFetchAttempt>? PriorAttempts { get; init; }

    public const long DefaultMaxTestResults = 10_000;
    public const long DefaultMaxTestAttachments = 1_000;

    public string BuildIdOrUrl { get; init; } = "";
    public string? ManifestPath { get; init; }
    public bool Resume { get; init; }
    public string? ExportPath { get; init; }
    public bool AllowIncomplete { get; init; }
    public int MaxConcurrency { get; init; } = 6;
    public int RetryCount { get; init; } = 3;
    public IReadOnlySet<AcquisitionErrorKind> RetryKinds { get; init; } =
        new HashSet<AcquisitionErrorKind>
        {
            AcquisitionErrorKind.RateLimited,
            AcquisitionErrorKind.Timeout,
            AcquisitionErrorKind.TransportError
        };
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public string ArtifactPattern { get; init; } = "*";
    public string? ArtifactJobPrefix { get; init; }
    public bool KeepAttemptPrefix { get; init; }
    public string Match { get; init; } = "auto";
    public string JobResults { get; init; } = "failed,canceled";
    public string LogScope { get; init; } = "failed";
    public string TestScope { get; init; } = "failed";
    public long? MaxTestResults { get; init; }
    public string TestAttachmentScope { get; init; } = "diagnostic";
    public long? MaxTestAttachments { get; init; }
    public string HelixScope { get; init; } = "suggested";
    public string? DownloadHelixFiles { get; init; }
    public string? DownloadAzdoArtifacts { get; init; }
    public string ArtifactByteScope { get; init; } = "auto";
    public string ArtifactByteMode { get; init; } = "auto";
    public string? DownloadArtifactMembers { get; init; }
    public string? DownloadTestAttachments { get; init; }
    public string? DownloadAttachmentMembers { get; init; }
    public string AttachmentContentKind { get; init; } = "auto";
    public bool AllowLarge { get; init; }
    public long MaxTotalStreamBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public IReadOnlyList<string> Argv { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Options { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

public sealed record CollectResult(
    CollectManifest Manifest,
    string ManifestPath,
    string? SnapshotPath);

public sealed record CollectRetryPolicy(
    int MaxAttempts,
    IReadOnlySet<AcquisitionErrorKind> Kinds,
    TimeSpan InitialDelay,
    TimeSpan MaxDelay);

public enum CollectOutcome
{
    Ok,
    Cached,
    RecordedFailure,
    Failed,
    Skipped
}

public sealed record CollectCompleteness(
    bool Complete,
    int ExitCode,
    IReadOnlyList<CollectIncompleteDetail> IncompleteDetails);

internal sealed class CollectSetBudget(long maxResults, long maxAttachments)
{
    private long _results;
    private long _attachments;
    public long RemainingResults => Math.Max(0, maxResults - Interlocked.Read(ref _results));
    public long RemainingAttachments => Math.Max(0, maxAttachments - Interlocked.Read(ref _attachments));
    public void AddResults(int count) => Interlocked.Add(ref _results, count);
    public void AddAttachments(int count) => Interlocked.Add(ref _attachments, count);
}
