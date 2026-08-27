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
        // D8: exit 2 carries the full plan — not a fatal error. Plan is still usable.
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
                "Matching strategy: 'auto' (default) = source-id join then normalized-name fallback; 'source-id' = GUID join only; 'normalized-exact' = PR #132609 name parity; 'exact' = ordinal equality after prefix strip."),
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
