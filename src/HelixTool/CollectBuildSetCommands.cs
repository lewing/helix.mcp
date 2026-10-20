using System.Text.Json;
using ConsoleAppFramework;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using Microsoft.Extensions.DependencyInjection;

namespace HelixTool;

public sealed class CollectBuildSetCommands(IServiceProvider services)
{
    /// <summary>Collect an AzDO query's captured build membership and evidence into one replayable snapshot.</summary>
    /// <param name="query">Path to a strict schemaVersion 1 semantic query JSON file.</param>
    /// <param name="buildScope">evidence (default) or metadata.</param>
    /// <param name="allowLarge">Continue beyond unattended planning thresholds; never changes authorization.</param>
    /// <param name="maxBuilds">Unattended member planning threshold (default 50).</param>
    /// <param name="maxDefinitions">Unattended definition planning threshold (default 25).</param>
    /// <param name="maxScanBuilds">Unattended discovery planning threshold (default 5000).</param>
    /// <param name="maxSetTestResults">Run-wide unattended test result planning threshold (default 10000).</param>
    /// <param name="maxSetTestAttachments">Run-wide unattended attachment planning threshold (default 1000).</param>
    /// <param name="json">Print a compact completion report; the full manifest is always persisted.</param>
    /// <param name="jsonManifest">Print the full aggregate manifest.</param>
    [Command("collect azdo-builds")]
    public async Task AzdoBuilds(string query, string buildScope = "evidence", int maxBuilds = 50,
        int maxDefinitions = 25, int maxScanBuilds = 5000, long maxSetTestResults = 10000,
        long maxSetTestAttachments = 1000, bool allowLarge = false, string? manifest = null,
        bool resume = false, string? export = null, bool json = false, bool jsonManifest = false, string? cacheDir = null,
        bool allowIncomplete = false, int maxConcurrency = 6, int retryCount = 3,
        string retryKinds = "rate_limited,timeout,transport_error", string retryInitialDelay = "2s", string retryMaxDelay = "30s",
        string artifactPattern = "*", string? artifactJobPrefix = null, bool keepAttemptPrefix = false,
        string match = "auto", string jobResults = "failed,canceled",
        string logScope = "failed", string testScope = "failed", long? maxTestResults = null,
        string testAttachmentScope = "diagnostic", long? maxTestAttachments = null,
        string helixScope = "suggested", string? downloadHelixFiles = null,
        string? downloadAzdoArtifacts = null, string artifactByteScope = "auto", string artifactByteMode = "auto",
        string? downloadArtifactMembers = null, string? downloadTestAttachments = null,
        string? downloadAttachmentMembers = null, string attachmentContentKind = "auto",
        long maxFileBytes = 50L * 1024 * 1024, long maxTotalBytes = 2L * 1024 * 1024 * 1024,
        long maxTotalStreamBytes = 2L * 1024 * 1024 * 1024, bool schema = false, CancellationToken ct = default)
    {
        if (Commands.TryPrintSchema<CollectManifest>(schema)) return;
        var options = services.GetRequiredService<CacheOptions>();
        if (options.EvalMode) throw new InvalidOperationException("collect azdo-builds requires live mode: unset HLX_EVAL_SNAPSHOT and export a new destination.");
        if (cacheDir is not null) options.CacheRoot = Path.GetFullPath(cacheDir);
        else if (export is not null)
        {
            options.CacheRoot = Path.Combine(Directory.GetCurrentDirectory(), ".hlx-collections", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(options.CacheRoot);
            Console.Error.WriteLine($"Isolated collection cache: {options.CacheRoot}");
        }
        if (!CollectCommands.TryParseRetryKinds(retryKinds, out var kinds, out var retryError)) throw new ArgumentException(retryError);
        if (!CollectCommands.TryParseDuration(retryInitialDelay, out var initialDelay) || !CollectCommands.TryParseDuration(retryMaxDelay, out var maximumDelay) || initialDelay < TimeSpan.Zero || maximumDelay < TimeSpan.Zero)
            throw new ArgumentException("Retry delays must be nonnegative durations, e.g. 2s or 1m.");
        var semantic = JsonSerializer.Deserialize<AzdoBuildSetQuery>(await File.ReadAllTextAsync(query, ct), AzdoBuildQueries.JsonOptions)
            ?? throw new ArgumentException("Query file must contain a JSON object.");
        var argv = Environment.GetCommandLineArgs().Skip(1).ToList();
        var start = argv.IndexOf("collect"); if (start >= 0) argv = argv.Skip(start).ToList();
        var queryFlag = argv.IndexOf("--query");
        if (queryFlag >= 0 && queryFlag + 1 < argv.Count) argv[queryFlag + 1] = Path.GetFullPath(query);
        var assignedQuery = argv.FindIndex(value => value.StartsWith("--query=", StringComparison.Ordinal));
        if (assignedQuery >= 0) argv[assignedQuery] = "--query=" + Path.GetFullPath(query);
        var policy = new CollectPolicy
        {
            ManifestPath = manifest, Resume = resume, ExportPath = export, AllowLarge = allowLarge,
            AllowIncomplete = allowIncomplete, MaxConcurrency = maxConcurrency, RetryCount = retryCount,
            RetryKinds = kinds, RetryInitialDelay = initialDelay, RetryMaxDelay = maximumDelay,
            ArtifactPattern = artifactPattern, ArtifactJobPrefix = artifactJobPrefix, KeepAttemptPrefix = keepAttemptPrefix, Match = match, JobResults = jobResults,
            LogScope = logScope, TestScope = testScope, MaxTestResults = maxTestResults,
            TestAttachmentScope = testAttachmentScope, MaxTestAttachments = maxTestAttachments,
            HelixScope = helixScope, DownloadHelixFiles = downloadHelixFiles,
            DownloadAzdoArtifacts = downloadAzdoArtifacts, ArtifactByteScope = artifactByteScope, ArtifactByteMode = artifactByteMode,
            DownloadArtifactMembers = downloadArtifactMembers, DownloadTestAttachments = downloadTestAttachments,
            DownloadAttachmentMembers = downloadAttachmentMembers, AttachmentContentKind = attachmentContentKind,
            MaxFileBytes = maxFileBytes, MaxTotalBytes = maxTotalBytes, MaxTotalStreamBytes = maxTotalStreamBytes,
            Argv = argv
        };
        var result = await services.GetRequiredService<AzdoBuildCollector>().CollectAzdoBuildsAsync(new CollectBuildSetPolicy
        {
            Query = semantic, QueryPath = Path.GetFullPath(query), BuildScope = buildScope,
            MaxBuilds = maxBuilds, MaxDefinitions = maxDefinitions, MaxScanBuilds = maxScanBuilds,
            MaxSetTestResults = maxSetTestResults, MaxSetTestAttachments = maxSetTestAttachments, Collection = policy
        }, message => Console.Error.WriteLine(message), ct);
        Environment.ExitCode = result.Manifest.ExitCode;
        if (jsonManifest)
        {
            await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result.Manifest, AzdoBuildQueries.JsonOptions, ct);
            Console.WriteLine();
        }
        else if (json) Console.WriteLine(JsonSerializer.Serialize(new
        {
            manifestPath = result.ManifestPath, snapshotPath = result.SnapshotPath, complete = result.Manifest.Complete,
            builds = result.Manifest.Builds?.Count ?? 0, total = result.Manifest.Source.Discovery?.Total,
            exitCode = result.Manifest.ExitCode, incompleteCodes = result.Manifest.IncompleteDetails.Select(d => d.Code).Distinct(),
            recovery = result.Manifest.IncompleteDetails.Where(d => d.Code is "build_set_threshold" or "discovery_recapture_required").Select(d => d.Resource)
        }, AzdoBuildQueries.JsonOptions));
        else Console.WriteLine($"Build-set collection: {result.Manifest.Builds?.Count ?? 0} builds; complete={result.Manifest.Complete}; manifest={result.ManifestPath}");
    }
}
