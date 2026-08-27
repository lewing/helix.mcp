// Group E — CLI + MCP surface contract tests.
//
// Run: `dotnet test --filter "FullyQualifiedName~AzdoEvidenceSurfaceTests"`

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Mcp.Tools;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

[CollectionDefinition("AzdoEvidenceConsole", DisableParallelization = true)]
public sealed class AzdoEvidenceConsoleCollection;

/// <summary>
/// E-group surface tests: CLI route registration, MCP tool attributes, exit codes,
/// incomplete-plan handling, cancellation, and no-download / no-write assertions.
/// </summary>
[Collection("AzdoEvidenceConsole")]
public class AzdoEvidenceSurfaceTests
{
    private readonly IAzdoApiClient _mockApi;
    private readonly AzdoService _svc;
    private readonly AzdoMcpTools _tools;

    public AzdoEvidenceSurfaceTests()
    {
        _mockApi = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_mockApi);
        _tools = new AzdoMcpTools(_svc, Substitute.For<IAzdoTokenAccessor>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // E1 — CommandRegistry contains "azdo evidence plan"
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CommandRegistry_ContainsAzdoEvidencePlanRoute()
    {
        var command = CommandRegistry.Get("azdo evidence plan");
        Assert.NotNull(command);
        Assert.Equal("azdo evidence plan", command!.Route);
        Assert.Equal("AzDO", command.Category);
    }

    [Fact]
    public void CommandRegistry_AzdoEvidencePlan_HasDescription()
    {
        var command = CommandRegistry.Get("azdo evidence plan");
        Assert.NotNull(command);
        Assert.False(string.IsNullOrWhiteSpace(command!.Description));
    }

    [Fact]
    public void CommandRegistry_AzdoEvidencePlan_HasExpectedParameters()
    {
        var command = CommandRegistry.Get("azdo evidence plan");
        Assert.NotNull(command);
        Assert.Collection(
            command!.Parameters,
            parameter => AssertCliParameter(parameter, "buildId", "String", null, isPositional: true),
            parameter => AssertCliParameter(parameter, "artifactPattern", "String", "*", isPositional: false),
            parameter => AssertCliParameter(parameter, "artifactJobPrefix", "String", "null", isPositional: false),
            parameter => AssertCliParameter(parameter, "keepAttemptPrefix", "Boolean", "false", isPositional: false),
            parameter => AssertCliParameter(parameter, "match", "String", "auto", isPositional: false),
            parameter => AssertCliParameter(parameter, "jobResults", "String", "failed,canceled", isPositional: false),
            parameter => AssertCliParameter(parameter, "json", "Boolean", "false", isPositional: false),
            parameter => AssertCliParameter(parameter, "schema", "Boolean", "false", isPositional: false));
    }

    // ════════════════════════════════════════════════════════════════════════
    // E2 — [McpEquivalent("azdo_evidence_plan")] declared and resolves
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CommandRegistry_AzdoEvidencePlan_McpToolNameMatches()
    {
        var command = CommandRegistry.Get("azdo evidence plan");
        Assert.NotNull(command);
        Assert.Equal("azdo_evidence_plan", command!.McpToolName);
    }

    [Fact]
    public void McpTool_AzdoEvidencePlan_Registered()
    {
        var mcpTool = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(mcpTool);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3 — Exit-code contract: 0=complete, 2=incomplete (full plan on stdout), 1=error
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvidencePlan_CompletePlan_ExitCode0()
    {
        // D8: exit 0 when plan.Complete == true.
        // Tests the service layer; CLI sets Environment.ExitCode based on plan.Complete.
        SetupCompleteMockPlan();

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.True(plan.Complete);
        // The exit-code assignment is in the CLI command; verify the plan is complete
        // so the CLI would set exit 0.
    }

    [Fact]
    public async Task EvidencePlan_IncompletePlan_ProducesUsableOutput()
    {
        // Service-level shape check. The exit-code half of D8 is gated by the real CLI
        // subprocess test CliEvidencePlan_IncompletePlan_ExitsTwoWithUsableJsonPlanOnStdout.
        SetupIncompleteMockPlan();

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.False(plan.Complete);
        Assert.NotEmpty(plan.IncompleteReasons);
        // The plan still has entries — the output is usable.
        Assert.NotEmpty(plan.Entries);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E4 — MCP returns complete:false without throwing for incomplete plan
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task McpTool_EvidencePlan_IncompletePlan_DoesNotThrow()
    {
        // D8: MCP tool must never throw McpException for incomplete plans.
        // McpException is reserved for invalid arguments and not-found (per mcp-structured-content).
        SetupIncompleteMockPlan();

        var plan = await _tools.EvidencePlan(
            "1570501",
            artifactPattern: "Logs_Build_*",
            artifactJobPrefix: "Logs_Build_",
            stripAttemptPrefix: true,
            match: "auto",
            jobResults: "failed,canceled");

        Assert.False(plan.Complete);
        Assert.NotNull(plan.IncompleteReasons);
        // If MCP layer wraps service, it must return plan.Complete = false rather than throw.
    }

    // ════════════════════════════════════════════════════════════════════════
    // E5 — Invalid arguments → McpException (MCP) and hard error (CLI)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("bogus-mode")]
    [InlineData("prefix")]         // explicitly rejected match mode
    [InlineData("contains")]       // explicitly rejected match mode
    [InlineData("fuzzy")]          // explicitly rejected match mode
    public async Task GetEvidencePlanAsync_InvalidMatchMode_Throws(string badMatch)
    {
        // D1/D3: unknown --match value → hard error.
        var opts = DefaultOptions() with { Match = badMatch };

        // Expect McpException or ArgumentException depending on layer.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _svc.GetEvidencePlanAsync("1570501", opts));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus-result")]
    [InlineData("completed,bogus")]
    public async Task GetEvidencePlanAsync_InvalidJobResult_Throws(string badResult)
    {
        // D3: unknown job-result value → hard error listing valid values.
        var jobResults = badResult.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var opts = new AzdoEvidencePlanOptions { JobResults = jobResults, Match = "auto" };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _svc.GetEvidencePlanAsync("1570501", opts));
    }

    // ════════════════════════════════════════════════════════════════════════
    // E6 — MCP tool attributes: ReadOnly, Idempotent, UseStructuredContent
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void McpTool_AzdoEvidencePlan_IsReadOnly()
    {
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var attr = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.True(attr!.ReadOnly, "azdo_evidence_plan must be ReadOnly = true");
    }

    [Fact]
    public void McpTool_AzdoEvidencePlan_IsIdempotent()
    {
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var attr = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.True(attr!.Idempotent, "azdo_evidence_plan must be Idempotent = true");
    }

    [Fact]
    public void McpTool_AzdoEvidencePlan_UsesStructuredContent()
    {
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var attr = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.True(attr!.UseStructuredContent, "azdo_evidence_plan must be UseStructuredContent = true");
    }

    [Fact]
    public void McpTool_AzdoEvidencePlan_HasTitle()
    {
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var attr = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("AzDO Evidence Plan", attr!.Title);
    }

    [Fact]
    public void McpTool_AzdoEvidencePlan_HasDescription()
    {
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var desc = method!.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.False(string.IsNullOrWhiteSpace(desc));
    }

    // ════════════════════════════════════════════════════════════════════════
    // E7 — No IProgress parameter (D7: single-shot fetch, not long-running)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void McpTool_AzdoEvidencePlan_HasNoIProgressParameter()
    {
        // D7: "No IProgress parameter." Three cached GETs is single-shot.
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var hasProgress = method!.GetParameters()
            .Any(p => p.ParameterType.IsGenericType &&
                      p.ParameterType.GetGenericTypeDefinition() == typeof(IProgress<>));

        Assert.False(hasProgress, "azdo_evidence_plan must not have an IProgress<> parameter (D7)");
    }

    [Fact]
    public void McpTool_AzdoEvidencePlan_AllParametersHaveDescriptions()
    {
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var missing = method!.GetParameters()
            .Where(p => p.GetCustomAttribute<DescriptionAttribute>() == null
                        && !p.IsOptional)  // cancellation tokens, etc. may be framework-injected
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void McpTool_AzdoEvidencePlan_ParametersAndInputSchemaAreExact()
    {
        var method = GetMcpToolMethod("azdo_evidence_plan");
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Collection(
            parameters,
            parameter => AssertMcpParameter(
                parameter,
                "buildIdOrUrl",
                typeof(string),
                hasDefault: false,
                defaultValue: null,
                "AzDO build ID as a JSON string (for example, '1570501') or full Azure DevOps build URL; not a Helix job ID"),
            parameter => AssertMcpParameter(
                parameter,
                "artifactPattern",
                typeof(string),
                hasDefault: true,
                defaultValue: "*",
                "Glob pattern for artifact names to include. Supports '*' (all), '*.ext' (suffix), 'Prefix*' (prefix), or substring. Default: '*'"),
            parameter => AssertMcpParameter(
                parameter,
                "artifactJobPrefix",
                typeof(string),
                hasDefault: true,
                defaultValue: null,
                "Prefix stripped from artifact names before matching (e.g. 'Logs_Build_'). Ordinal StartsWith; no regex."),
            parameter => AssertMcpParameter(
                parameter,
                "stripAttemptPrefix",
                typeof(bool),
                hasDefault: true,
                defaultValue: true,
                "Strip 'AttemptN_' from artifact names after the job prefix, recording the attempt number. Default: true"),
            parameter => AssertMcpParameter(
                parameter,
                "match",
                typeof(string),
                hasDefault: true,
                defaultValue: "auto",
                "Matching strategy: 'auto' (default) = source-id join then normalized-name fallback; 'source-id' = GUID join only; 'normalized-exact' = PR #132609 name parity; 'exact' = ordinal-ignore-case equality after prefix strip, no normalization."),
            parameter => AssertMcpParameter(
                parameter,
                "jobResults",
                typeof(string),
                hasDefault: true,
                defaultValue: "failed,canceled",
                "Comma-separated job results to include (e.g. 'failed', 'failed,canceled', 'succeeded,succeededWithIssues'). Any combination of: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none. Unknown values are rejected. Default: 'failed,canceled'."));

        Assert.Equal(
            new object[] { "auto", "source-id", "normalized-exact", "exact" },
            parameters[4].GetCustomAttribute<AllowedValuesAttribute>()!.Values);
        Assert.Null(parameters[5].GetCustomAttribute<AllowedValuesAttribute>());

        var tool = McpServerTool.Create(method, _tools, options: null).ProtocolTool;
        var schema = tool.InputSchema;
        var properties = schema.GetProperty("properties");
        Assert.Equal(
            parameters.Select(parameter => parameter.Name),
            properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["buildIdOrUrl"],
            schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        AssertSchemaProperty(properties, "buildIdOrUrl", ["string"], defaultValue: null, hasDefault: false);
        AssertSchemaProperty(properties, "artifactPattern", ["string"], "*", hasDefault: true);
        AssertSchemaProperty(properties, "artifactJobPrefix", ["string", "null"], defaultValue: null, hasDefault: true);
        AssertSchemaProperty(properties, "stripAttemptPrefix", ["boolean"], true, hasDefault: true);
        AssertSchemaProperty(properties, "match", ["string"], "auto", hasDefault: true);
        AssertSchemaProperty(properties, "jobResults", ["string"], "failed,canceled", hasDefault: true);
        Assert.Equal(
            ["auto", "source-id", "normalized-exact", "exact"],
            properties.GetProperty("match").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.False(properties.GetProperty("jobResults").TryGetProperty("enum", out _));
    }

    [Fact]
    public async Task McpTool_AzdoEvidencePlan_JobResultsAcceptsDocumentedCommaSeparatedCombination()
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 1570501, Status = "completed", Result = "failed" });
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new() { Id = "job-failed", Type = "Job", Result = "failed", Name = "Failed job", Order = 1 },
                    new() { Id = "job-canceled", Type = "Job", Result = "canceled", Name = "Canceled job", Order = 2 },
                    new() { Id = "job-succeeded", Type = "Job", Result = "succeeded", Name = "Succeeded job", Order = 3 }
                ]
            });
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns([]);

        var plan = await _tools.EvidencePlan(
            "1570501",
            artifactPattern: "*",
            artifactJobPrefix: null,
            stripAttemptPrefix: true,
            match: "auto",
            jobResults: "failed, canceled");

        Assert.Equal(["failed", "canceled"], plan.JobResultsFilter);
        Assert.Equal(["job-failed", "job-canceled"], plan.Entries.Select(entry => entry.JobId));
    }

