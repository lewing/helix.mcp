using System.Text.Json;
using ConsoleAppFramework;
using HelixTool.Core.Acquisition;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using Microsoft.Extensions.DependencyInjection;

/// <summary>CLI commands for deterministic cache/snapshot collection.</summary>
public sealed class CollectCommands
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new AcquisitionErrorKindJsonConverter() }
    };

    private readonly IServiceProvider _services;

    public CollectCommands(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>
    /// Collect deterministic evidence for an Azure DevOps build into the hlx cache and optionally export a replayable snapshot.
    /// The exported snapshot is consumed with <c>HLX_EVAL_SNAPSHOT=/path/to/snapshot hlx mcp</c>.
    /// A manifest is guaranteed for any non-cancellation failure once the collector begins running:
    /// auth resolution, the final auth-status lookup, and final evidence verification all persist a
    /// manifest (incompleteDetails code <c>collector_hard_error</c>, exit code 1) instead of crashing.
    /// The only exceptions are argument/policy validation and the "caching disabled" precondition,
    /// which fail before any manifest location is known and are reported as a plain usage error
    /// (exit 1, no manifest).
    /// </summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="cacheDir">Cache base directory to populate. Omit to use the normal hlx cache root, unless --export is set: then an isolated per-run temp cache directory is used so the snapshot can't leak other builds or other AzDO auth partitions' cached data.</param>
    /// <param name="manifest">Manifest output path. Defaults to hlx-collect-manifest.json next to the cache directory being populated (not the current working directory). This path is always written, even on hard failure (see summary).</param>
    /// <param name="resume">Reuse successful entries from the previous manifest when cache evidence still exists.</param>
    /// <param name="export">Destination snapshot directory. Must not already exist. Without --cache-dir, collects into an isolated per-run cache directory so the snapshot only ever contains this run's evidence.</param>
    /// <param name="json">Output the manifest as JSON.</param>
    /// <param name="allowIncomplete">Exit 0 only when incompleteness is limited to policy-allowed skips.</param>
    /// <param name="maxConcurrency">Maximum concurrent resource fetches. Default: 6.</param>
    /// <param name="retryCount">Total attempts per transient acquisition. Default: 3.</param>
    /// <param name="retryKinds">Comma-separated acquisition kinds to retry. Default: rate_limited,timeout,transport_error.</param>
    /// <param name="retryInitialDelay">Initial retry delay, such as 2s or 00:00:02. Default: 2s.</param>
    /// <param name="retryMaxDelay">Maximum retry delay, such as 30s or 00:00:30. Default: 30s.</param>
    /// <param name="artifactPattern">Artifact-name glob used by evidence planning. Default: *.</param>
    /// <param name="artifactJobPrefix">Prefix stripped from artifact names before matching.</param>
    /// <param name="keepAttemptPrefix">Keep AttemptN_ in artifact names instead of stripping it.</param>
    /// <param name="match">Evidence match strategy: auto, source-id, normalized-exact, or exact.</param>
    /// <param name="jobResults">Comma-separated timeline job results targeted by evidence planning.</param>
    /// <param name="logScope">AzDO log scope: failed, all, or none. Default: failed.</param>
    /// <param name="testScope">AzDO test scope: failed, all, or none. Default: failed.</param>
    /// <param name="maxTestResults">Build-wide all-outcome result budget. Default: 10000. Larger builds require an explicit budget at least as large as the estimated result count; refusal is a manifested policy skip (exit 2).</param>
    /// <param name="testAttachmentScope">Attachment metadata selection, independent of test scope: diagnostic (default), all, or none. Diagnostic selects Failed, Error, Timeout, Aborted, Inconclusive, Blocked, and Warning. All requires an explicit --max-test-attachments.</param>
    /// <param name="maxTestAttachments">Build-wide attachment-list request cap. Default: 1000 for diagnostic scope. Scope all requires an explicit positive cap; over-cap coverage is manifested as incomplete.</param>
    /// <param name="helixScope">Helix scope: suggested or none. Default: suggested.</param>
    /// <param name="downloadHelixFiles">Glob for Helix uploaded files to stream with byte caps; over-cap files are skipped (size_limit/total_size_limit), and in-cap files replay offline.</param>
    /// <param name="maxFileBytes">Maximum bytes per downloaded file. Default: 52428800.</param>
    /// <param name="maxTotalBytes">Maximum total optional Helix file bytes, including resumed files. Default: 2147483648. Required evidence is acquired and verified first; new downloads share the remaining cache capacity after all existing artifacts, so this run's optional writes cannot cause cache-cap eviction. The manifest records the requested limit, not cache-derived headroom.</param>
    [Command("collect azdo-build")]
    public async Task AzdoBuild(
        [Argument] string buildId,
        string? cacheDir = null,
        string? manifest = null,
        bool resume = false,
        string? export = null,
        bool json = false,
        bool allowIncomplete = false,
        int maxConcurrency = 6,
        int retryCount = 3,
        string retryKinds = "rate_limited,timeout,transport_error",
        string retryInitialDelay = "2s",
        string retryMaxDelay = "30s",
        string artifactPattern = "*",
        string? artifactJobPrefix = null,
        bool keepAttemptPrefix = false,
        string match = "auto",
        string jobResults = "failed,canceled",
        string logScope = "failed",
        string testScope = "failed",
        long? maxTestResults = null,
        string testAttachmentScope = "diagnostic",
        long? maxTestAttachments = null,
        string helixScope = "suggested",
        string? downloadHelixFiles = null,
        long maxFileBytes = 50L * 1024 * 1024,
        long maxTotalBytes = 2L * 1024 * 1024 * 1024,
        bool schema = false,
        CancellationToken ct = default)
    {
        if (Commands.TryPrintSchema<CollectManifest>(schema))
            return;

        var cacheOptions = _services.GetRequiredService<CacheOptions>();
        if (cacheOptions.EvalMode)
        {
            Console.Error.WriteLine("Error: 'collect azdo-build' cannot run in eval mode (HLX_EVAL_SNAPSHOT is set).");
            Environment.ExitCode = 1;
            return;
        }

        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            cacheOptions.CacheRoot = Path.GetFullPath(cacheDir);
        }
        else if (!string.IsNullOrWhiteSpace(export))
        {
            // --export without --cache-dir would otherwise export the entire shared cache —
            // including other builds and other AzDO auth partitions' private data — because the
            // shared cache has no key filtering on export. Isolate to a fresh per-run cache
            // directory instead so the snapshot can only ever contain this run's evidence.
            var isolatedCacheDir = Path.Combine(Path.GetTempPath(), "hlx-collect-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(isolatedCacheDir);
            cacheOptions.CacheRoot = isolatedCacheDir;
            cacheDir = isolatedCacheDir;
            Console.Error.WriteLine($"No --cache-dir given with --export; collecting into an isolated cache directory: {isolatedCacheDir}");
        }

        if (!TryParseRetryKinds(retryKinds, out var retryKindSet, out var retryError))
        {
            Console.Error.WriteLine(retryError);
            Environment.ExitCode = 1;
            return;
        }

        if (!TryParseDuration(retryInitialDelay, out var initialDelay) ||
            !TryParseDuration(retryMaxDelay, out var maxDelay) ||
            initialDelay < TimeSpan.Zero ||
            maxDelay < TimeSpan.Zero)
        {
            Console.Error.WriteLine("Invalid retry delay. Use a non-negative TimeSpan value or a suffix like 2s, 5m, or 1h.");
            Environment.ExitCode = 1;
            return;
        }

        var policy = new CollectPolicy
        {
            BuildIdOrUrl = buildId,
            ManifestPath = manifest,
            Resume = resume,
            ExportPath = export,
            AllowIncomplete = allowIncomplete,
            MaxConcurrency = maxConcurrency,
            RetryCount = retryCount,
            RetryKinds = retryKindSet,
            RetryInitialDelay = initialDelay,
            RetryMaxDelay = maxDelay,
            ArtifactPattern = artifactPattern,
            ArtifactJobPrefix = artifactJobPrefix,
            KeepAttemptPrefix = keepAttemptPrefix,
            Match = match,
            JobResults = jobResults,
            LogScope = logScope,
            TestScope = testScope,
            MaxTestResults = maxTestResults,
            TestAttachmentScope = testAttachmentScope,
            MaxTestAttachments = maxTestAttachments,
            HelixScope = helixScope,
            DownloadHelixFiles = downloadHelixFiles,
            MaxFileBytes = maxFileBytes,
            MaxTotalBytes = maxTotalBytes,
            Argv = Environment.GetCommandLineArgs().Skip(1).ToList(),
            Options = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cacheDir"] = cacheDir,
                ["manifest"] = manifest,
                ["resume"] = resume,
                ["export"] = export,
                ["allowIncomplete"] = allowIncomplete,
                ["maxConcurrency"] = maxConcurrency,
                ["retryCount"] = retryCount,
                ["retryKinds"] = retryKinds,
                ["retryInitialDelay"] = retryInitialDelay,
                ["retryMaxDelay"] = retryMaxDelay,
                ["artifactPattern"] = artifactPattern,
                ["artifactJobPrefix"] = artifactJobPrefix,
                ["keepAttemptPrefix"] = keepAttemptPrefix,
                ["match"] = match,
                ["jobResults"] = jobResults,
                ["logScope"] = logScope,
                ["testScope"] = testScope,
                ["maxTestResults"] = maxTestResults,
                ["testAttachmentScope"] = testAttachmentScope,
                ["maxTestAttachments"] = maxTestAttachments,
                ["helixScope"] = helixScope,
                ["downloadHelixFiles"] = downloadHelixFiles,
                ["maxFileBytes"] = maxFileBytes,
                ["maxTotalBytes"] = maxTotalBytes
            }
        };

        CollectResult result;
        try
        {
            var collector = _services.GetRequiredService<AzdoBuildCollector>();
            result = await collector.CollectAzdoBuildAsync(
                policy,
                message => Console.Error.WriteLine($"  {message}"),
                ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Policy validation (bad --option values) and the "caching disabled" precondition are
            // rejected by AzdoBuildCollector before any manifest location can be resolved, so they
            // stay plain CLI usage errors with no manifest - this is a deliberate exception to the
            // "every failure writes a manifest" rule documented on AzdoBuildCollector.CollectAzdoBuildAsync.
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Should be unreachable: AzdoBuildCollector.CollectAzdoBuildAsync wraps its entire
            // lifecycle (auth resolution, final auth-status lookup, final verification, etc.) in a
            // hard-error handler that always persists a manifest (incompleteDetails code
            // "collector_hard_error") and returns normally instead of throwing. This remains only
            // as an absolute last-resort net against a defect in that guarantee, so the CLI never
            // crashes with an unhandled stack trace and no manifest. Uses the same caller-cancellation
            // ownership predicate as the collector: excluded (left to propagate) only for genuine
            // caller cancellation of our own ct, so a non-caller OperationCanceledException/
            // TaskCanceledException (e.g. a provider-internal timeout) still gets this message
            // instead of silently crashing the process. Deliberately never prints ex.Message: if
            // this guarantee did fail, the escaping exception is unclassified and could carry
            // secrets or a raw stack trace, and this text goes straight to stderr.
            Console.Error.WriteLine("Error: hlx collect failed unexpectedly due to an unclassified internal error.");
            Environment.ExitCode = 1;
            return;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result.Manifest, s_jsonOptions));
        }
        else
        {
            Console.WriteLine($"Manifest: {result.ManifestPath}");
            Console.WriteLine($"Complete: {(result.Manifest.Complete ? "yes" : "no")}");
            Console.WriteLine($"Exit code: {result.Manifest.ExitCode}");
            Console.WriteLine($"Attempts: {result.Manifest.Summary.Attempted} attempted, {result.Manifest.Summary.Ok} ok, {result.Manifest.Summary.Cached} cached, {result.Manifest.Summary.RecordedFailure} recorded failure, {result.Manifest.Summary.Failed} failed, {result.Manifest.Summary.Skipped} skipped");
            if (result.Manifest.Snapshot.Exported)
            {
                Console.WriteLine($"Snapshot: {result.Manifest.Snapshot.Path}");
                Console.WriteLine($"MCP: HLX_EVAL_SNAPSHOT={result.Manifest.Snapshot.Path} hlx mcp");
            }
            if (result.Manifest.IncompleteDetails.Count > 0)
            {
                Console.WriteLine("Incomplete details:");
                foreach (var detail in result.Manifest.IncompleteDetails)
                    Console.WriteLine($"  - [{detail.Code}] {detail.Message}");
            }
        }

        Environment.ExitCode = result.Manifest.ExitCode;
    }

    private static bool TryParseRetryKinds(
        string value,
        out IReadOnlySet<AcquisitionErrorKind> retryKinds,
        out string? error)
    {
        var parsed = new HashSet<AcquisitionErrorKind>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseKind(item, out var kind))
            {
                retryKinds = parsed;
                error = $"Invalid --retry-kinds value '{item}'. Must contain acquisition kind names like rate_limited, timeout, transport_error.";
                return false;
            }

            parsed.Add(kind);
        }

        retryKinds = parsed;
        error = null;
        return true;
    }

    private static bool TryParseKind(string value, out AcquisitionErrorKind kind)
    {
        kind = value switch
        {
            "not_found" => AcquisitionErrorKind.NotFound,
            "access_denied" => AcquisitionErrorKind.AccessDenied,
            "rate_limited" => AcquisitionErrorKind.RateLimited,
            "timeout" => AcquisitionErrorKind.Timeout,
            "transport_error" => AcquisitionErrorKind.TransportError,
            "invalid_response" => AcquisitionErrorKind.InvalidResponse,
            "not_in_snapshot" => AcquisitionErrorKind.NotInSnapshot,
            _ => default
        };
        return value is "not_found" or "access_denied" or "rate_limited" or "timeout" or "transport_error" or "invalid_response" or "not_in_snapshot";
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        if (TimeSpan.TryParse(value, out duration))
            return true;

        if (value.Length < 2)
            return false;

        var suffix = value[^1];
        if (!double.TryParse(value[..^1], out var amount))
            return false;

        duration = suffix switch
        {
            's' or 'S' => TimeSpan.FromSeconds(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            _ => default
        };
        return duration != default;
    }
}
