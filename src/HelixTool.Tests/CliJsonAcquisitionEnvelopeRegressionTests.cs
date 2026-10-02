using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

[Collection("AzdoTokenEnv")]
public sealed class CliJsonAcquisitionEnvelopeRegressionTests
{
    private const string JobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItem = "System.Runtime.Tests";

    public static IEnumerable<object[]> AzdoJsonCommands()
    {
        yield return ["azdo build", "get_build", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<AzdoBuild?>(CommandException("azdo", "get_build")));
            await commands.Build("42", json: true);
        })];
        yield return ["azdo builds", "list_builds", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.ListBuildsAsync("dnceng-public", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IReadOnlyList<AzdoBuild>>(CommandException("azdo", "list_builds")));
            await commands.Builds(json: true);
        })];
        yield return ["azdo timeline", "get_timeline", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<AzdoTimeline?>(CommandException("azdo", "get_timeline")));
            await commands.Timeline("42", json: true);
        })];
        yield return ["azdo log", "get_build_log", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetBuildLogAsync("dnceng-public", "public", 42, 7, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<string?>(CommandException("azdo", "get_build_log")));
            await commands.Log("42", 7, tailLines: null, json: true);
        })];
        yield return ["azdo search-log", "get_build_log", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetBuildLogAsync("dnceng-public", "public", 42, 7, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<string?>(CommandException("azdo", "get_build_log")));
            await commands.SearchLog("42", pattern: "error", logId: 7, json: true);
        })];
        yield return ["azdo changes", "list_build_changes", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetBuildChangesAsync("dnceng-public", "public", 42, Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IReadOnlyList<AzdoBuildChange>>(CommandException("azdo", "list_build_changes")));
            await commands.Changes("42", json: true);
        })];
        yield return ["azdo test-runs", "list_test_runs", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetTestRunsAsync("dnceng-public", "public", 42, Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IReadOnlyList<AzdoTestRun>>(CommandException("azdo", "list_test_runs")));
            await commands.TestRuns("42", json: true);
        })];
        yield return ["azdo test-results", "list_test_results", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetTestResultsAsync("dnceng-public", "public", 101, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IReadOnlyList<AzdoTestResult>>(CommandException("azdo", "list_test_results")));
            await commands.TestResults("42", 101, json: true);
        })];
        yield return ["azdo artifacts", "list_artifacts", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IReadOnlyList<AzdoBuildArtifact>>(CommandException("azdo", "list_artifacts")));
            await commands.Artifacts("42", json: true);
        })];
        yield return ["azdo search-timeline", "get_timeline", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<AzdoTimeline?>(CommandException("azdo", "get_timeline")));
            await commands.SearchTimeline("42", "error", json: true);
        })];
        yield return ["azdo test-attachments", "list_test_attachments", (Func<IAzdoApiClient, global::AzdoCommands, Task>)(async (api, commands) =>
        {
            api.GetTestAttachmentsAsync("dnceng-public", "public", 101, 202, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IReadOnlyList<AzdoTestAttachment>>(CommandException("azdo", "list_test_attachments")));
            await commands.TestAttachments(101, 202, json: true);
        })];
    }

    public static IEnumerable<object[]> HelixJsonCommands()
    {
        yield return ["helix status", "get_helix_job", (Func<IHelixApiClient, global::Commands, Task>)(async (api, commands) =>
        {
            api.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IJobDetails>(CommandException("helix", "get_helix_job")));
            await commands.Status(JobId, json: true);
        })];
        yield return ["helix files", "list_helix_work_item_files", (Func<IHelixApiClient, global::Commands, Task>)(async (api, commands) =>
        {
            api.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IReadOnlyList<IWorkItemFile>>(CommandException("helix", "list_helix_work_item_files")));
            await commands.Files(JobId, WorkItem, json: true);
        })];
        yield return ["helix work-item", "get_helix_work_item", (Func<IHelixApiClient, global::Commands, Task>)(async (api, commands) =>
        {
            api.GetWorkItemDetailsAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IWorkItemDetails>(CommandException("helix", "get_helix_work_item")));
            api.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
                .Returns(new List<IWorkItemFile>());
            await commands.WorkItem(JobId, WorkItem, json: true);
        })];
    }

    [Theory]
    [MemberData(nameof(AzdoJsonCommands))]
    public async Task AzdoCommand_JsonAcquisitionError_WritesFailureEnvelope(
        string commandName,
        string operation,
        Func<IAzdoApiClient, global::AzdoCommands, Task> invoke)
    {
        var api = Substitute.For<IAzdoApiClient>();
        var commands = new global::AzdoCommands(new AzdoService(api), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode, thrown) = await CaptureConsoleAsync(() => invoke(api, commands), "--json");

        Assert.Null(thrown);
        AssertJsonFailureEnvelope(commandName, stdout, exitCode, "azdo", operation);
    }

    [Theory]
    [MemberData(nameof(HelixJsonCommands))]
    public async Task HelixCommand_JsonAcquisitionError_WritesFailureEnvelope(
        string commandName,
        string operation,
        Func<IHelixApiClient, global::Commands, Task> invoke)
    {
        var api = Substitute.For<IHelixApiClient>();
        var service = new HelixService(api, new HttpClient());
        var credentialStore = Substitute.For<ICredentialStore>();
        var commands = new global::Commands(
            new Lazy<HelixService>(() => service),
            credentialStore,
            new ChainedHelixTokenAccessor(credentialStore));

        var (stdout, _, exitCode, thrown) = await CaptureConsoleAsync(() => invoke(api, commands), "--json");

        Assert.Null(thrown);
        AssertJsonFailureEnvelope(commandName, stdout, exitCode, "helix", operation);
    }

    private static HlxAcquisitionException CommandException(string provider, string operation) =>
        AcquisitionAssertions.Exception(
            AcquisitionErrorKind.TransportError,
            provider,
            operation,
            new Dictionary<string, object?> { ["operation"] = operation });

    private static void AssertJsonFailureEnvelope(
        string commandName,
        string stdout,
        int exitCode,
        string provider,
        string operation)
    {
        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("transport_error", error.GetProperty("kind").GetString());
        Assert.Equal(provider, error.GetProperty("provider").GetString());
        Assert.Equal(operation, error.GetProperty("operation").GetString());
        Assert.True(error.TryGetProperty("resource", out _), $"{commandName} error envelope must include structured resource.");
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode, Exception? Thrown)> CaptureConsoleAsync(Func<Task> action, params string[] commandArguments)
    {
        await TestConsoleCapture.Lock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.ExitCode = 0;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Exception? thrown = null;
            try
            {
                await global::CliAcquisitionErrorPipeline.InvokeAsync(_ => action(), commandArguments);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            return (stdout.ToString(), stderr.ToString(), Environment.ExitCode, thrown);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
            TestConsoleCapture.Lock.Release();
        }
    }
}