    [Fact]
    public async Task CliEvidencePlan_KeepAttemptPrefixFlag_ReachesSerializedPlan()
    {
        const int buildId = 1570501;
        const string jobId = "job-live-cli";
        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            using (var store = new SqliteCacheStore(new CacheOptions
                   {
                       CacheRoot = cacheRoot,
                       AuthTokenHash = null
                   }))
            {
                var build = new AzdoBuild
                {
                    Id = buildId,
                    BuildNumber = "live-cli",
                    Status = "completed",
                    Result = "failed"
                };
                var timeline = new AzdoTimeline
                {
                    Records =
                    [
                        new()
                        {
                            Id = jobId,
                            Type = "Job",
                            Result = "failed",
                            Name = "Job",
                            Order = 1,
                            Attempt = 2
                        }
                    ]
                };
                IReadOnlyList<AzdoBuildArtifact> artifacts =
                [
                    new()
                    {
                        Id = 1,
                        Name = "Logs_Build_Attempt2_Job",
                        Source = jobId,
                        Resource = new() { Type = "Container" }
                    }
                ];

                await store.SetMetadataAsync(
                    $"azdo:dnceng-public:public:build:{buildId}",
                    JsonSerializer.Serialize(build),
                    TimeSpan.FromHours(1));
                await store.SetMetadataAsync(
                    $"azdo:dnceng-public:public:timeline:{buildId}",
                    JsonSerializer.Serialize(timeline),
                    TimeSpan.FromHours(1));
                await store.SetMetadataAsync(
                    $"azdo:dnceng-public:public:artifacts:{buildId}",
                    JsonSerializer.Serialize(artifacts),
                    TimeSpan.FromHours(1));
            }

            var result = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_",
                "--keep-attempt-prefix",
                "--json");

