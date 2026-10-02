using System.Net;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using HelixTool.Mcp.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

[Collection("AzdoTokenEnv")]
public sealed class AcquisitionRedactionRegressionTests
{
    private const string Secret = "SECRET-SAS-SIGNATURE";
    private const string SensitiveUrl = "https://helix.dot.net/api/2019-06-17/jobs/job/workitems/wi/files/output.binlog?sv=2024-01-01&sig=" + Secret;

    [Fact]
    public async Task HelixDirectDownload_AcquisitionError_DoesNotSerializeQueryStringOrSecret()
    {
        using var httpClient = new HttpClient(new FailingHandler(HttpStatusCode.Forbidden));
        var service = new HelixService(Substitute.For<IHelixApiClient>(), httpClient);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => service.DownloadFromUrlAsync(SensitiveUrl));

        AssertSerializedErrorIsRedacted(ex.Error);
    }

    [Fact]
    public async Task CliJsonEnvelope_AcquisitionError_DoesNotSerializeQueryStringOrSecret()
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<AzdoBuild?>(SensitiveAcquisitionException("get_build")));
        var commands = new global::AzdoCommands(new AzdoService(api), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode) = await CaptureConsoleAsync(() => commands.Build("12345", json: true), "--json");

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("sig=", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, stdout, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        AssertSerializedErrorIsRedacted(document.RootElement.GetProperty("error"));
    }

    [Fact]
    public async Task McpStructuredContent_AcquisitionError_DoesNotSerializeQueryStringOrSecret()
    {
        var options = new McpServerOptions().AddAcquisitionErrorFilter();
        var filter = Assert.Single(options.Filters.Request.CallToolFilters);
        var handler = filter((_, _) => throw SensitiveAcquisitionException("download_url"));

        var result = await handler(CreateRequest("helix_download"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.True(result.StructuredContent.HasValue);
        var serialized = result.StructuredContent.Value.GetRawText();
        Assert.DoesNotContain("sig=", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, serialized, StringComparison.Ordinal);
        AssertSerializedErrorIsRedacted(result.StructuredContent.Value.GetProperty("error"));
    }

    private static HlxAcquisitionException SensitiveAcquisitionException(string operation) =>
        AcquisitionAssertions.Exception(
            AcquisitionErrorKind.AccessDenied,
            "helix",
            operation,
            new Dictionary<string, object?> { ["url"] = SensitiveUrl },
            httpStatus: 403);

    private static void AssertSerializedErrorIsRedacted(AcquisitionError error)
    {
        var serialized = JsonSerializer.Serialize(error, AcquisitionJsonOptions.Default);
        Assert.DoesNotContain("sig=", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, serialized, StringComparison.Ordinal);
    }

    private static void AssertSerializedErrorIsRedacted(JsonElement error)
    {
        var serialized = error.GetRawText();
        Assert.DoesNotContain("sig=", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, serialized, StringComparison.Ordinal);
    }

    private static RequestContext<CallToolRequestParams> CreateRequest(string toolName)
        => new(
            server: Substitute.For<McpServer>(),
            jsonRpcRequest: new JsonRpcRequest { Method = "tools/call" },
            parameters: new CallToolRequestParams { Name = toolName });

    private static async Task<(string Stdout, string Stderr, int ExitCode)> CaptureConsoleAsync(Func<Task> action, params string[] commandArguments)
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
            await global::CliAcquisitionErrorPipeline.InvokeAsync(_ => action(), commandArguments);
            return (stdout.ToString(), stderr.ToString(), Environment.ExitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
            TestConsoleCapture.Lock.Release();
        }
    }

    private sealed class FailingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent("denied", Encoding.UTF8, "text/plain")
            });
    }
}
