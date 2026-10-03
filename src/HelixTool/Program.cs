using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;
using ConsoleAppFramework;
using HelixTool;
using HelixTool.Core;
using HelixTool.Generated;
using HelixTool.Core.CliSchema;
using HelixTool.Core.Acquisition;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using HelixTool.Core.Helix;
using HelixTool.Core.AzDO;
using HelixTool.Core.Paging;
using HelixTool.Mcp.Tools;
using FilesJsonResult = HelixTool.Mcp.Tools.CliFilesJsonResult;
using HelixFileJsonResult = HelixTool.Mcp.Tools.CliHelixFileJsonResult;
using StatusJobJsonResult = HelixTool.Mcp.Tools.CliStatusJobJsonResult;
using StatusJsonResult = HelixTool.Mcp.Tools.CliStatusJsonResult;
using StatusWorkItemJsonResult = HelixTool.Mcp.Tools.CliStatusWorkItemJsonResult;
using WorkItemJsonResult = HelixTool.Mcp.Tools.CliWorkItemJsonResult;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Reflection;

HelixToolUserAgent.Initialize(typeof(Commands).Assembly.GetName().Version?.ToString() ?? "0.0.0");

var services = new ServiceCollection();
services.AddHttpClient("HelixDownload", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    HelixToolUserAgent.Apply(c);
});
services.AddHttpClient("AzDO", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    HelixToolUserAgent.Apply(c);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
services.AddSingleton<ICredentialStore, GitCredentialStore>();
services.AddSingleton<ChainedHelixTokenAccessor>();
services.AddSingleton<IHelixTokenAccessor>(sp => sp.GetRequiredService<ChainedHelixTokenAccessor>());
services.AddSingleton<IHelixApiClientFactory, HelixApiClientFactory>();

var evalSnapshotDir = Environment.GetEnvironmentVariable("HLX_EVAL_SNAPSHOT");
if (!string.IsNullOrEmpty(evalSnapshotDir))
{
    var resolvedSnapshot = Path.GetFullPath(evalSnapshotDir);
    var azdoPartition = EvalSnapshotAzdoPartitionSelector.Select(resolvedSnapshot);
    var evalOptions = new CacheOptions
    {
        CacheRoot = resolvedSnapshot,
        EvalMode = true,
        CacheRootHash = null,
        AuthTokenHash = azdoPartition.AuthTokenHash,
    };
    services.AddEvalModeCore(evalOptions);
    services.AddSingleton(sp => new Lazy<HelixService>(() => sp.GetRequiredService<HelixService>()));
    services.AddSingleton<AzdoService>(sp =>
        new AzdoService(
            sp.GetRequiredService<IAzdoApiClient>(),
            sp.GetRequiredService<IHelixApiClient>(),
            sp.GetRequiredService<IAzdoAcquisitionFailureRecorder>(),
            sp.GetRequiredService<CacheOptions>()));
}
else
{
services.AddSingleton<CacheOptions>(_ =>
{
    // Don't resolve Helix auth eagerly — let it be lazy so MCP/AzDO commands
    // can start without prompting for Helix credentials.
    var opts = new CacheOptions { AuthTokenHash = null };
    var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
    if (int.TryParse(maxStr, out var mb))
        opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
    return opts;
});
services.AddSingleton<ICacheStore>(sp => new SqliteCacheStore(sp.GetRequiredService<CacheOptions>()));
// Defer Helix token resolution — only resolves when a Helix command is invoked.
services.AddSingleton(sp => new Lazy<HelixApiClient>(() =>
{
    var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
    return new HelixApiClient(token);
}));
services.AddSingleton<HelixApiClient>(sp => sp.GetRequiredService<Lazy<HelixApiClient>>().Value);
services.AddSingleton<IHelixApiClient>(sp =>
    new CachingHelixApiClient(
        sp.GetRequiredService<HelixApiClient>(),
        sp.GetRequiredService<ICacheStore>(),
        sp.GetRequiredService<CacheOptions>()));
services.AddSingleton<HelixService>(sp =>
    new HelixService(
        sp.GetRequiredService<IHelixApiClient>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("HelixDownload")));
services.AddSingleton(sp => new Lazy<HelixService>(() => sp.GetRequiredService<HelixService>()));

// AzDO services — same decorator pattern as Helix
services.AddSingleton<IAzdoTokenAccessor, AzCliAzdoTokenAccessor>();
services.AddSingleton<AzdoApiClient>(sp =>
    new AzdoApiClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("AzDO"),
        sp.GetRequiredService<IAzdoTokenAccessor>(),
        sp.GetRequiredService<CacheOptions>()));
services.AddSingleton<IAzdoApiClient>(sp =>
    new CachingAzdoApiClient(
        sp.GetRequiredService<AzdoApiClient>(),
        sp.GetRequiredService<ICacheStore>(),
        sp.GetRequiredService<CacheOptions>(),
        sp.GetRequiredService<IAzdoTokenAccessor>()));
services.AddSingleton<IAzdoAcquisitionFailureRecorder>(sp =>
    new CachingAzdoAcquisitionFailureRecorder(
        sp.GetRequiredService<ICacheStore>(),
        sp.GetRequiredService<CacheOptions>()));
// Inject IHelixApiClient so GetHelixJobsAsync can use the canonical Helix-side Job.ListAsync(source) path (#92)
services.AddSingleton<AzdoService>(sp =>
    new AzdoService(
        sp.GetRequiredService<IAzdoApiClient>(),
        sp.GetRequiredService<IHelixApiClient>(),
        sp.GetRequiredService<IAzdoAcquisitionFailureRecorder>(),
        sp.GetRequiredService<CacheOptions>()));
services.AddSingleton<AzdoBuildCollector>();
}

// Shared evidence delivery store (P0-1 design 2.1a): materializes oversized tool payloads into the
// private runtime delivery directory and backs hlx_read_evidence. CLI commands with --output write
// files directly; this store exists so CLI-hosted MCP stdio sessions (hlx mcp) can serve remote reads.
services.AddSingleton<HelixTool.Core.Delivery.IEvidenceDeliveryStore>(sp =>
    new HelixTool.Core.Delivery.FileEvidenceDeliveryStore(sp.GetRequiredService<CacheOptions>()));

ConsoleApp.ServiceProvider = services.BuildServiceProvider();

var app = ConsoleApp.Create();
app.UseFilter<AcquisitionErrorCliFilter>();
app.Add<Commands>();
app.Add<AzdoCommands>();
app.Add<SnapshotCommands>();
app.Add<CollectCommands>();
// When no command is specified: default to MCP server mode if stdin is redirected
// (e.g. piped or launched by an MCP host), otherwise show help text for interactive use.
app.Run(args.Length == 0 ? (Console.IsInputRedirected ? ["mcp"] : ["--help"]) : args);

internal sealed class AcquisitionErrorCliFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
        => await CliAcquisitionErrorPipeline.InvokeAsync(
            ct => Next.InvokeAsync(context, ct),
            context.CommandArguments.ToArray(),
            cancellationToken);
}

public static class CliAcquisitionErrorPipeline
{
    public static async Task InvokeAsync(
        Func<CancellationToken, Task> next,
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await next(cancellationToken);
        }
        catch (HlxAcquisitionException ex)
        {
            CliAcquisitionErrorWriter.Write(ex, IsJsonRequested(commandArguments));
            Environment.ExitCode = 1;
        }
    }

    private static bool IsJsonRequested(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < arguments.Count && TryParseBoolean(arguments[i + 1], out var explicitValue))
                    return explicitValue;
                return true;
            }

            const string jsonPrefix = "--json=";
            if (argument.StartsWith(jsonPrefix, StringComparison.OrdinalIgnoreCase)
                && TryParseBoolean(argument[jsonPrefix.Length..], out var value))
            {
                return value;
            }
        }

        return false;
    }

    private static bool TryParseBoolean(string value, out bool parsed)
    {
        if (bool.TryParse(value, out parsed))
            return true;
        if (value == "1")
        {
            parsed = true;
            return true;
        }
        if (value == "0")
        {
            parsed = false;
            return true;
        }

        parsed = false;
        return false;
    }
}

internal static class CliAcquisitionErrorWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new AcquisitionErrorKindJsonConverter() }
    };

    public static void Write(HlxAcquisitionException ex, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new AcquisitionErrorCliEnvelope(false, ex.Error),
                s_jsonOptions));
            return;
        }

        Console.Error.WriteLine($"Error: {QuoteUntrusted(ex.Error.Message)}");
    }

    private static string QuoteUntrusted(string? value)
    {
        if (value is null)
            return "(none)";

        return JsonSerializer.Serialize(value);
    }
}