            Assert.True(
                result.ExitCode == 0,
                $"CLI exited {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);

            using var json = JsonDocument.Parse(result.Stdout);
            var root = json.RootElement;
            Assert.False(root.GetProperty("stripAttemptPrefix").GetBoolean());
            Assert.True(root.GetProperty("complete").GetBoolean());
            var candidate = root.GetProperty("entries")[0].GetProperty("candidates")[0];
            Assert.Equal("Logs_Build_Attempt2_Job", candidate.GetProperty("artifactName").GetString());
            Assert.False(candidate.TryGetProperty("attempt", out _));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CliEvidencePlan_JsonFlagSelectsStructuredJsonAndTextEscapesControls()
    {
        const string buildNumber = "BUILD\r\n\u001b[31m";
        const string jobName = "Job\r\nName\u0007\u001b[31m";
        const string artifactName = "Logs_Build_Attempt1_Job\r\nName\u0007\u001b[31m";

        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 1570501,
                BuildNumber = buildNumber,
                Status = "completed",
                Result = "failed"
            });
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new()
                    {
                        Id = "job-control",
                        Type = "Job",
                        Result = "failed",
                        Name = jobName,
                        Order = 1,
                        Attempt = 1
                    }
                ]
            });
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(
            [
                new()
                {
                    Id = 1,
                    Name = artifactName,
                    Source = "job-control",
                    Resource = new() { Type = "Container" }
                }
            ]);

        var commands = new AzdoCommands(_svc, Substitute.For<IAzdoTokenAccessor>());
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var jsonOutput = await CaptureStdoutAsync(() => commands.EvidencePlan(
                "1570501", "Logs_Build_*", "Logs_Build_", true, "auto", "failed,canceled", json: true));
            Environment.ExitCode = 0;
            var textOutput = await CaptureStdoutAsync(() => commands.EvidencePlan(
                "1570501", "Logs_Build_*", "Logs_Build_", true, "auto", "failed,canceled", json: false));

            using var json = JsonDocument.Parse(jsonOutput);
            Assert.Equal(buildNumber, json.RootElement.GetProperty("build").GetProperty("buildNumber").GetString());
            Assert.Equal(jobName, json.RootElement.GetProperty("entries")[0].GetProperty("jobName").GetString());

            Assert.StartsWith("Evidence plan for build #1570501", textOutput, StringComparison.Ordinal);
            Assert.NotEqual(jsonOutput, textOutput);
            Assert.Contains("\"BUILD\\r\\n\\u001B[31m\"", textOutput, StringComparison.Ordinal);
            Assert.Contains("\"Job\\r\\nName\\u0007\\u001B[31m\"", textOutput, StringComparison.Ordinal);
            Assert.Contains("\"Logs_Build_Attempt1_Job\\r\\nName\\u0007\\u001B[31m\"", textOutput, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', textOutput);
            Assert.DoesNotContain('\u0007', textOutput);
            Assert.DoesNotContain('\u001b', textOutput);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task CliEvidencePlan_TextErrorEscapesNewlinesAndTerminalControls()
    {
        const string badMatch = "bad\r\n\u0007\u001b[31m";
        var commands = new AzdoCommands(_svc, Substitute.For<IAzdoTokenAccessor>());
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var error = await CaptureStderrAsync(() => commands.EvidencePlan(
                "1570501", match: badMatch, json: false));

            Assert.Equal(1, Environment.ExitCode);
            Assert.Contains("\"bad\\r\\n\\u0007\\u001B[31m\"", error, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', error);
            Assert.DoesNotContain('\u0007', error);
            Assert.DoesNotContain('\u001b', error);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3b — Real CLI subprocess: incomplete plan exits 2 with a usable stdout plan
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CliEvidencePlan_IncompletePlan_ExitsTwoWithUsableJsonPlanOnStdout()
    {
        // D8: exit 2 is not a fatal error — the full, usable plan is still written to stdout.
        // Runs the real built CLI as a subprocess against an offline snapshot: no network,
        // no credentials, deterministic input.
        const int buildId = 1570502;
        const string mappedJobId = "job-mapped";
        const string missingJobId = "job-missing";
        const string ambiguousJobId = "job-ambiguous";

        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-incomplete-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            await SeedEvidenceSnapshotAsync(
                cacheRoot,
                buildId,
                new AzdoBuild
                {
                    Id = buildId,
                    BuildNumber = "incomplete-cli",
                    Status = "completed",
                    Result = "failed",
                    Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" }
                },
                new AzdoTimeline
                {
                    Records =
                    [
                        new() { Id = mappedJobId,    Type = "Job", Result = "failed", Name = "Mapped Job",    Order = 1, Attempt = 1 },
                        new() { Id = missingJobId,   Type = "Job", Result = "failed", Name = "Missing Job",   Order = 2, Attempt = 1 },
                        new() { Id = ambiguousJobId, Type = "Job", Result = "failed", Name = "Ambiguous Job", Order = 3, Attempt = 1 },
                    ]
                },
                [
                    new() { Id = 1, Name = "Logs_Build_Attempt1_Mapped_Job",      Source = mappedJobId,    Resource = new() { Type = "Container" } },
                    new() { Id = 2, Name = "Logs_Build_Attempt1_Ambiguous_Job",   Source = ambiguousJobId, Resource = new() { Type = "Container" } },
                    new() { Id = 3, Name = "Logs_Build_Attempt2_Ambiguous_Job",   Source = ambiguousJobId, Resource = new() { Type = "Container" } },
                ]);

            var result = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-pattern", "Logs_Build_*",
                "--artifact-job-prefix", "Logs_Build_",
                "--json");

            Assert.True(
                result.ExitCode == 2,
                $"Expected exit 2 for an incomplete plan, got {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);

            // Exit 2 must still carry a parseable, usable plan on stdout.
            using var json = JsonDocument.Parse(result.Stdout);
            var root = json.RootElement;

            Assert.Equal(buildId, root.GetProperty("buildId").GetInt32());
            Assert.False(root.GetProperty("complete").GetBoolean());
            Assert.False(root.GetProperty("truncated").GetBoolean());

            var entries = root.GetProperty("entries");
            Assert.Equal(3, entries.GetArrayLength());

            var byJobId = entries
                .EnumerateArray()
                .ToDictionary(e => e.GetProperty("jobId").GetString()!, e => e);

            var mapped = byJobId[mappedJobId];
            Assert.Equal("mapped", mapped.GetProperty("status").GetString());
            Assert.Equal("source-id", mapped.GetProperty("matchedBy").GetString());
            Assert.Equal(
                "Logs_Build_Attempt1_Mapped_Job",
                mapped.GetProperty("candidates")[0].GetProperty("artifactName").GetString());

            var missing = byJobId[missingJobId];
            Assert.Equal("missing", missing.GetProperty("status").GetString());
            Assert.Equal(0, missing.GetProperty("candidates").GetArrayLength());
            Assert.False(missing.TryGetProperty("matchedBy", out _));

            var ambiguous = byJobId[ambiguousJobId];
            Assert.Equal("ambiguous", ambiguous.GetProperty("status").GetString());
            Assert.Equal(2, ambiguous.GetProperty("candidateTotal").GetInt32());
            Assert.Equal(2, ambiguous.GetProperty("candidates").GetArrayLength());
            Assert.False(ambiguous.GetProperty("candidatesTruncated").GetBoolean());

            // Diagnostics naming the actual jobs — the plan is actionable, not a bare failure.
            var reasons = root.GetProperty("incompleteReasons")
                .EnumerateArray()
                .Select(r => r.GetString()!)
                .ToList();
            Assert.Equal(2, reasons.Count);
            Assert.Contains(reasons, r => r.Contains("Missing Job", StringComparison.Ordinal));
            Assert.Contains(reasons, r => r.Contains("Ambiguous Job", StringComparison.Ordinal));
        }
        finally
        {
            CleanupSnapshot(cacheRoot);
        }
    }

    [Fact]
    public async Task CliEvidencePlan_IncompletePlan_HumanOutput_ExitsTwoWithUsablePlanOnStdout()
    {
        // The default (non---json) CLI path must also emit a usable plan on stdout with exit 2.
        const int buildId = 1570503;
        const string missingJobId = "job-missing";

        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-incomplete-text-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            await SeedEvidenceSnapshotAsync(
                cacheRoot,
                buildId,
                new AzdoBuild
                {
                    Id = buildId,
                    BuildNumber = "incomplete-cli-text",
                    Status = "completed",
                    Result = "failed",
                    Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" }
                },
                new AzdoTimeline
                {
                    Records =
                    [
                        new() { Id = missingJobId, Type = "Job", Result = "failed", Name = "Missing Job", Order = 1, Attempt = 1 }
                    ]
                },
                []);

            var result = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_");

            Assert.True(
                result.ExitCode == 2,
                $"Expected exit 2 for an incomplete plan, got {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);

            var stdout = result.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.StartsWith($"Evidence plan for build #{buildId}", stdout, StringComparison.Ordinal);
            Assert.Contains("Complete:          no", stdout, StringComparison.Ordinal);
            Assert.Contains("Truncated:         no", stdout, StringComparison.Ordinal);
            Assert.Contains("[\"missing\"] Job \"Missing Job\"", stdout, StringComparison.Ordinal);
            Assert.Contains("Incomplete reasons:", stdout, StringComparison.Ordinal);
            Assert.Contains("Missing Job", stdout, StringComparison.Ordinal);

            // The warnings block is conditional: a completed build with no truncation has
            // nothing to warn about, so the section must be absent rather than empty.
            Assert.DoesNotContain("Warnings:", stdout, StringComparison.Ordinal);
        }
        finally
        {
            CleanupSnapshot(cacheRoot);
        }
    }

    [Fact]
    public async Task CliEvidencePlan_IncompleteBuild_HumanOutput_RendersWarningsSection()
    {
        // Warnings are serialized for --json consumers, but the default human path renders them
        // in its own section. A non-completed build produces the point-in-time warning while the
        // mapping itself is complete, so this also proves warnings are non-fatal: exit stays 0.
        const int buildId = 1570506;
        const string jobId = "job-warn";

        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-warn-text-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            await SeedEvidenceSnapshotAsync(
                cacheRoot,
                buildId,
                new AzdoBuild
                {
                    Id = buildId,
                    BuildNumber = "warn-cli-text",
                    Status = "inProgress",
                    Result = "failed",
                    Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" }
                },
                new AzdoTimeline
                {
                    Records =
                    [
                        new() { Id = jobId, Type = "Job", Result = "failed", Name = "Warned Job", Order = 1, Attempt = 1 }
                    ]
                },
                [
                    new() { Id = 1, Name = "Logs_Build_Attempt1_Warned_Job", Source = jobId, Resource = new() { Type = "Container" } }
                ]);

            var result = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_");

            Assert.True(
                result.ExitCode == 0,
                $"Expected exit 0 for a complete plan with warnings, got {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);

            var stdout = result.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("Complete:          yes", stdout, StringComparison.Ordinal);
            Assert.Contains("Build incomplete:  yes", stdout, StringComparison.Ordinal);

            // Untruncated warnings use the bare header, and the warning text itself is rendered.
            Assert.Contains("\nWarnings:\n", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("truncated):", stdout, StringComparison.Ordinal);
            Assert.Contains(
                "  - \"Build is not completed; this evidence plan is a point-in-time snapshot and may change.\"",
                stdout,
                StringComparison.Ordinal);
        }
        finally
        {
            CleanupSnapshot(cacheRoot);
        }
    }

    [Fact]
    public async Task CliEvidencePlan_CompletePlan_ExitsZero_ProvingExitTwoIsNotUnconditional()
    {
        // Falsifies the exit-2 gate: the same CLI path over a fully-mapped snapshot exits 0.
        const int buildId = 1570504;
        const string jobId = "job-complete";

        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-complete-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            await SeedEvidenceSnapshotAsync(
                cacheRoot,
                buildId,
                new AzdoBuild { Id = buildId, BuildNumber = "complete-cli", Status = "completed", Result = "failed" },
                new AzdoTimeline
                {
                    Records =
                    [
                        new() { Id = jobId, Type = "Job", Result = "failed", Name = "Mapped Job", Order = 1, Attempt = 1 }
                    ]
                },
                [
                    new() { Id = 1, Name = "Logs_Build_Attempt1_Mapped_Job", Source = jobId, Resource = new() { Type = "Container" } }
                ]);

            var result = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_",
                "--json");

            Assert.True(
                result.ExitCode == 0,
                $"Expected exit 0 for a complete plan, got {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");

            using var json = JsonDocument.Parse(result.Stdout);
            Assert.True(json.RootElement.GetProperty("complete").GetBoolean());
        }
        finally
        {
            CleanupSnapshot(cacheRoot);
        }
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    [InlineData("NORMALIZED-EXACT")]
    [InlineData("Exact")]
    public async Task CliEvidencePlan_UppercaseMatchStrategy_CanonicalizedEndToEnd(string match)
    {
        // A mixed-case --match must be accepted, canonicalized in the serialized plan, and must
        // not regress every entry to "missing" (which would flip the exit code to 2).
        const int buildId = 1570505;
        const string jobId = "job-case";

        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-case-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            await SeedEvidenceSnapshotAsync(
                cacheRoot,
                buildId,
                new AzdoBuild { Id = buildId, BuildNumber = "case-cli", Status = "completed", Result = "failed" },
                new AzdoTimeline
                {
                    Records =
                    [
                        new() { Id = jobId, Type = "Job", Result = "failed", Name = "Mapped Job", Order = 1, Attempt = 1 }
                    ]
                },
                [
                    new() { Id = 1, Name = "Logs_Build_Attempt1_Mapped Job", Source = jobId, Resource = new() { Type = "Container" } }
                ]);

            var result = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_",
                "--match", match,
                "--json");

            Assert.True(
                result.ExitCode == 0,
                $"Expected exit 0 for --match {match}, got {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");

            using var json = JsonDocument.Parse(result.Stdout);
            var root = json.RootElement;

            Assert.Equal(match.ToLowerInvariant(), root.GetProperty("matchStrategy").GetString());
            Assert.True(root.GetProperty("complete").GetBoolean());
            Assert.Equal("mapped", root.GetProperty("entries")[0].GetProperty("status").GetString());
        }
        finally
        {
            CleanupSnapshot(cacheRoot);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3c — `truncated` is always serialized, both false and true
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvidencePlan_UntruncatedPlan_SerializesTruncatedAsFalse()
    {
        SetupCompleteMockPlan();

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.False(plan.Truncated);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        Assert.True(
            json.RootElement.TryGetProperty("truncated", out var truncated),
            "`truncated` must always be serialized, including when false.");
        Assert.Equal(JsonValueKind.False, truncated.ValueKind);
        Assert.False(truncated.GetBoolean());

        // `totalEntries` stays absent when nothing was truncated.
        Assert.False(json.RootElement.TryGetProperty("totalEntries", out _));
    }

    [Fact]
    public async Task EvidencePlan_TruncatedPlan_SerializesTruncatedAsTrue()
    {
        // MaxPlanEntries + 5 failed jobs → entry-list truncation.
        const int jobCount = AzdoEvidenceMatcher.MaxPlanEntries + 5;

        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 1570501, Status = "completed", Result = "failed" });
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records = Enumerable.Range(0, jobCount)
                    .Select(i => new AzdoTimelineRecord
                    {
                        Id = $"job-{i:D4}",
                        Type = "Job",
                        Result = "failed",
                        Name = $"Job {i:D4}",
                        Order = i,
                        Attempt = 1
                    })
                    .ToList()
            });
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.True(plan.Truncated);
        Assert.Equal(AzdoEvidenceMatcher.MaxPlanEntries, plan.Entries.Count);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        var root = json.RootElement;
        Assert.True(root.TryGetProperty("truncated", out var truncated));
        Assert.Equal(JsonValueKind.True, truncated.ValueKind);
        Assert.True(truncated.GetBoolean());
        Assert.Equal(jobCount, root.GetProperty("totalEntries").GetInt32());
        Assert.False(root.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task CliEvidencePlan_UntruncatedPlan_EmitsTruncatedFalseOverTheRealCli()
    {
        // End-to-end: the serialized CLI payload must carry `"truncated": false`, not omit it.
        const int buildId = 1570506;
        const string jobId = "job-untruncated";

        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-truncated-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            await SeedEvidenceSnapshotAsync(
                cacheRoot,
                buildId,
                new AzdoBuild { Id = buildId, BuildNumber = "truncation-cli", Status = "completed", Result = "failed" },
                new AzdoTimeline
                {
                    Records =
                    [
                        new() { Id = jobId, Type = "Job", Result = "failed", Name = "Mapped Job", Order = 1, Attempt = 1 }
                    ]
                },
                [
                    new() { Id = 1, Name = "Logs_Build_Attempt1_Mapped_Job", Source = jobId, Resource = new() { Type = "Container" } }
                ]);

            var result = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_",
                "--json");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("\"truncated\": false", result.Stdout, StringComparison.Ordinal);

            using var json = JsonDocument.Parse(result.Stdout);
            Assert.Equal(JsonValueKind.False, json.RootElement.GetProperty("truncated").ValueKind);
        }
        finally
        {
            CleanupSnapshot(cacheRoot);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3d — Bounded warnings contract
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvidencePlan_CleanPlan_WarningsContractPresentAndEmpty()
    {
        // The three warning fields are always serialized so consumers can bind them
        // unconditionally — and they stay empty/zero/false when nothing is warnable.
        SetupCompleteMockPlan();

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.Empty(plan.Warnings);
        Assert.Equal(0, plan.WarningTotal);
        Assert.False(plan.WarningsTruncated);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        var root = json.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, root.GetProperty("warningTotal").GetInt32());
        Assert.Equal(JsonValueKind.False, root.GetProperty("warningsTruncated").ValueKind);
    }

    [Fact]
    public async Task EvidencePlan_WarnablePlan_PopulatesWarningsMeaningfully()
    {
        // Two distinct warnable conditions in one plan: an in-flight build and a
        // truncated candidate list. Both must be reported, with real content.
        var plan = await BuildWarnablePlanAsync();

        // Pre-condition: the fixture really does trip both conditions, so the assertions
        // below are not vacuously satisfied by an empty warning set.
        Assert.True(plan.BuildIncomplete);
        var crowded = Assert.Single(plan.Entries, e => e.CandidatesTruncated);
        Assert.Equal(AzdoEvidenceMatcher.MaxCandidatesPerEntry, crowded.Candidates.Count);
        Assert.Equal(AzdoEvidenceMatcher.MaxCandidatesPerEntry + 2, crowded.CandidateTotal);

        Assert.Equal(2, plan.Warnings.Count);
        Assert.Equal(2, plan.WarningTotal);
        Assert.False(plan.WarningsTruncated);

        // Non-vacuous: every warning is substantive and distinct.
        Assert.All(plan.Warnings, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w));
            Assert.True(w.Length >= 20, $"Warning is not meaningful: '{w}'");
        });
        Assert.Equal(plan.Warnings.Count, plan.Warnings.Distinct(StringComparer.Ordinal).Count());

        // Each warning names the condition that produced it.
        Assert.Contains("not completed", plan.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("snapshot", plan.Warnings[0], StringComparison.Ordinal);

        Assert.Contains("Candidate lists truncated", plan.Warnings[1], StringComparison.Ordinal);
        Assert.Contains("candidateTotal", plan.Warnings[1], StringComparison.Ordinal);

        // Warnings are non-fatal diagnostics, distinct from the completeness reasons.
        Assert.NotEmpty(plan.IncompleteReasons);
        Assert.Empty(plan.Warnings.Intersect(plan.IncompleteReasons, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EvidencePlan_Warnings_SerializeStablyAndInDeterministicOrder()
    {
        var first = await BuildWarnablePlanAsync();
        var second = await BuildWarnablePlanAsync(shuffleInputs: true);

        // Deterministic order: shuffled timeline/artifact input yields the same warning sequence,
        // with the build-level warning ahead of the matcher-level warning.
        Assert.Equal(first.Warnings, second.Warnings);
        Assert.StartsWith("Build is not completed", first.Warnings[0], StringComparison.Ordinal);

        // Stable serialization: repeated serialization of the same plan is byte-identical, and
        // the array preserves list order.
        var json1 = JsonSerializer.Serialize(first);
        var json2 = JsonSerializer.Serialize(first);
        Assert.Equal(json1, json2);

        using var document = JsonDocument.Parse(json1);
        var serializedWarnings = document.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(w => w.GetString()!)
            .ToList();
        Assert.Equal(first.Warnings, serializedWarnings);
        Assert.Equal(first.WarningTotal, document.RootElement.GetProperty("warningTotal").GetInt32());
        Assert.Equal(
            first.WarningsTruncated,
            document.RootElement.GetProperty("warningsTruncated").GetBoolean());
    }

    [Fact]
    public async Task GetEvidencePlanAsync_WarningsOverBound_RetainsOriginalDetailsAndSerializesTrueTotal()
    {
        SetupOverBoundWarningMockPlan(reverseInputs: false);
        var first = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        SetupOverBoundWarningMockPlan(reverseInputs: true);
        var reversed = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        var expectedWarnings = Enumerable.Range(0, AzdoEvidencePlan.MaxWarnings)
            .Select(i =>
                $"Candidate lists truncated for job 'Crowded Job {i:D2}' (job-crowded-{i:D2}): " +
                $"showing first {AzdoEvidenceMatcher.MaxCandidatesPerEntry} of " +
                $"{AzdoEvidenceMatcher.MaxCandidatesPerEntry + 1}; candidateTotal preserves the full count.")
            .ToList();

        Assert.False(first.Complete);
        Assert.True(first.Truncated);
        Assert.Equal(11, first.TotalEntries);
        Assert.Equal(11, first.Entries.Count);
        Assert.All(first.Entries, entry =>
        {
            Assert.True(entry.CandidatesTruncated);
            Assert.Equal(AzdoEvidenceMatcher.MaxCandidatesPerEntry + 1, entry.CandidateTotal);
            Assert.Equal(AzdoEvidenceMatcher.MaxCandidatesPerEntry, entry.Candidates.Count);
        });

        Assert.Equal(10, AzdoEvidencePlan.MaxWarnings);
        Assert.Equal(10, first.Warnings.Count);
        Assert.Equal(11, first.WarningTotal);
        Assert.True(first.WarningsTruncated);
        Assert.Equal(expectedWarnings, first.Warnings);
        Assert.Equal(first.Warnings, reversed.Warnings);
        Assert.DoesNotContain(
            first.Warnings,
            warning => warning.Contains("Warning diagnostics truncated by the service", StringComparison.Ordinal));
        Assert.DoesNotContain(
            first.Warnings,
            warning => warning.Contains("Crowded Job 10", StringComparison.Ordinal));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(first));
        var root = json.RootElement;

        var serialized = root.GetProperty("warnings")
            .EnumerateArray()
            .Select(w => w.GetString()!)
            .ToList();

        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.Equal(expectedWarnings, serialized);
        Assert.Equal(11, root.GetProperty("warningTotal").GetInt32());
        Assert.Equal(JsonValueKind.True, root.GetProperty("warningsTruncated").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("complete").ValueKind);
        Assert.Equal(JsonValueKind.True, root.GetProperty("truncated").ValueKind);
        Assert.Equal(11, root.GetProperty("totalEntries").GetInt32());
    }

    [Fact]
    public async Task McpTool_EvidencePlan_SurfacesTruncatedWarningContract()
    {
        SetupOverBoundWarningMockPlan(reverseInputs: true);

        var plan = await _tools.EvidencePlan(
            "1570501",
            artifactPattern: "Logs_Build_*",
            artifactJobPrefix: "Logs_Build_",
            stripAttemptPrefix: true,
            match: "auto",
            jobResults: "failed,canceled");

        Assert.Equal(10, plan.Warnings.Count);
        Assert.Equal(11, plan.WarningTotal);
        Assert.True(plan.WarningsTruncated);
        Assert.StartsWith("Candidate lists truncated for job 'Crowded Job 00'", plan.Warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("Candidate lists truncated for job 'Crowded Job 09'", plan.Warnings[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(
            plan.Warnings,
            warning => warning.Contains("Warning diagnostics truncated by the service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CliEvidencePlan_TruncatedWarningsReachJsonAndHumanHeader()
    {
        const int buildId = 1570508;

        var cacheRoot = Path.Combine(AppContext.BaseDirectory, $"evidence-cli-warn-{Guid.NewGuid():N}");
        var snapshotRoot = Path.Combine(cacheRoot, "public");

        try
        {
            var (timeline, artifacts) = CreateOverBoundWarningInputs(reverseInputs: true);
            await SeedEvidenceSnapshotAsync(
                cacheRoot,
                buildId,
                new AzdoBuild { Id = buildId, BuildNumber = "warn-cli", Status = "completed", Result = "failed" },
                timeline,
                artifacts);

            var jsonResult = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_",
                "--json");

            Assert.True(
                jsonResult.ExitCode == 2,
                $"Expected exit 2, got {jsonResult.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{jsonResult.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{jsonResult.Stderr}");

            using var json = JsonDocument.Parse(jsonResult.Stdout);
            var root = json.RootElement;

            var warnings = root.GetProperty("warnings")
                .EnumerateArray()
                .Select(w => w.GetString()!)
                .ToList();
            Assert.Equal(10, warnings.Count);
            Assert.StartsWith("Candidate lists truncated for job 'Crowded Job 00'", warnings[0], StringComparison.Ordinal);
            Assert.StartsWith("Candidate lists truncated for job 'Crowded Job 09'", warnings[^1], StringComparison.Ordinal);
            Assert.Equal(11, root.GetProperty("warningTotal").GetInt32());
            Assert.Equal(JsonValueKind.True, root.GetProperty("warningsTruncated").ValueKind);
            Assert.DoesNotContain(
                warnings,
                warning => warning.Contains("Warning diagnostics truncated by the service", StringComparison.Ordinal));

            var humanResult = await RunCliAsync(
                snapshotRoot,
                "azdo", "evidence", "plan", buildId.ToString(CultureInfo.InvariantCulture),
                "--artifact-job-prefix", "Logs_Build_");

            Assert.Equal(2, humanResult.ExitCode);
            var stdout = humanResult.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("\nWarnings (showing 10 of 11; truncated):\n", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Warning diagnostics truncated by the service", stdout, StringComparison.Ordinal);
        }
        finally
        {
            CleanupSnapshot(cacheRoot);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // No writes/downloads — mock client request assertions
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetEvidencePlanAsync_CallsExactlyThreeGets()
    {
        // D6: exactly three cached GETs: build, timeline, artifacts. No fan-out.
        SetupCompleteMockPlan();

        await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        await _mockApi.Received(1).GetBuildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _mockApi.Received(1).GetTimelineAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _mockApi.Received(1).GetBuildArtifactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // No other API methods called.
        await _mockApi.DidNotReceive().GetBuildLogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEvidencePlanAsync_NothingDownloaded()
    {
        // D5: "Nothing is downloaded, extracted, or written to disk."
        SetupCompleteMockPlan();

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        // The plan contains downloadUrls but nothing is fetched.
        Assert.NotNull(plan);
        // downloadUrl is in candidate, not yet fetched:
        foreach (var entry in plan.Entries.Where(e => e.Status == "mapped"))
        {
            Assert.NotNull(entry.Candidates[0].DownloadUrl);
            // URL is safe to emit verbatim — no SAS query params (§3.5).
            Assert.DoesNotMatch(@"[?&](sig|se|sv|spr)=", entry.Candidates[0].DownloadUrl);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Cancellation propagation
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetEvidencePlanAsync_CancelledToken_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockApi.GetBuildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _svc.GetEvidencePlanAsync("1570501", DefaultOptions(), cts.Token));
    }

    [Fact]
    public async Task GetEvidencePlanAsync_PassesCancellationTokenToClient()
    {
        var ct = new CancellationToken(false);
        SetupCompleteMockPlan();

        await _svc.GetEvidencePlanAsync("1570501", DefaultOptions(), ct);

        // CancellationToken must be forwarded to every client call.
        await _mockApi.Received(1).GetBuildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct);
        await _mockApi.Received(1).GetTimelineAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct);
        await _mockApi.Received(1).GetBuildArtifactsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Build ID/URL resolution parity
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetEvidencePlanAsync_PlainId_UsesDefaultOrgProject()
    {
        SetupCompleteMockPlan();

        await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        await _mockApi.Received(1).GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEvidencePlanAsync_DevAzureUrl_ParsesOrgProject()
    {
        SetupMockPlanForOrg("myorg", "myproject", 9999);

        await _svc.GetEvidencePlanAsync(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=9999",
            DefaultOptions());

        await _mockApi.Received(1).GetBuildAsync("myorg", "myproject", 9999, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEvidencePlanAsync_VisualStudioUrl_ParsesOrgProject()
    {
        SetupMockPlanForOrg("dnceng", "internal", 8888);

        await _svc.GetEvidencePlanAsync(
            "https://dnceng.visualstudio.com/internal/_build/results?buildId=8888",
            DefaultOptions());

        await _mockApi.Received(1).GetBuildAsync("dnceng", "internal", 8888, Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // Provenance: build fields populated correctly
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetEvidencePlanAsync_ProvenanceContainsDefinitionFields()
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 1570501,
                BuildNumber = "20260827.10",
                Status = "completed",
                Result = "failed",
                Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" },
                SourceBranch = "refs/heads/main",
                SourceVersion = "abc123",
            });
        // Non-empty timeline required by GetEvidencePlanAsync; use a succeeded job
        // that is not selected by the failed/canceled filter.
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records = [new() { Id = Guid.NewGuid().ToString(), Type = "Job", Result = "succeeded", Name = "placeholder", Order = 1 }]
            });
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.NotNull(plan.Build);
        Assert.Equal(1570501, plan.Build!.BuildId);
        Assert.Equal("runtime", plan.Build.DefinitionName);
        Assert.Equal(666, plan.Build.DefinitionId);
        Assert.Equal("completed", plan.Build.Status);
        Assert.Equal("failed", plan.Build.Result);
        Assert.Equal("refs/heads/main", plan.Build.SourceBranch);
        Assert.Equal("abc123", plan.Build.SourceVersion);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_ProvenanceContainsPrSourceSha()
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 1570501,
                TriggerInfo = new AzdoTriggerInfo
                {
                    PrSourceSha = "c1191feefeed",
                    PrNumber = "14859",
                    PrSourceBranch = "refs/pull/14859/head",
                    PrIsFork = "True",
                    PrDraft = "False",
                    PrProviderId = "github"
                }
            });
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records = [new() { Id = Guid.NewGuid().ToString(), Type = "Job", Result = "succeeded", Name = "placeholder", Order = 1 }]
            });
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.Equal("c1191feefeed", plan.Build!.PrSourceSha);
        Assert.Equal("14859", plan.Build.PrNumber);
        Assert.Equal("refs/pull/14859/head", plan.Build.PrSourceBranch);
        Assert.True(plan.Build.PrIsFork);
        Assert.False(plan.Build.PrDraft);
        Assert.Equal("github", plan.Build.PrProviderId);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_BuildIncomplete_WhenBuildNotCompleted()
    {
        // D3: if the build is not completed, plan.BuildIncomplete = true.
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 1570501, Status = "inProgress" });
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records = [new() { Id = Guid.NewGuid().ToString(), Type = "Job", Result = "succeeded", Name = "placeholder", Order = 1 }]
            });
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());

        var plan = await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());

        Assert.True(plan.BuildIncomplete);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════════

    private static AzdoEvidencePlanOptions DefaultOptions() => new()
    {
        JobResults = ["failed", "canceled"],
        ArtifactPattern = "Logs_Build_*",
        ArtifactJobPrefix = "Logs_Build_",
        StripAttemptPrefix = true,
        Match = "auto"
    };

    private static string ExampleDownloadUrl()
    {
        var uri = new UriBuilder(Uri.UriSchemeHttps, "example.invalid")
        {
            Path = "/artifact-content",
            Query = "format=zip"
        };
        return uri.Uri.AbsoluteUri;
    }

    private void SetupCompleteMockPlan()
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 1570501,
                BuildNumber = "20260827.10",
                Status = "completed",
                Result = "failed",
                Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" },
                SourceBranch = "refs/heads/main",
                SourceVersion = "abc123"
            });

        var jobId = Guid.NewGuid().ToString();
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records = [new() { Id = jobId, Type = "Job", Result = "failed", Name = "linux-x64 release", Order = 1, Attempt = 1 }]
            });

        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>
            {
                new()
                {
                    Id = 1,
                    Name = "Logs_Build_Attempt1_linux_x64_release",
                    Source = jobId,
                    Resource = new AzdoArtifactResource
                    {
                        Type = "Container",
                        DownloadUrl = ExampleDownloadUrl(),
                        Properties = new Dictionary<string, string> { ["artifactsize"] = "51200" }
                    }
                }
            });
    }

    private void SetupIncompleteMockPlan()
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 1570501,
                Status = "completed",
                Result = "failed",
                Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" }
            });

        var jobId = Guid.NewGuid().ToString();
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new() { Id = jobId, Type = "Job", Result = "failed", Name = "Monitor Helix Jobs", Order = 5, Attempt = 1 }
                ]
            });

        // No artifact with matching source → missing entry
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());
    }

    /// <summary>
    /// Builds a plan that trips both warnable conditions: an in-flight build (build-level
    /// warning) and a job with more candidates than <c>MaxCandidatesPerEntry</c>
    /// (matcher-level warning).
    /// </summary>
    private void SetupWarnableMockPlan(bool shuffleInputs)
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 1570501,
                BuildNumber = "20260827.11",
                Status = "inProgress",
                Result = null,
                Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" }
            });

        const string crowdedJobId = "job-crowded";
        const string mappedJobId = "job-mapped";

        var records = new List<AzdoTimelineRecord>
        {
            new() { Id = crowdedJobId, Type = "Job", Result = "failed", Name = "Crowded Job", Order = 1, Attempt = 1 },
            new() { Id = mappedJobId,  Type = "Job", Result = "failed", Name = "Mapped Job",  Order = 2, Attempt = 1 }
        };

        var artifacts = Enumerable.Range(1, AzdoEvidenceMatcher.MaxCandidatesPerEntry + 2)
            .Select(i => new AzdoBuildArtifact
            {
                Id = i,
                Name = $"Logs_Build_Attempt{i}_Crowded_Job",
                Source = crowdedJobId,
                Resource = new AzdoArtifactResource { Type = "Container" }
            })
            .ToList();

        artifacts.Add(new AzdoBuildArtifact
        {
            Id = 999,
            Name = "Logs_Build_Attempt1_Mapped_Job",
            Source = mappedJobId,
            Resource = new AzdoArtifactResource { Type = "Container" }
        });

        if (shuffleInputs)
        {
            records.Reverse();
            artifacts.Reverse();
        }

        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Records = records });
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(artifacts);
    }

    private async Task<AzdoEvidencePlan> BuildWarnablePlanAsync(bool shuffleInputs = false)
    {
        SetupWarnableMockPlan(shuffleInputs);
        return await _svc.GetEvidencePlanAsync("1570501", DefaultOptions());
    }

    private void SetupOverBoundWarningMockPlan(bool reverseInputs)
    {
        _mockApi.GetBuildAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 1570501,
                BuildNumber = "20260827.12",
                Status = "completed",
                Result = "failed",
                Definition = new AzdoBuildDefinition { Id = 666, Name = "runtime" }
            });

        var (timeline, artifacts) = CreateOverBoundWarningInputs(reverseInputs);
        _mockApi.GetTimelineAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(timeline);
        _mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(artifacts);
    }

    private static (AzdoTimeline Timeline, List<AzdoBuildArtifact> Artifacts)
        CreateOverBoundWarningInputs(bool reverseInputs)
    {
        var records = new List<AzdoTimelineRecord>();
        var artifacts = new List<AzdoBuildArtifact>();

        for (var jobIndex = 0; jobIndex < 11; jobIndex++)
        {
            var jobId = $"job-crowded-{jobIndex:D2}";
            records.Add(new AzdoTimelineRecord
            {
                Id = jobId,
                Type = "Job",
                Result = "failed",
                Name = $"Crowded Job {jobIndex:D2}",
                Order = jobIndex + 1,
                Attempt = 1
            });

            for (var attempt = 1; attempt <= AzdoEvidenceMatcher.MaxCandidatesPerEntry + 1; attempt++)
            {
                artifacts.Add(new AzdoBuildArtifact
                {
                    Id = (jobIndex * 100) + attempt,
                    Name = $"Logs_Build_Attempt{attempt}_Crowded_Job_{jobIndex:D2}",
                    Source = jobId,
                    Resource = new AzdoArtifactResource { Type = "Container" }
                });
            }
        }

        if (reverseInputs)
        {
            records.Reverse();
            artifacts.Reverse();
        }

        return (new AzdoTimeline { Records = records }, artifacts);
    }

    /// <summary>
    /// Writes build/timeline/artifact payloads into an offline eval snapshot the CLI can
    /// read via <c>HLX_EVAL_SNAPSHOT</c>, so subprocess tests never touch the network.
    /// </summary>
    private static async Task SeedEvidenceSnapshotAsync(
        string cacheRoot,
        int buildId,
        AzdoBuild build,
        AzdoTimeline timeline,
        List<AzdoBuildArtifact> artifacts)
    {
        var ttl = TimeSpan.FromHours(1);
        using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = cacheRoot, AuthTokenHash = null });

        await store.SetMetadataAsync(
            $"azdo:dnceng-public:public:build:{buildId}",
            JsonSerializer.Serialize(build),
            ttl);
        await store.SetMetadataAsync(
            $"azdo:dnceng-public:public:timeline:{buildId}",
            JsonSerializer.Serialize(timeline),
            ttl);
        await store.SetMetadataAsync(
            $"azdo:dnceng-public:public:artifacts:{buildId}",
            JsonSerializer.Serialize(artifacts),
            ttl);
    }

    private static void CleanupSnapshot(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
            return;

        try
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked snapshot file must not fail the test.
        }
    }

    private void SetupMockPlanForOrg(string org, string project, int buildId)
    {
        _mockApi.GetBuildAsync(org, project, buildId, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = buildId, Status = "completed", Result = "succeeded" });
        _mockApi.GetTimelineAsync(org, project, buildId, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                // Non-empty required; succeeded job is not selected by failed/canceled filter.
                Records = [new() { Id = Guid.NewGuid().ToString(), Type = "Job", Result = "succeeded", Name = "placeholder", Order = 1 }]
            });
        _mockApi.GetBuildArtifactsAsync(org, project, buildId, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildArtifact>());
    }

    private static MethodInfo? GetMcpToolMethod(string toolName)
    {
        return typeof(AzdoMcpTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .FirstOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
    }

    private static void AssertCliParameter(
        CommandRegistry.ParamInfo parameter,
        string name,
        string type,
        string? defaultValue,
        bool isPositional)
    {
        Assert.Equal(name, parameter.Name);
        Assert.Equal(type, parameter.Type);
        Assert.Equal(defaultValue, parameter.Default);
        Assert.Equal(isPositional, parameter.IsPositional);
    }

    private static void AssertMcpParameter(
        ParameterInfo parameter,
        string name,
        Type type,
        bool hasDefault,
        object? defaultValue,
        string description)
    {
        Assert.Equal(name, parameter.Name);
        Assert.Equal(type, parameter.ParameterType);
        Assert.Equal(hasDefault, parameter.HasDefaultValue);
        if (hasDefault)
            Assert.Equal(defaultValue, parameter.DefaultValue);
        Assert.Equal(description, parameter.GetCustomAttribute<DescriptionAttribute>()?.Description);
    }

    private static void AssertSchemaProperty(
        JsonElement properties,
        string name,
        string[] types,
        object? defaultValue,
        bool hasDefault)
    {
        var property = properties.GetProperty(name);
        var type = property.GetProperty("type");
        var actualTypes = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [type.GetString()];
        Assert.Equal(types, actualTypes);
        Assert.Equal(
            GetMcpToolMethod("azdo_evidence_plan")!
                .GetParameters()
                .Single(parameter => parameter.Name == name)
                .GetCustomAttribute<DescriptionAttribute>()!
                .Description,
            property.GetProperty("description").GetString());
        Assert.Equal(hasDefault, property.TryGetProperty("default", out var actualDefault));
        if (hasDefault)
            Assert.Equal(JsonSerializer.Serialize(defaultValue), actualDefault.GetRawText());
    }

    private static async Task<string> CaptureStdoutAsync(Func<Task> action)
    {
        var original = Console.Out;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.NewLine = "\n";
        try
        {
            Console.SetOut(writer);
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    private static async Task<string> CaptureStderrAsync(Func<Task> action)
    {
        var original = Console.Error;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.NewLine = "\n";
        try
        {
            Console.SetError(writer);
            await action();
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string snapshotRoot,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(FindBuiltCliAssembly());
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment["HLX_EVAL_SNAPSHOT"] = snapshotRoot;
        startInfo.Environment.Remove("AZDO_TOKEN");
        startInfo.Environment.Remove("AZDO_TOKEN_TYPE");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindBuiltCliAssembly()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = testOutput.Name;
        var configuration = testOutput.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");

        for (var directory = testOutput; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "HelixTool",
                "bin",
                configuration,
                targetFramework,
                "HelixTool.dll");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate the {configuration}/{targetFramework} HelixTool build output.");
    }
}
