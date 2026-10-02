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
    /// </summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="cacheDir">Cache base directory to populate. Omit to use the normal hlx cache root.</param>
    /// <param name="manifest">Manifest output path. Defaults to hlx-collect-manifest.json in the current directory.</param>
    /// <param name="resume">Reuse successful entries from the previous manifest when cache evidence still exists.</param>
    /// <param name="export">Destination snapshot directory. Must not already exist.</param>
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
    /// <param name="helixScope">Helix scope: suggested or none. Default: suggested.</param>
    /// <param name="downloadHelixFiles">Glob for Helix uploaded files to stream with byte caps; over-cap files are skipped (size_limit/total_size_limit), and in-cap files replay offline.</param>
    /// <param name="maxFileBytes">Maximum bytes per downloaded file. Default: 52428800.</param>
    /// <param name="maxTotalBytes">Maximum total downloaded bytes. Default: 2147483648.</param>
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
            cacheOptions.CacheRoot = Path.GetFullPath(cacheDir);

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
            Console.Error.WriteLine($"Error: {ex.Message}");
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
