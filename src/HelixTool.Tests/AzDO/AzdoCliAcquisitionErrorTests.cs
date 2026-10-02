using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class AzdoCliAcquisitionErrorTests
{
    [Fact]
    public async Task AzdoLog_JsonAcquisitionError_WritesEnvelopeToStdoutAndExitsOne()
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildLogAsync("dnceng-public", "public", 12345, 7, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string?>(AcquisitionAssertions.Exception(
                AcquisitionErrorKind.NotFound,
                "azdo",
                "get_build_log",
                new Dictionary<string, object?>
                {
                    ["org"] = "dnceng-public",
                    ["project"] = "public",
                    ["buildId"] = 12345,
                    ["logId"] = 7
                },
                httpStatus: 404)));
        var commands = new global::AzdoCommands(new AzdoService(api), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode) = await CaptureConsoleAsync(() => commands.Log("12345", 7, json: true), "--json");

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("kind").GetString());
        Assert.Equal("azdo", error.GetProperty("provider").GetString());
        Assert.Equal("get_build_log", error.GetProperty("operation").GetString());
        Assert.Equal(404, error.GetProperty("httpStatus").GetInt32());
        Assert.Equal(7, error.GetProperty("resource").GetProperty("logId").GetInt32());
    }

    [Fact]
    public async Task AzdoLog_JsonEmptyBodyAbsentFromMetadata_WritesNotFoundEnvelopeAndExitsOne()
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildLogAsync("dnceng-public", "public", 12345, 999999, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        api.GetBuildLogsListAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 1, LineCount = 10 } });
        api.GetTimelineAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new AzdoTimelineRecord { Id = "task1", Log = new AzdoLogReference { Id = 2 } }
                ]
            });
        var commands = new global::AzdoCommands(new AzdoService(api), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode) = await CaptureConsoleAsync(() => commands.Log("12345", 999999, json: true), "--json");

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("kind").GetString());
        Assert.Equal("azdo", error.GetProperty("provider").GetString());
        Assert.Equal("get_build_log", error.GetProperty("operation").GetString());
        Assert.Equal(12345, error.GetProperty("resource").GetProperty("buildId").GetInt32());
        Assert.Equal(999999, error.GetProperty("resource").GetProperty("logId").GetInt32());
    }

    [Fact]
    public async Task AzdoTimeline_JsonInvalidResponse_WritesEnvelopeToStdoutAndExitsOne()
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetTimelineAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<AzdoTimeline?>(AcquisitionAssertions.Exception(
                AcquisitionErrorKind.InvalidResponse,
                "azdo",
                "get_timeline",
                new Dictionary<string, object?>
                {
                    ["org"] = "dnceng-public",
                    ["project"] = "public",
                    ["buildId"] = 12345
                },
                httpStatus: 200)));
        var commands = new global::AzdoCommands(new AzdoService(api), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode) = await CaptureConsoleAsync(() => commands.Timeline("12345", json: true), "--json");

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("invalid_response", error.GetProperty("kind").GetString());
        Assert.Equal("azdo", error.GetProperty("provider").GetString());
        Assert.Equal("get_timeline", error.GetProperty("operation").GetString());
        Assert.Equal(200, error.GetProperty("httpStatus").GetInt32());
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> CaptureConsoleAsync(Func<Task> action, params string[] commandArguments)
    {
        await HelixTool.Tests.TestConsoleCapture.Lock.WaitAsync();
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
            await global::CliAcquisitionErrorPipeline.InvokeAsync(_ => action(), commandArguments);
            return (stdout.ToString(), stderr.ToString(), Environment.ExitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
            HelixTool.Tests.TestConsoleCapture.Lock.Release();
        }
    }
}