/// <summary>
/// CLI commands for interacting with .NET Helix test infrastructure.
/// </summary>
public class Commands
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new AcquisitionErrorKindJsonConverter() }
    };

    internal static bool TryPrintSchema<T>(bool schema)
    {
        if (!schema) return false;
        Console.WriteLine(SchemaGenerator.GenerateSchema<T>());
        return true;
    }

    private readonly Lazy<HelixService> _lazySvc;
    private readonly ICredentialStore _credentialStore;
    private readonly ChainedHelixTokenAccessor _tokenAccessor;

    private HelixService Svc => _lazySvc.Value;

    public Commands(Lazy<HelixService> svc, ICredentialStore credentialStore, ChainedHelixTokenAccessor tokenAccessor)
    {
        _lazySvc = svc;
        _credentialStore = credentialStore;
        _tokenAccessor = tokenAccessor;
    }

    private static int GetCategoryOrder(string category)
        => category switch
        {
            "Helix" => 0,
            "AzDO" => 1,
            _ => 2
        };

    private static string NormalizeRoute(string route)
        => string.Join(" ", route.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsUpper(ch))
            {
                if (i > 0)
                    builder.Append('-');

                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string GetParameterLabel(CommandRegistry.ParamInfo parameter)
    {
        if (parameter.IsPositional)
        {
            return parameter.Type.EndsWith("[]", StringComparison.Ordinal)
                ? $"<{parameter.Name}...>"
                : $"<{parameter.Name}>";
        }

        return $"--{ToKebabCase(parameter.Name)}";
    }

    private static string GetParameterDetail(CommandRegistry.ParamInfo parameter)
    {
        if (parameter.Default is null)
            return $"{parameter.Type} (required)";

        if (string.Equals(parameter.Default, "null", StringComparison.OrdinalIgnoreCase))
            return $"{parameter.Type} (optional)";

        return $"{parameter.Type} (default: {parameter.Default})";
    }

    private static string ToSummaryDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "(no description)";

        var text = description.Trim();
        var firstSentenceEnd = text.IndexOf(". ", StringComparison.Ordinal);
        if (firstSentenceEnd >= 0)
            return text[..(firstSentenceEnd + 1)];

        return text.Length <= 72 ? text : text[..69] + "...";
    }

    private static void PrintCommandSummary()
    {
        var categories = CommandRegistry.Commands
            .GroupBy(command => command.Category)
            .OrderBy(group => GetCategoryOrder(group.Key));

        foreach (var category in categories)
        {
            var commands = category.ToArray();
            if (commands.Length == 0)
                continue;

            var width = commands.Max(command => command.Route.Length) + 2;
            Console.WriteLine($"{category.Key} Commands:");
            foreach (var command in commands)
            {
                Console.Write("  ");
                Console.Write(command.Route.PadRight(width));
                Console.WriteLine(ToSummaryDescription(command.Description));
            }

            Console.WriteLine();
        }

        Console.WriteLine("Use 'hlx describe <command>' for MCP-equivalent command parameters and --schema hints.");
    }

    private static void PrintCommandDetail(string route)
    {
        var normalizedRoute = NormalizeRoute(route);
        var command = CommandRegistry.Get(normalizedRoute);
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command '{route}'.");
            Console.Error.WriteLine("Use 'hlx describe' to list available commands.");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"{command.Route} — {command.Description ?? "No description available."}");
        Console.WriteLine();
        Console.WriteLine("Parameters:");

        if (command.Parameters.Length == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            var labels = command.Parameters.Select(GetParameterLabel).ToArray();
            var width = labels.Max(label => label.Length) + 2;
            for (var i = 0; i < command.Parameters.Length; i++)
            {
                Console.Write("  ");
                Console.Write(labels[i].PadRight(width));
                Console.WriteLine(GetParameterDetail(command.Parameters[i]));
            }
        }

        if (command.Parameters.Any(parameter => string.Equals(parameter.Name, "schema", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine();
            Console.WriteLine($"Use 'hlx {command.Route} --schema' for full JSON output shape.");
        }
    }

    [Command("describe")]
    public void Describe([Argument] params string[] command)
    {
        if (command.Length == 0)
        {
            PrintCommandSummary();
            return;
        }

        PrintCommandDetail(string.Join(" ", command));
    }

    /// <summary>Show work item summary for a Helix job.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="filter">Filter: 'failed' (default), 'passed', or 'all'.</param>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [McpEquivalent("helix_status")]
    [Command("status")]
    public async Task Status([Argument] string jobId, [Argument] string filter = "failed", bool json = false, bool schema = false)
    {
        if (TryPrintSchema<StatusJsonResult>(schema))
            return;

        if (!filter.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("passed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid filter '{filter}'. Must be 'failed', 'passed', or 'all'.", nameof(filter));
        }

        var showFailed = filter.Equals("failed", StringComparison.OrdinalIgnoreCase) || filter.Equals("all", StringComparison.OrdinalIgnoreCase);
        var showPassed = filter.Equals("passed", StringComparison.OrdinalIgnoreCase) || filter.Equals("all", StringComparison.OrdinalIgnoreCase);

        Console.Error.Write("Fetching job details...");
        var summary = await Svc.GetJobStatusAsync(jobId);
        Console.Error.WriteLine(" done.");

        if (json)
        {
            var result = new StatusJsonResult
            {
                Job = new StatusJobJsonResult
                {
                    JobId = summary.JobId,
                    Name = summary.Name,
                    QueueId = summary.QueueId,
                    QueueAlias = summary.QueueAlias,
                    Creator = summary.Creator,
                    Source = summary.Source,
                    Created = summary.Created,
                    Finished = summary.Finished,
                    DockerTag = summary.DockerTag
                },
                TotalWorkItems = summary.TotalCount,
                FailedCount = summary.Failed.Count,
                PassedCount = summary.Passed.Count,
                InProgressCount = summary.InProgress.Count,
                Failed = showFailed
                    ? summary.Failed.Select(f => new StatusWorkItemJsonResult
                    {
                        Name = f.Name,
                        ExitCode = f.ExitCode,
                        State = f.State,
                        MachineName = f.MachineName,
                        Duration = f.Duration?.ToString(),
                        ConsoleLogUrl = f.ConsoleLogUrl,
                        FailureCategory = f.FailureCategory?.ToString()
                    }).ToList()
                    : null,
                Passed = showPassed
                    ? summary.Passed.Select(p => new StatusWorkItemJsonResult
                    {
                        Name = p.Name,
                        ExitCode = p.ExitCode,
                        State = p.State,
                        MachineName = p.MachineName,
                        Duration = p.Duration?.ToString(),
                        ConsoleLogUrl = p.ConsoleLogUrl,
                        FailureCategory = null
                    }).ToList()
                    : null,
                InProgress = summary.InProgress.Count > 0
                    ? summary.InProgress.Select(p => new StatusWorkItemJsonResult
                    {
                        Name = p.Name,
                        ExitCode = p.ExitCode,
                        State = p.State,
                        MachineName = p.MachineName,
                        Duration = p.Duration?.ToString(),
                        ConsoleLogUrl = p.ConsoleLogUrl,
                        FailureCategory = null
                    }).ToList()
                    : null
            };
            Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Job: {summary.Name}");
        Console.WriteLine($"Queue: {summary.QueueId}  Creator: {summary.Creator}  Source: {summary.Source}");
        Console.WriteLine($"Created: {summary.Created}  Finished: {summary.Finished}");
        Console.WriteLine($"Work items: {summary.TotalCount}");
        Console.WriteLine();

        if (showFailed && summary.Failed.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed: {summary.Failed.Count}");
            Console.ResetColor();
            foreach (var item in summary.Failed)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  [FAIL] ");
                Console.ResetColor();
                Console.Write(item.Name);
                if (item.FailureCategory.HasValue)
                    Console.Write($" [{item.FailureCategory.Value}]");
                Console.Write($" (exit code {item.ExitCode}");
                if (item.Duration.HasValue)
                    Console.Write($", {FormatDuration(item.Duration.Value)}");
                if (!string.IsNullOrEmpty(item.MachineName))
                    Console.Write($", machine: {item.MachineName}");
                Console.WriteLine(")");
                Console.WriteLine($"         {item.ConsoleLogUrl}");
            }
        }
        else if (showFailed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All work items passed.");
            Console.ResetColor();
        }

        if (summary.InProgress.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\nIn progress: {summary.InProgress.Count} (work items still Waiting/Running — not yet completed)");
            Console.ResetColor();
            foreach (var item in summary.InProgress)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("  [...]  ");
                Console.ResetColor();
                Console.Write(item.Name);
                if (!string.IsNullOrEmpty(item.State))
                    Console.Write($" (state: {item.State})");
                Console.WriteLine();
            }
        }

        if (showPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nPassed: {summary.Passed.Count}");
            Console.ResetColor();
            foreach (var item in summary.Passed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [OK]   ");
                Console.ResetColor();
                Console.Write(item.Name);
                if (item.Duration.HasValue)
                    Console.Write($" ({FormatDuration(item.Duration.Value)})");
                Console.WriteLine();
            }
        }
        else if (summary.Passed.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Passed: {summary.Passed.Count} (use 'hlx status <jobId> all' to show)");
            Console.ResetColor();
        }
    }

    /// <summary>Download console log for a work item to a temp file.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    [McpEquivalent("helix_logs")]
    [Command("logs")]
    public async Task Logs([Argument] string jobId, [Argument] string workItem)
    {
        var path = await Svc.DownloadConsoleLogAsync(jobId, workItem);
        Console.WriteLine(path);
    }

    /// <summary>List uploaded files for a work item.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [McpEquivalent("helix_files")]
    [Command("files")]
    public async Task Files([Argument] string jobId, [Argument] string workItem, bool json = false, bool schema = false)
    {
        if (TryPrintSchema<FilesJsonResult>(schema))
            return;

        var files = await Svc.GetWorkItemFilesAsync(jobId, workItem);

        if (json)
        {
            var result = new FilesJsonResult
            {
                Binlogs = files.Where(f => HelixService.MatchesPattern(f.Name, "*.binlog"))
                    .Select(f => new HelixFileJsonResult { Name = f.Name, Uri = f.Uri })
                    .ToList(),
                TestResults = files.Where(f => HelixService.IsTestResultFile(f.Name))
                    .Select(f => new HelixFileJsonResult { Name = f.Name, Uri = f.Uri })
                    .ToList(),
                Other = files.Where(f => !HelixService.MatchesPattern(f.Name, "*.binlog") && !HelixService.IsTestResultFile(f.Name))
                    .Select(f => new HelixFileJsonResult { Name = f.Name, Uri = f.Uri })
                    .ToList()
            };
            Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
            return;
        }

        foreach (var f in files)
        {
            var marker = HelixService.MatchesPattern(f.Name, "*.binlog") ? " [binlog]"
                       : HelixService.IsTestResultFile(f.Name) ? " [test-results]"
                       : "";
            Console.WriteLine($"  {f.Name}{marker}");
        }
    }

    /// <summary>Download work item files or a direct URL.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="pattern">File name or glob pattern (e.g., *.binlog).</param>
    /// <param name="url">Direct file URL to download (bypasses jobId/workItem).</param>
    /// <param name="json">Output downloaded paths as JSON and emit JSON acquisition errors.</param>
    [McpEquivalent("helix_download")]
    [Command("download")]
    public async Task Download([Argument] string? jobId = null, [Argument] string? workItem = null,
        string pattern = "*", string? url = null, bool json = false)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            var path = await Svc.DownloadFromUrlAsync(url);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new[] { path }, s_jsonOptions));
                return;
            }
            Console.WriteLine(path);
            return;
        }

        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(workItem))
            throw new ArgumentException("jobId and workItem are required unless --url is provided.");

        var paths = await Svc.DownloadFilesAsync(jobId, workItem, pattern);
        if (paths.Count == 0)
        {
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(Array.Empty<string>(), s_jsonOptions));
            else
                Console.Error.WriteLine($"No files matching '{pattern}' found.");
            return;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(paths, s_jsonOptions));
            return;
        }

        foreach (var p in paths)
            Console.WriteLine(p);
    }

    /// <summary>Scan work items in a job to find files matching a pattern.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="pattern">File name or glob pattern (e.g., *.binlog, *.trx, *.dmp).</param>
    /// <param name="maxItems">Max work items to scan (default 50).</param>
    [McpEquivalent("helix_find_files")]
    [Command("find-files")]
    public async Task FindFiles([Argument] string jobId, string pattern = "*", int maxItems = 50)
    {
        Console.WriteLine($"Scanning up to {maxItems} work items for '{pattern}'...");
        var results = await Svc.FindFilesAsync(jobId, pattern, maxItems);
        foreach (var r in results.Results)
        {
            Console.Write($"  {r.WorkItem}: ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(string.Join(", ", r.Files.Select(f => f.Name)));
            Console.ResetColor();
        }
        if (results.Results.Count == 0)
            Console.WriteLine($"  No files matching '{pattern}' found.");
        else if (results.Truncated)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  Scanned {maxItems} of {results.TotalWorkItems} work items. Use --max-items to scan more.");
            Console.ResetColor();
        }
    }

    /// <summary>Show detailed info about a specific work item.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [McpEquivalent("helix_work_item")]
    [Command("work-item")]
    public async Task WorkItem([Argument] string jobId, [Argument] string workItem, bool json = false, bool schema = false)
    {
        if (TryPrintSchema<WorkItemJsonResult>(schema))
            return;

        var detail = await Svc.GetWorkItemDetailAsync(jobId, workItem);

        if (json)
        {
            var result = new WorkItemJsonResult
            {
                Name = detail.Name,
                ExitCode = detail.ExitCode,
                IsCompleted = detail.IsCompleted,
                State = detail.State,
                MachineName = detail.MachineName,
                Duration = detail.Duration?.ToString(),
                ConsoleLogUrl = detail.ConsoleLogUrl,
                FailureCategory = detail.FailureCategory?.ToString(),
                Files = detail.Files.Select(f => new HelixFileJsonResult { Name = f.Name, Uri = f.Uri }).ToList()
            };
            Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Work Item: {detail.Name}");
        Console.WriteLine($"Exit Code: {detail.ExitCode}");
        Console.WriteLine($"Completed: {detail.IsCompleted}");
        if (detail.FailureCategory.HasValue)
            Console.WriteLine($"Category:  {detail.FailureCategory.Value}");
        if (!string.IsNullOrEmpty(detail.State))
            Console.WriteLine($"State:     {detail.State}");
        if (!string.IsNullOrEmpty(detail.MachineName))
            Console.WriteLine($"Machine:   {detail.MachineName}");
        if (detail.Duration.HasValue)
            Console.WriteLine($"Duration:  {FormatDuration(detail.Duration.Value)}");
        Console.WriteLine($"Log:       {detail.ConsoleLogUrl}");

        if (detail.Files.Count > 0)
        {
            Console.WriteLine($"\nFiles ({detail.Files.Count}):");
            foreach (var f in detail.Files)
            {
                var tag = HelixService.MatchesPattern(f.Name, "*.binlog") ? " [binlog]"
                    : HelixService.IsTestResultFile(f.Name) ? " [test-results]"
                    : "";
                Console.WriteLine($"  {f.Name}{tag}");
            }
        }
    }

    /// <summary>Get status for multiple Helix jobs at once.</summary>
    /// <param name="jobIds">One or more Helix job IDs or URLs.</param>
    [McpEquivalent("helix_batch_status")]
    [Command("batch-status")]
    public async Task BatchStatus([Argument] params string[] jobIds)
    {
        Console.Error.Write("Fetching batch status...");
        var batch = await Svc.GetBatchStatusAsync(jobIds);
        Console.Error.WriteLine(" done.");

        foreach (var job in batch.Jobs)
        {
            var idPrefix = job.JobId.Length >= 8 ? job.JobId[..8] : job.JobId;
            var line = $"Job {idPrefix}...: {job.Name} — {job.Failed.Count} failed, {job.Passed.Count} passed";
            if (job.InProgress.Count > 0)
                line += $", {job.InProgress.Count} in progress";
            Console.WriteLine(line);
        }

        var overall = $"Overall: {batch.TotalFailed} failed, {batch.TotalPassed} passed";
        if (batch.TotalInProgress > 0)
            overall += $", {batch.TotalInProgress} in progress";
        overall += $" across {batch.Jobs.Count} jobs";
        Console.WriteLine(overall);

        var allFailed = batch.Jobs.SelectMany(j => j.Failed).Where(f => f.FailureCategory.HasValue).ToList();
        if (allFailed.Count > 0)
        {
            var breakdown = allFailed
                .GroupBy(f => f.FailureCategory!.Value)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}");
            Console.WriteLine($"Failure breakdown: {string.Join(", ", breakdown)}");
        }
    }

    /// <summary>Search a work item's console log or uploaded file for a pattern.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="pattern">Text pattern to search for (case-insensitive substring match).</param>
    /// <param name="context">Number of context lines before and after each match.</param>
    /// <param name="maxMatches">Maximum number of matches to return (default 100).</param>
    /// <param name="fileName">File name to search (omit for console log).</param>
    [McpEquivalent("helix_search")]
    [Command("search-log")]
    public async Task SearchLog([Argument] string jobId, [Argument] string workItem,
        [Argument] string pattern, int context = 2, int maxMatches = 100, string? fileName = null)
    {
        static void PrintMatches(IReadOnlyList<LogMatch> matches, int contextLines)
        {
            foreach (var match in matches)
            {
                Console.WriteLine($"--- Line {match.LineNumber} ---");
                if (match.Context != null && contextLines > 0)
                {
                    int startLine = Math.Max(1, match.LineNumber - contextLines);
                    for (int i = 0; i < match.Context.Count; i++)
                    {
                        int lineNum = startLine + i;
                        if (lineNum == match.LineNumber)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"  {lineNum}: {match.Context[i]}");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.WriteLine($"  {lineNum}: {match.Context[i]}");
                        }
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  {match.LineNumber}: {match.Line}");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fileResult = await Svc.SearchFileAsync(jobId, workItem, fileName, pattern, context, maxMatches);

            if (fileResult.IsBinary)
            {
                Console.Error.WriteLine($"File '{fileName}' appears to be binary and cannot be searched.");
                return;
            }

            Console.WriteLine($"Searching for \"{pattern}\" in {fileResult.FileName} ({fileResult.TotalLines} lines)...");
            Console.WriteLine($"Found {fileResult.Matches.Count} matches{(fileResult.Truncated ? " (truncated)" : "")}:");
            Console.WriteLine();
            PrintMatches(fileResult.Matches, context);
            Console.WriteLine($"{fileResult.Matches.Count} matches found (showing up to {maxMatches}).");
            return;
        }

        var result = await Svc.SearchConsoleLogAsync(jobId, workItem, pattern, context, maxMatches);

        Console.WriteLine($"Searching for \"{pattern}\" in console log ({result.TotalLines} lines)...");
        Console.WriteLine($"Found {result.Matches.Count} matches{(result.Truncated ? " (truncated)" : "")}:");
        Console.WriteLine();
        PrintMatches(result.Matches, context);
        Console.WriteLine($"{result.Matches.Count} matches found (showing up to {maxMatches}).");
    }

    /// <summary>Parse TRX/xUnit XML test result files uploaded to Helix blob storage.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="fileName">Specific TRX file name (optional - auto-discovers all .trx files if not set).</param>
    /// <param name="includePassed">Include passed tests in output (default: false).</param>
    /// <param name="maxResults">Maximum number of test results to return (default: 200).</param>
    [McpEquivalent("helix_parse_uploaded_trx")]
    [Command("parse-uploaded-trx")]
    public async Task TestResults([Argument] string jobId, [Argument] string workItem,
        string? fileName = null, bool includePassed = false, int maxResults = 200)
    {
        var trxResults = await Svc.ParseTrxResultsAsync(jobId, workItem, fileName, includePassed, maxResults);

        foreach (var file in trxResults)
        {
            Console.WriteLine($"File: {file.FileName}");
            Console.WriteLine($"  Total: {file.TotalTests}  Passed: {file.Passed}  Failed: {file.Failed}  Skipped: {file.Skipped}");
            Console.WriteLine();

            foreach (var test in file.Results)
            {
                if (test.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("  [FAIL] ");
                }
                else if (test.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("  [PASS] ");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"  [{test.Outcome.ToUpperInvariant()}] ");
                }
                Console.ResetColor();

                Console.Write(test.TestName);
                if (test.Duration != null)
                    Console.Write($" ({test.Duration})");
                Console.WriteLine();

                if (test.ErrorMessage != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"         {test.ErrorMessage}");
                    Console.ResetColor();
                }
                if (test.StackTrace != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"         {test.StackTrace}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }
    }

    /// <summary>Print comprehensive tool documentation for LLM agents.</summary>
    [Command("llms-txt")]
    public void LlmsTxt()
    {
        var text = """
# hlx - Helix & Azure DevOps CI Investigation CLI

## CLI Commands
- `hlx status <jobId> [failed|passed|all]` — Work item summary (failed items with exit code, duration, machine)
- `hlx logs <jobId> <workItem>` — Download console log to temp file
- `hlx files <jobId> <workItem>` — List uploaded files for a work item
- `hlx download <jobId> <workItem> [--pattern PATTERN]` or `hlx download --url URL` — Download artifacts or a direct blob URL
- `hlx find-files <jobId> [--pattern PATTERN] [--max-items N]` — Search work items for files matching a pattern
- `hlx work-item <jobId> <workItem> [--json]` — Detailed work item info (exit code, state, machine, duration, files)
- `hlx batch-status <jobId1> <jobId2> ...` — Status for multiple jobs in parallel
- `hlx search-log <jobId> <workItem> <pattern> [--file-name NAME] [--context N] [--max-matches N]` — Search console logs or uploaded files
- `hlx cache status` — Show cache size, entry count, oldest/newest entries
- `hlx cache clear` — Wipe all cached data (SQLite + artifact files)

### AzDO CLI Commands
- `hlx azdo build <buildId> [--json]` — Get build details (status, result, branch, timing, URL)
- `hlx azdo builds [--org ORG] [--project PROJ] [--top N] [--branch B] [--pr-number N] [--definition-id N] [--status S] [--json]` — List builds (default top 20)
- `hlx azdo timeline <buildId> [--filter failed|all|...] [--record-id ID] [--parent-id ID] [--type T] [--result R] [--state S] [--name GLOB] [--expand MODE] [--projection triage|compact|full|summary] [--offset N] [--limit N] [--all] [--output PATH] [--allow-truncated] [--json] [--raw-json]` — Triage an AzDO build timeline: error/Helix-first Jobs/Tasks, short deduplicated issue previews, log IDs. Defaults to triage (Phase rows omitted); `--raw-json` preserves the legacy `{id,records}` shape for offline replay.
- `hlx azdo log <buildId> <logId> [--tail-lines N] [--full]` — Get build log content (last N lines by default, or the full log)
- `hlx azdo search-log <buildId> [--log-id N] [--pattern P] [--context-lines N] [--max-matches N] [--max-logs N] [--min-lines N] [--json]` — Search one build log or all ranked logs (defaults: 100 matches, 50 logs)
- `hlx azdo search-timeline <buildId> <pattern> [--type Stage|Job|Task] [--result failed|all] [--json]` — Search timeline records by name/issue pattern
- `hlx azdo changes <buildId> [--limit N|--top N] [--offset N] [--all] [--allow-truncated] [--json]` — Commits/changes associated with a build
- `hlx azdo test-runs <buildId> [--limit N|--top N] [--offset N] [--all] [--allow-truncated] [--json]` — List test runs for a build
- `hlx azdo test-results <buildId> <runId> [--limit N|--top N] [--offset N] [--all] [--allow-truncated] [--json]` — Test results for a test run (defaults to failed)
- `hlx azdo artifacts <buildId> [--pattern P] [--limit N|--top N] [--offset N] [--all] [--allow-truncated] [--json]` — List build artifacts with optional pattern filter
- `hlx azdo test-attachments <runId> <resultId> [--org ORG] [--project PROJ] [--limit N|--top N] [--offset N] [--all] [--allow-truncated] [--json]` — Test result attachments

Updated AzDO list commands emit `{ ok, results, returned, total, offset, limit, complete, truncated, next, cache, note }` with `--json`; truncated output exits 2 unless `--allow-truncated` is supplied.

## MCP Server
- `hlx mcp` — Start MCP server over stdio (for VS Code, Claude Desktop, etc.)
- `hlx-mcp` or `dotnet run --project src/HelixTool.Mcp` — HTTP MCP server

### MCP Tools
- `helix_status` — Job pass/fail summary with consoleLogUrl and failureCategory per work item
- `helix_logs` — Console log content (last N lines)
- `helix_files` — List uploaded files, grouped by type (binlogs, testResults, other)
- `helix_download` — Download files by glob pattern or direct blob URL to temp storage
- `helix_find_files` — Search work items for files matching a glob pattern (*.binlog, *.trx, *.dmp, etc.)
- `helix_work_item` — Detailed work item info with exit code, state, machine, duration, files
- `helix_batch_status` — Status for multiple jobs in parallel (accepts an array of job IDs/URLs)
- `helix_search` — Remote-first search for console logs or uploaded files when failures live in Helix output
- `helix_parse_uploaded_trx` — Parse TRX/xUnit XML files uploaded to Helix blob storage; most repos use azdo_test_results instead

### AzDO MCP Tools
- `azdo_build` — Get build details by ID or AzDO URL (status, result, branch, timing, web URL)
- `azdo_builds` — List builds with filters (definition, branch, PR number, status). Defaults to dnceng-public/public, top 20
- `azdo_timeline` — Get build timeline (stages, jobs, tasks) with optional filter ('failed' or 'all'). Returns log IDs for azdo_log
- `azdo_log` — Get build log content (last N lines, default 500). Use log ID from azdo_timeline
- `azdo_search_log` — Search one build log by log ID or all ranked build logs for a pattern (defaults: 100 matches, 50 logs)
- `azdo_search_timeline` — Search build timeline records by name or issue message pattern. Find specific failed steps or errors
- `azdo_changes` — Get commits/changes associated with a build
- `azdo_test_runs` — List test runs for a build (total/passed/failed counts)
- `azdo_test_results` — Get test results for a test run (outcome, duration, error details). Defaults to failed only (top 200)
- `azdo_artifacts` — List build artifacts with optional pattern filter (e.g., '*.binlog'). Default top: 100
- `azdo_test_attachments` — List attachments for a test result (screenshots, logs, dumps). Default top: 100

## Authentication
Set HELIX_ACCESS_TOKEN env var for internal Helix jobs. Public jobs need no auth.
Set AZDO_TOKEN env var for internal AzDO projects. Falls back to `az account get-access-token`. Public projects (e.g., dnceng-public) need no auth.

## Input
- Accepts bare GUIDs: `hlx status 02d8bd09-9400-4e86-8d2b-7a6ca21c5009`
- Accepts Helix URLs: `hlx status https://helix.dot.net/api/jobs/02d8bd09.../details`
- Accepts AzDO URLs: `azdo_build https://dev.azure.com/dnceng-public/public/_build/results?buildId=123`
- Accepts AzDO build IDs: `azdo_build 123` (defaults to dnceng-public/public)
- jobId and workItem are positional arguments (no --job-id flag needed)

## Failure Categorization
Failed work items are auto-classified: Timeout, Crash, BuildFailure, TestFailure, InfrastructureError, AssertionFailure, Unknown.
Available as `failureCategory` in JSON and MCP output.

## Output
- `status` prints summary with per-work-item state, exit code, duration, machine, consoleLogUrl, failureCategory
- `work-item` shows detailed info including all uploaded files
- `batch-status` shows per-job summary and overall totals with failure breakdown
- `logs` and `download` save files to %TEMP% and print paths

## Caching
- SQLite-backed cross-process cache (shared across `hlx mcp` stdio instances)
- Cache isolated per auth context: unauthenticated uses `{base}/public/`, token uses `{base}/cache-{hash}/` (first 8 chars of SHA256)
- Cache location base: `%LOCALAPPDATA%/hlx/` (Windows) or `$XDG_CACHE_HOME/hlx/` (Linux/macOS)
- Default max size: 1 GB. Configure via `HLX_CACHE_MAX_SIZE_MB` env var. Set to 0 to disable.
- Helix TTL: Running jobs 15-30s, completed 1-4h. Console logs never cached while running.
- AzDO TTL: Completed builds 4h, in-progress 15s, logs 4h (immutable), test results 1h.
- Eviction: TTL-based + LRU when over max size. Artifacts expire after 7 days without access.
- `cache clear` wipes ALL auth contexts. `cache status` shows the current auth context.
""";
        Console.Write(text);
    }

    /// <summary>Start the MCP server over stdio for use with MCP clients (VS Code, Claude Desktop, etc.).</summary>
    [Command("mcp")]
    public async Task Mcp()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddHttpClient("HelixDownload", c =>
        {
            c.Timeout = TimeSpan.FromMinutes(5);
            HelixToolUserAgent.Apply(c);
        });
        builder.Services.AddHttpClient("AzDO", c =>
        {
            c.Timeout = TimeSpan.FromMinutes(5);
            HelixToolUserAgent.Apply(c);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<IHelixTokenAccessor>(_ =>
            new EnvironmentHelixTokenAccessor(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")));
        builder.Services.AddSingleton<IHelixApiClientFactory, HelixApiClientFactory>();

        var mcpEvalSnapshotDir = Environment.GetEnvironmentVariable("HLX_EVAL_SNAPSHOT");
        if (!string.IsNullOrEmpty(mcpEvalSnapshotDir))
        {
            var resolvedSnapshot = Path.GetFullPath(mcpEvalSnapshotDir);
            var azdoPartition = EvalSnapshotAzdoPartitionSelector.Select(resolvedSnapshot);
            var evalOptions = new CacheOptions
            {
                CacheRoot = resolvedSnapshot,
                EvalMode = true,
                CacheRootHash = null,
                AuthTokenHash = azdoPartition.AuthTokenHash,
            };
            builder.Services.AddEvalModeCore(evalOptions);
            builder.Services.AddSingleton<AzdoService>(sp =>
                new AzdoService(
                    sp.GetRequiredService<IAzdoApiClient>(),
                    sp.GetRequiredService<IHelixApiClient>(),
                    sp.GetRequiredService<IAzdoAcquisitionFailureRecorder>(),
                    sp.GetRequiredService<CacheOptions>()));
        }
        else
        {
        builder.Services.AddSingleton<CacheOptions>(_ =>
        {
            var opts = new CacheOptions { AuthTokenHash = null };
            var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
            if (int.TryParse(maxStr, out var mb))
                opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
            return opts;
        });
        builder.Services.AddSingleton<ICacheStore>(sp => new SqliteCacheStore(sp.GetRequiredService<CacheOptions>()));
        builder.Services.AddSingleton(sp => new Lazy<HelixApiClient>(() =>
        {
            var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
            return new HelixApiClient(token);
        }));
        builder.Services.AddSingleton<HelixApiClient>(sp => sp.GetRequiredService<Lazy<HelixApiClient>>().Value);
        builder.Services.AddSingleton<IHelixApiClient>(sp =>
            new CachingHelixApiClient(
                sp.GetRequiredService<HelixApiClient>(),
                sp.GetRequiredService<ICacheStore>(),
                sp.GetRequiredService<CacheOptions>()));
        builder.Services.AddSingleton<HelixService>(sp =>
            new HelixService(
                sp.GetRequiredService<IHelixApiClient>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("HelixDownload")));

        // AzDO services — same decorator pattern as Helix
        builder.Services.AddSingleton<IAzdoTokenAccessor, AzCliAzdoTokenAccessor>();
        builder.Services.AddSingleton<AzdoApiClient>(sp =>
            new AzdoApiClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("AzDO"),
                sp.GetRequiredService<IAzdoTokenAccessor>(),
                sp.GetRequiredService<CacheOptions>()));
        builder.Services.AddSingleton<IAzdoApiClient>(sp =>
            new CachingAzdoApiClient(
                sp.GetRequiredService<AzdoApiClient>(),
                sp.GetRequiredService<ICacheStore>(),
                sp.GetRequiredService<CacheOptions>(),
                sp.GetRequiredService<IAzdoTokenAccessor>()));
        builder.Services.AddSingleton<IAzdoAcquisitionFailureRecorder>(sp =>
            new CachingAzdoAcquisitionFailureRecorder(
                sp.GetRequiredService<ICacheStore>(),
                sp.GetRequiredService<CacheOptions>()));
        // Inject IHelixApiClient so GetHelixJobsAsync can use the canonical Helix-side Job.ListAsync(source) path (#92)
        builder.Services.AddSingleton<AzdoService>(sp =>
            new AzdoService(
                sp.GetRequiredService<IAzdoApiClient>(),
                sp.GetRequiredService<IHelixApiClient>(),
                sp.GetRequiredService<IAzdoAcquisitionFailureRecorder>(),
                sp.GetRequiredService<CacheOptions>()));
        }

        // Shared evidence delivery store (P0-1 design 2.1a); backs hlx_read_evidence for the stdio MCP server.
        builder.Services.AddSingleton<HelixTool.Core.Delivery.IEvidenceDeliveryStore>(sp =>
            new HelixTool.Core.Delivery.FileEvidenceDeliveryStore(sp.GetRequiredService<CacheOptions>()));

        builder.Services
            .AddMcpServer(options =>
            {
                var serverVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
                options.ServerInfo = new() { Name = "hlx", Version = serverVersion };

                options.AddBindingErrorFilter();
                // Stage B: did-you-mean filter — runs after alias normalization, before SDK dispatch.
                // Intercepts unknown params with structured McpException + Levenshtein hints.
                // Stage A's UnmappedMemberHandling.Disallow (below) remains as defense-in-depth.
                options.AddUnknownParameterFilter(typeof(HelixMcpTools).Assembly);
                options.AddAcquisitionErrorFilter();
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(HelixMcpTools).Assembly, new JsonSerializerOptions
            {
                // Reject unknown parameters at binding time so callers get a structured error
                // instead of silent data loss. The AddBindingErrorFilter above catches the resulting
                // ArgumentException(paramName:"arguments") and wraps it as McpException.
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                // Required: SDK calls MakeReadOnly() on options before schema gen; without a
                // TypeInfoResolver set, CreateJsonSchemaCore tries to assign one post-lock → InvalidOperationException.
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            })
            .WithResourcesFromAssembly(typeof(HelixMcpTools).Assembly);
        await builder.Build().RunAsync();
    }

    /// <summary>Clear all cached data across ALL auth contexts.</summary>
    [Command("cache clear")]
    public async Task CacheClear()
    {
        // Clear current auth context's cache store
        var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
        await cache.ClearAsync();

        // Also wipe all sibling auth context directories under the base root
        var options = ConsoleApp.ServiceProvider!.GetRequiredService<CacheOptions>();
        var baseRoot = options.GetBaseCacheRoot();
        if (Directory.Exists(baseRoot))
        {
            foreach (var dir in Directory.GetDirectories(baseRoot))
            {
                var name = Path.GetFileName(dir);
                if (name == "public" || name.StartsWith("cache-", StringComparison.Ordinal))
                {
                    // Skip current context (already cleared via ICacheStore)
                    var currentRoot = options.GetEffectiveCacheRoot();
                    if (string.Equals(dir, currentRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { Directory.Delete(dir, recursive: true); }
                    catch (IOException) { /* best effort */ }
                }
            }
        }

        Console.WriteLine("Cache cleared (all auth contexts).");
    }

    /// <summary>Show cache size, entry count, oldest/newest entries for the current auth context.</summary>
    [Command("cache status")]
    public async Task CacheStatusCmd()
    {
        var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
        var options = ConsoleApp.ServiceProvider!.GetRequiredService<CacheOptions>();
        var status = await cache.GetStatusAsync();

        var context = options.AuthTokenHash != null ? $"authenticated (cache-{options.AuthTokenHash})" : "public (unauthenticated)";
        Console.WriteLine($"Cache status ({context}):");
        Console.WriteLine($"  Cache dir:       {options.GetEffectiveCacheRoot()}");
        Console.WriteLine($"  Max size:        {FormatBytes(status.MaxSizeBytes)}");
        Console.WriteLine($"  Current size:    {FormatBytes(status.TotalSizeBytes)}");
        Console.WriteLine($"  Metadata entries: {status.MetadataEntryCount}");
        Console.WriteLine($"  Artifact files:   {status.ArtifactFileCount}");
        if (status.OldestEntry.HasValue)
            Console.WriteLine($"  Oldest entry:    {status.OldestEntry.Value:u}");
        if (status.NewestEntry.HasValue)
            Console.WriteLine($"  Newest entry:    {status.NewestEntry.Value:u}");
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1L * 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        if (duration.TotalMinutes >= 1)
        {
            var minutes = (int)duration.TotalMinutes;
            var seconds = duration.Seconds;
            return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
        }
        return $"{(int)duration.TotalSeconds}s";
    }

    /// <summary>Authenticate with helix.dot.net by storing an API token.</summary>
    /// <param name="noBrowser">Skip opening the browser.</param>
    [Command("login")]
    public async Task Login(bool noBrowser = false)
    {
        const string tokenUrl = "https://helix.dot.net/Account/Tokens";

        Console.WriteLine("To authenticate, you need an API token from helix.dot.net:");
        Console.WriteLine();

        if (!noBrowser)
        {
            Console.WriteLine($"  1. Opening {tokenUrl} in your browser...");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(tokenUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                Console.WriteLine($"     Could not open browser. Open this URL manually:");
                Console.WriteLine($"     {tokenUrl}");
            }
        }
        else
        {
            Console.WriteLine($"  1. Open {tokenUrl} in your browser");
        }

        Console.WriteLine("  2. Log in with your Microsoft account if prompted");
        Console.WriteLine("  3. Generate a new API access token");
        Console.WriteLine("  4. Paste it below");
        Console.WriteLine();

        // Check for existing token
        var existing = await _credentialStore.GetTokenAsync("helix.dot.net", "helix-api-token");
        if (existing is not null)
        {
            Console.Write("A token is already stored. Replace it? [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled.");
                return;
            }
        }

        Console.Write("Token: ");
        var token = ReadSecret();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("No token provided.");
            return;
        }

        // Validate token
        Console.Write("Validating...");
        try
        {
            var testClient = new HelixApiClient(token);
            await testClient.ListWorkItemsAsync("00000000-0000-0000-0000-000000000000");
            // If we get here without a 401, token is valid (we'll get a 404 for fake job, which is fine)
            Console.WriteLine(" \u2713 Token is valid.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Console.Error.WriteLine(" \u2717 Token is invalid (401 Unauthorized). Not storing.");
            return;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 is expected for our fake job ID — token auth worked
            Console.WriteLine(" \u2713 Token is valid.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($" \u26a0 Could not validate ({ex.Message}). Storing anyway.");
        }

        await _credentialStore.StoreTokenAsync("helix.dot.net", "helix-api-token", token);
        Console.WriteLine("\u2713 Token stored successfully.");
    }

    /// <summary>Remove stored Helix authentication token.</summary>
    [Command("logout")]
    public async Task Logout()
    {
        await _credentialStore.DeleteTokenAsync("helix.dot.net", "helix-api-token");
        Console.WriteLine("\u2713 Token removed from credential store.");

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")))
        {
            Console.WriteLine("Note: HELIX_ACCESS_TOKEN environment variable is still set.");
            Console.WriteLine("Unset it to fully log out: unset HELIX_ACCESS_TOKEN");
        }
    }

    /// <summary>Show current authentication status.</summary>
    [Command("auth-status")]
    public async Task AuthStatus()
    {
        var token = _tokenAccessor.GetAccessToken();
        var source = _tokenAccessor.Source;

        switch (source)
        {
            case TokenSource.EnvironmentVariable:
                Console.WriteLine("Token source: HELIX_ACCESS_TOKEN environment variable");
                break;
            case TokenSource.StoredCredential:
                Console.WriteLine("Token source: stored credential (git credential)");
                break;
            default:
                Console.Error.WriteLine("\u26a0 No Helix token configured.");
                Console.Error.WriteLine("Run 'hlx login' to authenticate, or set HELIX_ACCESS_TOKEN.");
                Environment.ExitCode = 1;
                return;
        }

        // Test the token
        Console.Write("API test:     ");
        try
        {
            var testClient = new HelixApiClient(token);
            await testClient.ListWorkItemsAsync("00000000-0000-0000-0000-000000000000");
            Console.WriteLine("\u2713 Connected");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine("\u2713 Connected");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Console.WriteLine("\u2717 Token is invalid (401 Unauthorized)");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\u26a0 Could not connect ({ex.Message})");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>Read a line from stdin while masking input with bullet chars.</summary>
    private static string ReadSecret()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (!Console.IsInputRedirected)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }
                sb.Append(key.KeyChar);
                Console.Write('\u2022');
            }
            else
            {
                return Console.ReadLine() ?? string.Empty;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// CLI commands for Azure DevOps build and test investigation.
/// </summary>
public class AzdoCommands
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new AcquisitionErrorKindJsonConverter() }
    };

    private readonly AzdoService _svc;
    private readonly IAzdoTokenAccessor _tokenAccessor;
    private readonly HelixTool.Core.Cache.CacheOptions _cacheOptions;
    private readonly HelixTool.Core.Delivery.IEvidenceDeliveryStore _evidenceStore;

    public AzdoCommands(
        AzdoService svc,
        IAzdoTokenAccessor tokenAccessor,
        HelixTool.Core.Cache.CacheOptions? cacheOptions = null,
        HelixTool.Core.Delivery.IEvidenceDeliveryStore? evidenceStore = null)
    {
        _svc = svc;
        _tokenAccessor = tokenAccessor;
        _cacheOptions = cacheOptions ?? new HelixTool.Core.Cache.CacheOptions();
        _evidenceStore = evidenceStore ?? new HelixTool.Core.Delivery.FileEvidenceDeliveryStore(_cacheOptions);
    }

    private static bool TryCreatePageRequest(
        bool all,
        int offset,
        int? limit,
        int? top,
        out HlxPageRequest request)
    {
        request = new HlxPageRequest();
        if (offset < 0)
        {
            Console.Error.WriteLine("--offset must be greater than or equal to 0.");
            Environment.ExitCode = 1;
            return false;
        }

        if (limit is <= 0)
        {
            Console.Error.WriteLine("--limit must be greater than 0.");
            Environment.ExitCode = 1;
            return false;
        }

        if (top is <= 0)
        {
            Console.Error.WriteLine("--top must be greater than 0.");
            Environment.ExitCode = 1;
            return false;
        }

        if (limit.HasValue && top.HasValue)
        {
            Console.Error.WriteLine("--limit and --top are aliases; specify only one.");
            Environment.ExitCode = 1;
            return false;
        }

        if (all && (offset != 0 || limit.HasValue || top.HasValue))
        {
            Console.Error.WriteLine("--all is mutually exclusive with --offset, --limit, and --top.");
            Environment.ExitCode = 1;
            return false;
        }

        request = new HlxPageRequest
        {
            All = all,
            Offset = offset,
            Limit = limit ?? top
        };
        return true;
    }

    private static void ApplyTruncatedExit<T>(HlxListEnvelope<T> envelope, bool allowTruncated)
    {
        if (envelope.Truncated && !allowTruncated)
            Environment.ExitCode = 2;
    }

    private static void PrintPagingWarning<T>(HlxListEnvelope<T> envelope)
    {
        if (envelope.Truncated && envelope.Note is not null)
            Console.WriteLine($"Warning: {envelope.Note}");
    }

    /// <summary>Show the currently resolved Azure DevOps authentication path without making an AzDO API request.</summary>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [Command("azdo auth-status")]
    public async Task AuthStatus(bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<AzdoAuthStatus>(schema))
            return;

        var status = await _tokenAccessor.AuthStatusAsync();

        if (!status.IsAuthenticated)
            Environment.ExitCode = 1;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(status, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Path:           {status.Path}");
        Console.WriteLine($"Credential:     {status.Source}");
        Console.WriteLine($"Authenticated:  {(status.IsAuthenticated ? "yes" : "no")}");
        Console.WriteLine($"Looks expired:  {FormatExpirationStatus(status.LooksExpired)}");
        if (status.ExpiresOnUtc.HasValue)
            Console.WriteLine($"Expires:        {status.ExpiresOnUtc.Value:u}");

        foreach (var warning in status.Warnings)
            Console.WriteLine($"Warning:        {warning}");
    }

    /// <summary>Get details of a specific Azure DevOps build.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [McpEquivalent("azdo_build")]
    [Command("azdo build")]
    public async Task Build([Argument] string buildId, bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<AzdoBuildSummary>(schema))
            return;

        var summary = await _svc.GetBuildSummaryAsync(buildId);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(summary, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Build #{summary.Id}: {summary.BuildNumber}");
        Console.WriteLine($"  Definition: {summary.DefinitionName} ({summary.DefinitionId})");
        Console.WriteLine($"  Status:     {summary.Status}  Result: {summary.Result}");
        Console.WriteLine($"  Branch:     {summary.SourceBranch}");
        Console.WriteLine($"  Commit:     {summary.SourceVersion}");
        Console.WriteLine($"  Requested:  {summary.RequestedFor}");
        if (summary.StartTime.HasValue)
            Console.WriteLine($"  Started:    {summary.StartTime.Value:u}");
        if (summary.FinishTime.HasValue)
            Console.WriteLine($"  Finished:   {summary.FinishTime.Value:u}");
        if (summary.Duration.HasValue)
            Console.WriteLine($"  Duration:   {Commands.FormatDuration(summary.Duration.Value)}");
        Console.WriteLine($"  URL:        {summary.WebUrl}");
    }

    /// <summary>List recent builds for an Azure DevOps project.</summary>
    /// <param name="org">Azure DevOps organization (default: dnceng-public).</param>
    /// <param name="project">Azure DevOps project (default: public).</param>
    /// <param name="top">Maximum number of builds to return (default 20).</param>
    /// <param name="branch">Filter by branch name.</param>
    /// <param name="prNumber">Filter by pull request number.</param>
    /// <param name="definitionId">Filter by pipeline definition ID.</param>
    /// <param name="status">Filter by build status.</param>
    /// <param name="minTime">Lower bound on the build time field selected by queryOrder. With the default queryOrder (queueTimeDescending), filters by queueTime. Use finishTimeDescending to filter by finishTime, etc. (ISO 8601).</param>
    /// <param name="maxTime">Upper bound on the build time field selected by queryOrder. With the default queryOrder (queueTimeDescending), filters by queueTime. Use finishTimeDescending to filter by finishTime, etc. (ISO 8601).</param>
    /// <param name="queryOrder">Order results by time field (e.g. finishTimeDescending). Default: queueTimeDescending.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_builds")]
    [Command("azdo builds")]
    public async Task Builds(string org = "dnceng-public", string project = "public",
        int top = 20, string? branch = null, string? prNumber = null,
        int? definitionId = null, string? status = null,
        DateTimeOffset? minTime = null, DateTimeOffset? maxTime = null,
        string? queryOrder = null, bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<IReadOnlyList<AzdoBuild>>(schema))
            return;

        queryOrder = AzdoService.NormalizeQueryOrder(queryOrder);
        if (!AzdoService.IsValidQueryOrder(queryOrder))
        {
            Console.Error.WriteLine(AzdoService.GetInvalidQueryOrderMessage(queryOrder!));
            Environment.ExitCode = 1;
            return;
        }

        var filter = new AzdoBuildFilter
        {
            PrNumber = prNumber,
            Branch = branch,
            DefinitionId = definitionId,
            Top = top,
            StatusFilter = status,
            MinTime = minTime,
            MaxTime = maxTime,
            QueryOrder = queryOrder
        };

        var builds = await _svc.ListBuildsAsync(org, project, filter);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(builds, s_jsonOptions));
            return;
        }

        if (builds.Count == 0)
        {
            Console.WriteLine("No builds found.");
            return;
        }

        foreach (var b in builds)
        {
            var result = b.Result ?? b.Status ?? "unknown";
            var defName = b.Definition?.Name ?? "?";
            Console.Write($"  #{b.Id} ");

            if (result.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Green;
            else if (result.Equals("failed", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Red;
            else
                Console.ForegroundColor = ConsoleColor.Yellow;

            Console.Write($"[{result}]");
            Console.ResetColor();
            Console.Write($" {defName}");
            if (b.SourceBranch is not null)
                Console.Write($" ({b.SourceBranch})");
            if (b.FinishTime.HasValue)
                Console.Write($" {b.FinishTime.Value:u}");
            Console.WriteLine();
        }
    }

    /// <summary>Triage an AzDO build timeline: error/Helix-first Jobs/Tasks with short deduplicated issue text and log IDs.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="filter">Effective 'failed' for ordinary triage; 'all' when an explicit selector is supplied and no preset is given.</param>
    /// <param name="recordId">Exact timeline record GUID.</param>
    /// <param name="parentId">Select records whose direct parent is this GUID.</param>
    /// <param name="type">Exact record type: Stage, Phase, Job, Task, Checkpoint.</param>
    /// <param name="result">Exact result: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none.</param>
    /// <param name="state">Exact provider state: pending, inProgress, completed.</param>
    /// <param name="name">Case-insensitive name glob using * and ?.</param>
    /// <param name="expand">none, ancestors (default), children, descendants, ancestorsAndChildren, or ancestorsAndDescendants.</param>
    /// <param name="projection">triage (default), compact, full, or summary.</param>
    /// <param name="includePhase">Effective default false for triage/summary, true for full/compact.</param>
    /// <param name="previewIssueLimit">Efficiency default per row.</param>
    /// <param name="previewChars">Efficiency default Unicode-scalar preview length per issue.</param>
    /// <param name="offset">Offset into the deduplicated expanded record set.</param>
    /// <param name="limit">Requested row window.</param>
    /// <param name="viewId">Source/view fingerprint from a prior response.</param>
    /// <param name="issueOffset">Issue offset for one recordId with projection='full'.</param>
    /// <param name="issueLimit">Explicit issue window for one full record.</param>
    /// <param name="all">Retrieve the complete selected scope (ignores limit shaping).</param>
    /// <param name="output">Write the complete requested selection as JSON to this file and print a small receipt.</param>
    /// <param name="allowTruncated">Exit 0 even when the output is truncated.</param>
    /// <param name="json">Output the #154 JSON envelope.</param>
    /// <param name="rawJson">Output the legacy {id,records} shape with full Phase/issue detail (offline replay path).</param>
    /// <param name="delivery">auto (default) keeps a useful inline page; file/all/chunked retrieve the complete selection and deliver via --output or a verified evidence reference.</param>
    [McpEquivalent("azdo_timeline")]
    [Command("azdo timeline")]
    public async Task Timeline(
        [Argument] string buildId,
        string? filter = null,
        string? recordId = null,
        string? parentId = null,
        string? type = null,
        string? result = null,
        string? state = null,
        string? name = null,
        string expand = "ancestors",
        string projection = "triage",
        bool? includePhase = null,
        int previewIssueLimit = 5,
        int previewChars = 200,
        int offset = 0,
        int limit = 10,
        string? viewId = null,
        int issueOffset = 0,
        int? issueLimit = null,
        bool all = false,
        string delivery = "auto",
        string? output = null,
        bool allowTruncated = false,
        bool json = false,
        bool rawJson = false,
        bool schema = false)
    {
        if (Commands.TryPrintSchema<AzdoTimeline>(schema))
            return;

        var timeline = await _svc.GetTimelineAsync(buildId);
        if (timeline is null)
        {
            Console.WriteLine($"No timeline available for build {buildId}.");
            return;
        }

        if (rawJson)
        {
            // Legacy offline replay path: original {id,records} shape, full Phase/issue detail,
            // original 'failed'/'all' semantics only. Not the new #154 envelope.
            var legacyFilter = string.IsNullOrEmpty(filter) ? "failed" : filter;
            legacyFilter = AzdoService.NormalizeFilter(legacyFilter);
            if (!AzdoService.IsValidFilter(legacyFilter))
                throw new ArgumentException(AzdoService.GetInvalidFilterMessage(legacyFilter), nameof(filter));

            var legacyRecords = timeline.Records;
            if (!legacyFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in legacyRecords)
                    if (r.Id is not null && AzdoService.MatchesFilter(r, legacyFilter))
                        matchedIds.Add(r.Id);

                var allIds = new HashSet<string>(matchedIds, StringComparer.OrdinalIgnoreCase);
                var recordById = legacyRecords.Where(r => r.Id is not null).ToDictionary(r => r.Id!, StringComparer.OrdinalIgnoreCase);
                foreach (var id in matchedIds)
                {
                    var current = recordById.GetValueOrDefault(id);
                    while (current?.ParentId is not null && allIds.Add(current.ParentId))
                        current = recordById.GetValueOrDefault(current.ParentId);
                }
                legacyRecords = legacyRecords.Where(r => r.Id is not null && allIds.Contains(r.Id)).ToList();
            }

            Console.WriteLine(JsonSerializer.Serialize(new AzdoTimeline { Id = timeline.Id, Records = legacyRecords }, s_jsonOptions));
            return;
        }

        var wantsComplete = all || output is not null
            || delivery.Equals("file", StringComparison.OrdinalIgnoreCase)
            || delivery.Equals("all", StringComparison.OrdinalIgnoreCase)
            || delivery.Equals("chunked", StringComparison.OrdinalIgnoreCase);

        var request = new TimelineProjectionRequest
        {
            Filter = filter,
            RecordId = recordId,
            ParentId = parentId,
            Type = type,
            Result = result,
            State = state,
            Name = name,
            Expand = expand,
            Projection = projection,
            IncludePhase = includePhase,
            PreviewIssueLimit = previewIssueLimit,
            PreviewChars = previewChars,
            Offset = offset,
            Limit = limit,
            ViewId = viewId,
            IssueOffset = issueOffset,
            IssueLimit = issueLimit,
            Delivery = delivery,
            All = wantsComplete,
            BuildIdOrUrl = buildId
        };

        TimelineProjectionResult Project(TimelineProjectionRequest r) => AzdoTimelineProjector.Project(timeline, r);

        var (cacheKey, cachePartition) = _svc.ResolveTimelineCacheIdentity(buildId);
        var cache = new HlxCacheProvenance { Key = cacheKey, CompleteKey = cacheKey };

        TimelineProjectionResult view;
        try
        {
            // An explicit --output destination always writes the complete requested representation
            // there directly (today's established CLI contract). Without --output, route through the
            // same auto/file/chunked shaping+evidence-store delivery the MCP tool uses, so `--all`/
            // `--delivery file|chunked` without --output still materializes a readable, verified
            // evidence file instead of silently truncating to the default row window.
            view = output is not null
                ? Project(request)
                : McpPresentationBudget.ShapeTimeline(request, Project, _evidenceStore, cachePartition, cacheKey);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        if (output is not null)
        {
            if (!TryGuardOutputDestination(output, out var guardError))
            {
                Environment.ExitCode = 1;
                var safeOutput = Path.Combine(Path.GetTempPath(), $"hlx-timeline-output-{Guid.NewGuid():N}.json");
                var recoveryArgv = BuildRecoveryArgv(
                    buildId, filter, recordId, parentId, type, result, state, name, expand, projection, includePhase,
                    previewIssueLimit, previewChars, offset, limit, viewId, issueOffset, issueLimit, all, delivery,
                    safeOutput, allowTruncated, json, rawJson);
                if (json)
                {
                    var errorEnvelope = new
                    {
                        ok = false,
                        error = guardError,
                        recovery = new { tool = "hlx", argv = recoveryArgv }
                    };
                    Console.WriteLine(JsonSerializer.Serialize(errorEnvelope, s_jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine(guardError);
                    Console.Error.WriteLine($"Retry outside the snapshot: {string.Join(' ', recoveryArgv.Skip(1))}");
                }
                return;
            }

            var envelope = BuildCliEnvelope(view, cache);
            var fileJson = JsonSerializer.Serialize(envelope, s_jsonOptions);
            await File.WriteAllTextAsync(output, fileJson);
            var bytes = await File.ReadAllBytesAsync(output);
            var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            var receipt = new
            {
                ok = true,
                delivery = new
                {
                    complete = true,
                    localPath = Path.GetFullPath(output),
                    bytes = bytes.LongLength,
                    sha256
                }
            };
            Console.WriteLine(JsonSerializer.Serialize(receipt, s_jsonOptions));
            return;
        }


        if (json)
        {
            var envelope = BuildCliEnvelope(view, cache);
            Console.WriteLine(JsonSerializer.Serialize(envelope, s_jsonOptions));
            if (!IsEffectivelyComplete(view) && !allowTruncated)
                Environment.ExitCode = 2;
            return;
        }

        foreach (var row in view.Records)
        {
            var element = JsonSerializer.SerializeToElement(row);
            var rid = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var rname = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var rresult = element.TryGetProperty("result", out var resultProp) ? resultProp.GetString() : null;
            Console.WriteLine($"[{rresult ?? "?"}] {rname} ({rid})");
        }
        if (!IsEffectivelyComplete(view) && !allowTruncated)
            Environment.ExitCode = 2;
    }

    /// <summary>
    /// A small materialized-receipt response (verified evidence file covering the complete requested
    /// selection) is a full success even though its own top-level `complete`/`returned` describe the
    /// zero-row receipt, not the backing data — exit 0 for a complete file delivery, matching the
    /// existing explicit `--output` contract, and exit 2 only for a genuinely partial/paged response.
    /// </summary>
    private static bool IsEffectivelyComplete(TimelineProjectionResult view) =>
        view.Complete || (view.Delivery?.Complete ?? false);

    /// <summary>
    /// Rejects a caller-selected --output destination that would land inside the active immutable
    /// eval snapshot directory (HLX_EVAL_SNAPSHOT). That directory is replayed read-only for
    /// deterministic offline evidence; writing into it — even a legitimate query receipt — could
    /// corrupt a fixture or silently poison a future replay with content the snapshot never captured.
    /// </summary>
    private bool TryGuardOutputDestination(string output, out string error)
    {
        error = "";
        if (!_cacheOptions.EvalMode || string.IsNullOrEmpty(_cacheOptions.CacheRoot))
            return true;

        var snapshotRoot = Path.GetFullPath(_cacheOptions.GetEffectiveCacheRoot());
        var resolvedOutput = Path.GetFullPath(output);
        var snapshotRootWithSeparator = snapshotRoot.EndsWith(Path.DirectorySeparatorChar) ? snapshotRoot : snapshotRoot + Path.DirectorySeparatorChar;
        var isInsideSnapshot = string.Equals(resolvedOutput, snapshotRoot, StringComparison.OrdinalIgnoreCase)
            || resolvedOutput.StartsWith(snapshotRootWithSeparator, StringComparison.OrdinalIgnoreCase);
        if (!isInsideSnapshot)
            return true;

        error = $"--output '{output}' resolves inside the active read-only eval snapshot ('{snapshotRoot}'). " +
                "Choose a destination outside HLX_EVAL_SNAPSHOT; writing here could corrupt the immutable snapshot used for offline replay.";
        return false;
    }

    /// <summary>
    /// Reconstructs an equivalent `hlx azdo timeline` invocation with the unsafe --output destination
    /// replaced by <paramref name="safeOutput"/>, so a caller whose destination was rejected (because it
    /// resolved inside the immutable eval snapshot) has an exact, directly-executable recovery command
    /// rather than having to re-derive every other flag from scratch.
    /// </summary>
    private static string[] BuildRecoveryArgv(
        string buildId, string? filter, string? recordId, string? parentId, string? type, string? result, string? state,
        string? name, string expand, string projection, bool? includePhase, int previewIssueLimit, int previewChars,
        int offset, int limit, string? viewId, int issueOffset, int? issueLimit, bool all, string delivery,
        string safeOutput, bool allowTruncated, bool json, bool rawJson)
    {
        List<string> argv = ["hlx", "azdo", "timeline", buildId];
        void Add(string flag, string? value) { if (value is not null) { argv.Add(flag); argv.Add(value); } }
        Add("--filter", filter);
        Add("--record-id", recordId);
        Add("--parent-id", parentId);
        Add("--type", type);
        Add("--result", result);
        Add("--state", state);
        Add("--name", name);
        if (!expand.Equals("ancestors", StringComparison.OrdinalIgnoreCase)) Add("--expand", expand);
        if (!projection.Equals("triage", StringComparison.OrdinalIgnoreCase)) Add("--projection", projection);
        if (includePhase is not null) argv.AddRange(["--include-phase", includePhase.Value.ToString()]);
        if (previewIssueLimit != 5) Add("--preview-issue-limit", previewIssueLimit.ToString());
        if (previewChars != 200) Add("--preview-chars", previewChars.ToString());
        if (offset != 0) Add("--offset", offset.ToString());
        if (limit != 10) Add("--limit", limit.ToString());
        Add("--view-id", viewId);
        if (issueOffset != 0) Add("--issue-offset", issueOffset.ToString());
        if (issueLimit is not null) Add("--issue-limit", issueLimit.Value.ToString());
        if (all) argv.Add("--all");
        if (!delivery.Equals("auto", StringComparison.OrdinalIgnoreCase)) Add("--delivery", delivery);
        Add("--output", safeOutput);
        if (allowTruncated) argv.Add("--allow-truncated");
        if (json) argv.Add("--json");
        if (rawJson) argv.Add("--raw-json");
        return [.. argv];
    }

    /// <summary>Builds the CLI #154-style envelope for a timeline view: same metadata, "results" instead of "records" (matching other CLI list commands), plus cache provenance.</summary>
    private static Dictionary<string, object?> BuildCliEnvelope(TimelineProjectionResult view, HlxCacheProvenance cache)
    {
        var element = JsonSerializer.SerializeToElement(view);
        var envelope = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.NameEquals("records"))
                envelope["results"] = prop.Value;
            else
                envelope[prop.Name] = prop.Value;
        }
        envelope["cache"] = cache;
        return envelope;
    }

    /// <summary>Get log content for a specific build log.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="logId">Log ID from the timeline record's log reference.</param>
    /// <param name="tailLines">Number of lines from the end to return.</param>
    /// <param name="full">Fetch the complete log instead of the default tail.</param>
    /// <param name="json">Emit JSON error envelope on acquisition failure.</param>
    [McpEquivalent("azdo_log")]
    [Command("azdo log")]
    public async Task Log([Argument] string buildId, [Argument] int logId, int? tailLines = 500, bool full = false, bool json = false)
    {
        var content = await _svc.GetBuildLogAsync(buildId, logId, full ? null : tailLines);

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(content, s_jsonOptions));
        else
            Console.Write(content);
    }

    /// <summary>Search one build log or all ranked build logs for a pattern.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="pattern">Text pattern to search for (case-insensitive).</param>
    /// <param name="contextLines">Lines of context before and after each match.</param>
    /// <param name="maxMatches">Maximum number of matches to return (default 100).</param>
    /// <param name="logId">Log ID from the timeline record's log reference. Omit to search all ranked logs.</param>
    /// <param name="maxLogs">Maximum number of log steps to download and search (default 50).</param>
    /// <param name="minLines">Minimum line count to include a log in the search.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_search_log")]
    [Command("azdo search-log")]
    public async Task SearchLog([Argument] string buildId,
        string pattern = "error", int contextLines = 2, int maxMatches = 100,
        int? logId = null, int maxLogs = 50, int minLines = 5, bool json = false, bool schema = false)
    {
        if (logId.HasValue
            ? Commands.TryPrintSchema<LogSearchResult>(schema)
            : Commands.TryPrintSchema<CrossStepSearchResult>(schema))
        {
            return;
        }

        static void PrintMatches(IReadOnlyList<LogMatch> matches, int context)
        {
            foreach (var m in matches)
            {
                if (m.Context is { Count: > 0 })
                {
                    int currentLineNumber = Math.Max(1, m.LineNumber - context);
                    foreach (var line in m.Context)
                    {
                        var prefix = currentLineNumber == m.LineNumber ? ">>>" : "   ";
                        Console.WriteLine($"  {prefix} {currentLineNumber,6}: {line}");
                        currentLineNumber++;
                    }
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine($"  >>> {m.LineNumber,6}: {m.Line}");
                }
            }
        }

        if (logId.HasValue)
        {
            var result = await _svc.SearchBuildLogAsync(buildId, logId.Value, pattern, contextLines, maxMatches);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
                return;
            }

            Console.WriteLine($"Search: '{pattern}' in log {logId.Value} — {result.Matches.Count} match(es) in {result.TotalLines} lines{(result.Truncated ? " (truncated)" : "")}");
            if (result.Truncated)
                Console.WriteLine("  (stopped early — increase --max-matches to see more)");
            Console.WriteLine();
            PrintMatches(result.Matches, contextLines);
            return;
        }

        var crossStepResult = await _svc.SearchBuildLogAcrossStepsAsync(
            buildId, pattern, contextLines, maxMatches, maxLogs, minLines);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(crossStepResult, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Search: '{pattern}' across build — {crossStepResult.TotalMatchCount} match(es) in {crossStepResult.LogsSearched}/{crossStepResult.TotalLogsInBuild} logs ({crossStepResult.LogsSkipped} skipped)");
        if (crossStepResult.StoppedEarly)
            Console.WriteLine("  (stopped early — increase --max-matches or --max-logs to see more)");
        Console.WriteLine();

        if (crossStepResult.Steps.Count == 0)
        {
            Console.WriteLine("No matches found.");
            return;
        }

        foreach (var step in crossStepResult.Steps)
        {
            var resultStr = step.StepResult ?? "?";
            if (resultStr.Equals("failed", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Red;
            else if (resultStr.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Green;
            else
                Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  [{resultStr}]");
            Console.ResetColor();

            Console.Write($" {step.StepName} (log: {step.LogId}, {step.MatchCount} match(es))");

            if (step.ParentName is not null)
                Console.Write($" in {step.ParentName}");

            Console.WriteLine();
            PrintMatches(step.Matches, contextLines);
        }
    }

    /// <summary>Get the commits/changes associated with a build.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="top">Compatibility alias for --limit.</param>
    /// <param name="offset">Zero-based offset into the complete change list.</param>
    /// <param name="limit">Maximum number of changes to return.</param>
    /// <param name="all">Return the complete selected change list.</param>
    /// <param name="allowTruncated">Exit 0 even when the output is truncated.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_changes")]
    [Command("azdo changes")]
    public async Task Changes([Argument] string buildId, int? top = null, int offset = 0, int? limit = null,
        bool all = false, bool allowTruncated = false, bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<HlxListEnvelope<AzdoBuildChange>>(schema))
            return;

        if (!TryCreatePageRequest(all, offset, limit, top, out var page))
            return;

        var envelope = await _svc.GetBuildChangesPageAsync(buildId, page);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, s_jsonOptions));
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        var changes = envelope.Results;
        if (changes.Count == 0)
        {
            Console.WriteLine("No changes found.");
            PrintPagingWarning(envelope);
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        foreach (var c in changes)
        {
            var sha = c.Id?.Length > 8 ? c.Id[..8] : c.Id;
            var author = c.Author?.DisplayName ?? "?";
            var msg = c.Message?.Split('\n')[0] ?? "";
            Console.WriteLine($"  {sha}  {author}  {msg}");
        }
        PrintPagingWarning(envelope);
        ApplyTruncatedExit(envelope, allowTruncated);
    }

    /// <summary>List test runs for a build.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="top">Compatibility alias for --limit.</param>
    /// <param name="offset">Zero-based offset into the complete test-run list.</param>
    /// <param name="limit">Maximum number of test runs to return.</param>
    /// <param name="all">Return the complete selected test-run list.</param>
    /// <param name="allowTruncated">Exit 0 even when the output is truncated.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_test_runs")]
    [Command("azdo test-runs")]
    public async Task TestRuns([Argument] string buildId, int? top = null, int offset = 0, int? limit = null,
        bool all = false, bool allowTruncated = false, bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<HlxListEnvelope<AzdoTestRun>>(schema))
            return;

        if (!TryCreatePageRequest(all, offset, limit, top, out var page))
            return;

        var envelope = await _svc.GetTestRunsPageAsync(buildId, page);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, s_jsonOptions));
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        var runs = envelope.Results;
        if (runs.Count == 0)
        {
            Console.WriteLine("No test runs found.");
            PrintPagingWarning(envelope);
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        foreach (var r in runs)
        {
            Console.Write($"  Run #{r.Id}: {r.Name}  ");
            Console.Write($"Total: {r.TotalTests}  ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"Passed: {r.PassedTests}  ");
            if (r.FailedTests > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"Failed: {r.FailedTests}");
            }
            else
            {
                Console.Write($"Failed: {r.FailedTests}");
            }
            Console.ResetColor();
            if (r.IncompleteTests > 0)
                Console.Write($"  Incomplete: {r.IncompleteTests}");
            if (r.NotApplicableTests > 0)
                Console.Write($"  NotApplicable: {r.NotApplicableTests}");
            Console.WriteLine();
        }
        PrintPagingWarning(envelope);
        ApplyTruncatedExit(envelope, allowTruncated);
    }

    /// <summary>Get test results for a specific test run.</summary>
    /// <param name="buildId">AzDO build ID or URL — used to resolve org/project context.</param>
    /// <param name="runId">Test run ID from azdo-test-runs output.</param>
    /// <param name="top">Compatibility alias for --limit.</param>
    /// <param name="offset">Zero-based offset into the complete test-result list.</param>
    /// <param name="limit">Maximum number of test results to return.</param>
    /// <param name="all">Return the complete selected test-result list.</param>
    /// <param name="allowTruncated">Exit 0 even when the output is truncated.</param>
    /// <param name="outcomes">Comma-separated AzDO test outcomes to include (e.g. 'Failed', 'Passed,Failed'). Default: Failed.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_test_results")]
    [Command("azdo test-results")]
    public async Task TestResults([Argument] string buildId, [Argument] int runId,
        int? top = null, int offset = 0, int? limit = null, bool all = false,
        bool allowTruncated = false, string? outcomes = null, bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<HlxListEnvelope<AzdoTestResult>>(schema))
            return;

        if (!TryCreatePageRequest(all, offset, limit, top, out var page))
            return;

        var envelope = await _svc.GetTestResultsPageAsync(buildId, runId, page, outcomes);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, s_jsonOptions));
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        var results = envelope.Results;
        if (results.Count == 0)
        {
            Console.WriteLine("No test results found.");
            PrintPagingWarning(envelope);
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        foreach (var t in results)
        {
            var outcome = t.Outcome ?? "?";
            if (outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Red;
            else if (outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Green;
            else
                Console.ForegroundColor = ConsoleColor.Yellow;

            Console.Write($"  [{outcome}] ");
            Console.ResetColor();
            Console.Write(t.TestCaseTitle ?? t.AutomatedTestName ?? $"Result #{t.Id}");
            if (t.DurationInMs.HasValue)
                Console.Write($" ({t.DurationInMs.Value:F0}ms)");
            Console.WriteLine();

            if (t.ErrorMessage is not null)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"         {t.ErrorMessage}");
                Console.ResetColor();
            }
        }
        PrintPagingWarning(envelope);
        ApplyTruncatedExit(envelope, allowTruncated);
    }

    /// <summary>List artifacts produced by a build.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="pattern">Filter artifacts by name using glob-style matching.</param>
    /// <param name="top">Compatibility alias for --limit.</param>
    /// <param name="offset">Zero-based offset into the complete artifact list.</param>
    /// <param name="limit">Maximum number of artifacts to return.</param>
    /// <param name="all">Return the complete selected artifact list.</param>
    /// <param name="allowTruncated">Exit 0 even when the output is truncated.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_artifacts")]
    [Command("azdo artifacts")]
    public async Task Artifacts([Argument] string buildId, string pattern = "*",
        int? top = null, int offset = 0, int? limit = null, bool all = false,
        bool allowTruncated = false, bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<HlxListEnvelope<AzdoBuildArtifact>>(schema))
            return;

        if (!TryCreatePageRequest(all, offset, limit, top, out var page))
            return;

        var envelope = await _svc.GetBuildArtifactsPageAsync(buildId, pattern, page);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, s_jsonOptions));
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        var artifacts = envelope.Results;
        if (artifacts.Count == 0)
        {
            Console.WriteLine("No artifacts found.");
            PrintPagingWarning(envelope);
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        foreach (var a in artifacts)
        {
            Console.Write($"  {a.Name}");
            if (a.Resource?.Type is not null)
                Console.Write($" [{a.Resource.Type}]");
            Console.WriteLine();
        }
        PrintPagingWarning(envelope);
        ApplyTruncatedExit(envelope, allowTruncated);
    }

    /// <summary>Search build timeline for records matching a pattern.</summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="pattern">Text pattern to search for in record names and issue messages (case-insensitive).</param>
    /// <param name="type">Filter by record type: Stage, Job, or Task.</param>
    /// <param name="result">Result filter: 'failed' (default — includes non-succeeded records or records with timeline issues, same as 'azdo timeline') or 'all'.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_search_timeline")]
    [Command("azdo search-timeline")]
    public async Task SearchTimeline([Argument] string buildId, [Argument] string pattern,
        string? type = null, string result = "failed", bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<TimelineSearchResult>(schema))
            return;

        if (type is not null &&
            !type.Equals("Stage", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("Job", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("Task", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid type '{type}'. Must be 'Stage', 'Job', or 'Task'.", nameof(type));
        }

        if (!result.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !result.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid result '{result}'. Must be 'failed' or 'all'.", nameof(result));
        }

        var searchResult = await _svc.SearchTimelineAsync(buildId, pattern, type, result);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(searchResult, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Search: '{pattern}' in timeline — {searchResult.MatchCount} match(es) in {searchResult.TotalRecords} records");
        Console.WriteLine();

        if (searchResult.MatchCount == 0)
        {
            Console.WriteLine("No matching records found.");
            return;
        }

        foreach (var m in searchResult.Matches)
        {
            var resultStr = m.Result ?? m.State ?? "?";
            if (resultStr.Equals("failed", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Red;
            else if (resultStr.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                Console.ForegroundColor = ConsoleColor.Green;
            else
                Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  [{resultStr}]");
            Console.ResetColor();

            Console.Write($" {m.Name} ({m.Type})");

            if (m.Duration is not null)
                Console.Write($" {m.Duration}");

            if (m.LogId is not null)
                Console.Write($" (log: {m.LogId})");

            if (m.ParentName is not null)
                Console.Write($" in {m.ParentName}");

            Console.WriteLine();

            foreach (var issue in m.MatchedIssues)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    ↳ {issue}");
                Console.ResetColor();
            }
        }
    }


    /// <summary>List attachments for a specific test result.</summary>
    /// <param name="runId">Test run ID from azdo-test-runs output.</param>
    /// <param name="resultId">Test result ID from azdo-test-results output.</param>
    /// <param name="org">Azure DevOps organization (default: dnceng-public).</param>
    /// <param name="project">Azure DevOps project (default: public).</param>
    /// <param name="top">Compatibility alias for --limit.</param>
    /// <param name="offset">Zero-based offset into the complete attachment list.</param>
    /// <param name="limit">Maximum number of attachments to return.</param>
    /// <param name="all">Return the complete selected attachment list.</param>
    /// <param name="allowTruncated">Exit 0 even when the output is truncated.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_test_attachments")]
    [Command("azdo test-attachments")]
    public async Task TestAttachments([Argument] int runId, [Argument] int resultId,
        string org = "dnceng-public", string project = "public",
        int? top = null, int offset = 0, int? limit = null, bool all = false,
        bool allowTruncated = false, bool json = false, bool schema = false)
    {
        if (Commands.TryPrintSchema<HlxListEnvelope<AzdoTestAttachment>>(schema))
            return;

        if (!TryCreatePageRequest(all, offset, limit, top, out var page))
            return;

        var envelope = await _svc.GetTestAttachmentsPageAsync(org, project, runId, resultId, page);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(envelope, s_jsonOptions));
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        var attachments = envelope.Results;
        if (attachments.Count == 0)
        {
            Console.WriteLine("No attachments found.");
            PrintPagingWarning(envelope);
            ApplyTruncatedExit(envelope, allowTruncated);
            return;
        }

        foreach (var a in attachments)
        {
            Console.Write($"  {a.FileName}");
            if (a.Size > 0)
                Console.Write($" ({Commands.FormatBytes(a.Size)})");
            if (a.Comment is not null)
                Console.Write($" — {a.Comment}");
            Console.WriteLine();
        }
        PrintPagingWarning(envelope);
        ApplyTruncatedExit(envelope, allowTruncated);
    }

    private static string FormatExpirationStatus(bool? looksExpired)
        => looksExpired switch
        {
            true => "yes",
            false => "no",
            _ => "unknown"
        };

    private static string QuoteUntrusted(string? value)
    {
        if (value is null)
            return "(none)";

        // JSON string escaping keeps untrusted values on one line and neutralizes terminal controls.
        return JsonSerializer.Serialize(value);
    }

    private static string FormatInvariant<T>(T value) where T : IFormattable
        => value.ToString(null, CultureInfo.InvariantCulture);

    private static void PrintEvidencePlan(AzdoEvidencePlan plan)
    {
        Console.WriteLine($"Evidence plan for build #{FormatInvariant(plan.BuildId)}");
        Console.WriteLine($"  Build number:      {QuoteUntrusted(plan.Build.BuildNumber)}");
        Console.WriteLine($"  Definition:        {QuoteUntrusted(plan.Build.DefinitionName)} ({(plan.Build.DefinitionId.HasValue ? FormatInvariant(plan.Build.DefinitionId.Value) : "none")})");
        Console.WriteLine($"  Status/result:     {QuoteUntrusted(plan.Build.Status)} / {QuoteUntrusted(plan.Build.Result)}");
        Console.WriteLine($"  Source branch:     {QuoteUntrusted(plan.Build.SourceBranch)}");
        Console.WriteLine($"  Source version:    {QuoteUntrusted(plan.Build.SourceVersion)}");
        Console.WriteLine($"  Finish time:       {(plan.Build.FinishTime.HasValue ? plan.Build.FinishTime.Value.ToString("O", CultureInfo.InvariantCulture) : "none")}");
        Console.WriteLine($"  URL:               {QuoteUntrusted(plan.Build.WebUrl)}");
        Console.WriteLine($"  Organization:      {QuoteUntrusted(plan.Build.Org)}");
        Console.WriteLine($"  Project:           {QuoteUntrusted(plan.Build.Project)}");
        Console.WriteLine($"  Build incomplete:  {(plan.BuildIncomplete ? "yes" : "no")}");
        Console.WriteLine($"  Match strategy:    {QuoteUntrusted(plan.MatchStrategy)}");
        Console.WriteLine($"  Job results:       {string.Join(", ", plan.JobResultsFilter.Select(QuoteUntrusted))}");
        Console.WriteLine($"  Artifact pattern:  {QuoteUntrusted(plan.ArtifactPattern)}");
        Console.WriteLine($"  Artifact prefix:   {QuoteUntrusted(plan.ArtifactJobPrefix)}");
        Console.WriteLine($"  Strip attempts:    {(plan.StripAttemptPrefix ? "yes" : "no")}");
        Console.WriteLine($"  Entries:           {FormatInvariant(plan.Entries.Count)} of {FormatInvariant(plan.TotalEntries ?? plan.Entries.Count)}");
        Console.WriteLine($"  Complete:          {(plan.Complete ? "yes" : "no")}");
        Console.WriteLine($"  Truncated:         {(plan.Truncated ? "yes" : "no")}");

        if (plan.Build.PrNumber is not null)
        {
            Console.WriteLine($"  PR number:         {QuoteUntrusted(plan.Build.PrNumber)}");
            Console.WriteLine($"  PR source SHA:     {QuoteUntrusted(plan.Build.PrSourceSha)}");
            Console.WriteLine($"  PR source branch:  {QuoteUntrusted(plan.Build.PrSourceBranch)}");
            Console.WriteLine($"  PR provider:       {QuoteUntrusted(plan.Build.PrProviderId)}");
            Console.WriteLine($"  PR fork/draft:     {FormatNullableBoolean(plan.Build.PrIsFork)} / {FormatNullableBoolean(plan.Build.PrDraft)}");
        }

        foreach (var entry in plan.Entries)
        {
            Console.WriteLine();
            Console.WriteLine($"  [{QuoteUntrusted(entry.Status)}] Job {QuoteUntrusted(entry.JobName)} ({QuoteUntrusted(entry.JobId)})");
            Console.WriteLine($"    Result:           {QuoteUntrusted(entry.JobResult)}");
            Console.WriteLine($"    Order/attempt:    {(entry.JobOrder.HasValue ? FormatInvariant(entry.JobOrder.Value) : "none")} / {(entry.JobAttempt.HasValue ? FormatInvariant(entry.JobAttempt.Value) : "none")}");
            Console.WriteLine($"    Matched by:       {QuoteUntrusted(entry.MatchedBy)}");
            Console.WriteLine($"    Candidates:       {FormatInvariant(entry.Candidates.Count)} of {FormatInvariant(entry.CandidateTotal)}{(entry.CandidatesTruncated ? " (truncated)" : "")}");
            if (entry.CandidateNote is not null)
                Console.WriteLine($"    Candidate note:   {QuoteUntrusted(entry.CandidateNote)}");

            foreach (var candidate in entry.Candidates)
            {
                Console.WriteLine($"      {FormatInvariant(candidate.Rank)}. {QuoteUntrusted(candidate.ArtifactName)} (artifact #{FormatInvariant(candidate.ArtifactId)})");
                Console.WriteLine($"         Source:      {QuoteUntrusted(candidate.Source)}");
                Console.WriteLine($"         Attempt:     {(candidate.Attempt.HasValue ? FormatInvariant(candidate.Attempt.Value) : "none")}");
                Console.WriteLine($"         Type:        {QuoteUntrusted(candidate.ResourceType)}");
                Console.WriteLine($"         Size bytes:  {(candidate.SizeBytes.HasValue ? FormatInvariant(candidate.SizeBytes.Value) : "none")}");
                Console.WriteLine($"         Download:    {QuoteUntrusted(candidate.DownloadUrl)}");
            }
        }

        if (plan.HelixFailures.Count > 0)
        {
            Console.WriteLine();
            var helixFailureRange = plan.HelixFailures.Count == 0
                ? $"0 of {FormatInvariant(plan.HelixFailureTotal)}"
                : $"{FormatInvariant(plan.HelixFailureOffset + 1)}-{FormatInvariant(plan.HelixFailureOffset + plan.HelixFailures.Count)} of {FormatInvariant(plan.HelixFailureTotal)}";
            Console.WriteLine(
                $"Helix monitor failures: {helixFailureRange}{(plan.HelixFailuresTruncated ? " (truncated)" : "")}");
            foreach (var failure in plan.HelixFailures)
            {
                Console.WriteLine();
                Console.WriteLine($"  Work item:        {QuoteUntrusted(failure.WorkItem)}");
                Console.WriteLine($"    Helix job:      {QuoteUntrusted(failure.HelixJobId)}");
                if (failure.HelixJobName is not null)
                    Console.WriteLine($"    Helix job name: {QuoteUntrusted(failure.HelixJobName)}");
                if (failure.Leg is not null || failure.Queue is not null)
                    Console.WriteLine($"    Leg/queue:      {QuoteUntrusted(failure.Leg)} / {QuoteUntrusted(failure.Queue)}");
                Console.WriteLine($"    State/exit code: {QuoteUntrusted(failure.State)} / {(failure.ExitCode.HasValue ? FormatInvariant(failure.ExitCode.Value) : "none")}");
                Console.WriteLine($"    Monitor job/task: {QuoteUntrusted(failure.MonitorJobName)} ({QuoteUntrusted(failure.MonitorJobId)}) / {QuoteUntrusted(failure.MonitorTaskName)} ({QuoteUntrusted(failure.MonitorTaskId)})");
                Console.WriteLine($"    Source format:  {QuoteUntrusted(failure.SourceFormat)}");
            }
        }

        if (plan.IncompleteReasons.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Incomplete reasons:");
            if (plan.IncompleteDetails.Count > 0)
            {
                foreach (var detail in plan.IncompleteDetails)
                    Console.WriteLine($"  - [{detail.Code}] {QuoteUntrusted(detail.Message)}");
            }
            else
            {
                foreach (var reason in plan.IncompleteReasons)
                    Console.WriteLine($"  - {QuoteUntrusted(reason)}");
            }
        }

        if (plan.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(plan.WarningsTruncated
                ? $"Warnings (showing {FormatInvariant(plan.Warnings.Count)} of {FormatInvariant(plan.WarningTotal)}; truncated):"
                : "Warnings:");
            foreach (var warning in plan.Warnings)
                Console.WriteLine($"  - {QuoteUntrusted(warning)}");
        }

        if (plan.Note is not null)
            Console.WriteLine($"Note: {QuoteUntrusted(plan.Note)}");

        Console.WriteLine($"Generated at: {plan.GeneratedAt:O}");
    }

    private static string FormatNullableBoolean(bool? value)
        => value switch
        {
            true => "yes",
            false => "no",
            null => "unknown"
        };

    /// <summary>
    /// Plan CI evidence collection for a build: maps failed/canceled Job records to artifact candidates and
    /// surfaces Helix monitor work-item failures from timeline issues. Nothing is downloaded and no Helix API
    /// calls are made. Exits 0 when the plan is complete, 2 when incomplete (ambiguous/missing/truncated),
    /// 1 on hard errors.
    /// </summary>
    /// <param name="buildId">AzDO build ID (integer) or full AzDO build URL.</param>
    /// <param name="artifactPattern">Glob pattern for artifact names (e.g. 'Logs_Build_*'). Default: all.</param>
    /// <param name="artifactJobPrefix">Prefix stripped from artifact names before matching (e.g. 'Logs_Build_').</param>
    /// <param name="keepAttemptPrefix">Keep 'AttemptN_' in artifact names instead of stripping it. Default: strip and record the attempt number.</param>
    /// <param name="match">Matching strategy: 'auto' (default), 'source-id', 'normalized-exact', 'exact'.</param>
    /// <param name="jobResults">Comma-separated job results to target. Default: 'failed,canceled'.</param>
    /// <param name="helixFailureOffset">Offset into parsed Helix monitor failures. Default: 0.</param>
    /// <param name="helixFailureLimit">Maximum parsed Helix monitor failures to return. Default: 200, max: 1000.</param>
    /// <param name="json">Output as structured JSON.</param>
    [McpEquivalent("azdo_evidence_plan")]
    [Command("azdo evidence plan")]
    public async Task EvidencePlan(
        [Argument] string buildId,
        string artifactPattern = "*",
        string? artifactJobPrefix = null,
        bool keepAttemptPrefix = false,
        string match = "auto",
        string jobResults = "failed,canceled",
        int helixFailureOffset = 0,
        int helixFailureLimit = AzdoEvidencePlan.DefaultHelixFailureLimit,
        bool json = false,
        bool schema = false)
    {
        if (Commands.TryPrintSchema<AzdoEvidencePlan>(schema))
            return;

        // Validate match
        if (!AzdoEvidenceMatchStrategy.AllValues.Contains(match, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"Invalid --match {QuoteUntrusted(match)}. Must be one of: {string.Join(", ", AzdoEvidenceMatchStrategy.AllValues)}.");
            Environment.ExitCode = 1;
            return;
        }

        // Parse and validate jobResults
        var resultList = jobResults
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();
        if (resultList.Count == 0)
            resultList = ["failed", "canceled"];

        var validResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "failed", "canceled", "abandoned", "skipped", "succeededWithIssues", "succeeded", "none" };
        var bad = resultList.FirstOrDefault(r => !validResults.Contains(r));
        if (bad is not null)
        {
            Console.Error.WriteLine(
                $"Invalid --job-results value {QuoteUntrusted(bad)}. Must be one of: {string.Join(", ", validResults.OrderBy(v => v, StringComparer.Ordinal))}.");
            Environment.ExitCode = 1;
            return;
        }

        var options = new AzdoEvidencePlanOptions
        {
            ArtifactPattern = artifactPattern,
            ArtifactJobPrefix = artifactJobPrefix,
            StripAttemptPrefix = !keepAttemptPrefix,
            Match = match,
            JobResults = resultList,
            HelixFailureOffset = helixFailureOffset,
            HelixFailureLimit = helixFailureLimit
        };

        AzdoEvidencePlan plan;
        try
        {
            plan = await _svc.GetEvidencePlanAsync(buildId, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HlxAcquisitionException)
        {
            Console.Error.WriteLine($"Error: {QuoteUntrusted(ex.Message)}");
            Environment.ExitCode = 1;
            return;
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(plan, s_jsonOptions));
        else
            PrintEvidencePlan(plan);

        // Exit 2 for incomplete plans (ambiguous/missing/truncated); 0 for complete
        if (!plan.Complete)
            Environment.ExitCode = 2;
    }

}
