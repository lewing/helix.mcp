using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// T2 (mandatory gate, .squad/decisions/inbox/dallas-csharp-mcp-sdk-review.md §B2): a
/// protocol-level guard that every <c>[McpServerTool(UseStructuredContent = true)]</c> method
/// generates an <em>object</em> output schema, and that the resulting
/// <c>structuredContent</c> envelope is the same on every negotiated protocol version.
///
/// <para><b>Why the schema, not the CLR type.</b> SDK 2.x's structured-content envelope is
/// selected by <c>AIFunctionMcpServerTool.ShouldWrapValueForLegacyWire</c>, which inspects the
/// <em>generated JSON schema</em> — specifically whether it is <c>"type":"object"</c>. It never
/// looks at the CLR type. A CLR class whose schema is opaque to <c>System.Text.Json</c>'s
/// schema exporter (the exporter emits the permissive schema <c>true</c> for any type carrying
/// a custom <c>JsonConverter</c>) is therefore classified as a <em>non-object</em> return, and
/// the SDK wraps its structured content in <c>{"result": …}</c> for pre-SEP-2106 clients while
/// leaving it unwrapped for <c>2026-07-28</c>+ clients. The original version of this test
/// asserted on CLR shape and so green-lit all six <c>LimitedResults&lt;T&gt;</c> tools, which
/// were exactly the ones affected.</para>
///
/// <para><b>What an object schema buys us.</b> An honest object schema makes
/// <c>ShouldWrapValueForLegacyWire</c> return <see langword="false"/> at every protocol version,
/// so a tool presents one <c>structuredContent</c> shape to every client instead of two — and
/// schema-driven clients get a real contract instead of the information-free <c>true</c>.</para>
/// </summary>
public class StructuredContentReturnTypeTests
{
    private static readonly Type[] ToolTypes =
    [
        typeof(AzdoMcpTools),
        typeof(HelixMcpTools),
        typeof(CiKnowledgeTool),
    ];

    public static IEnumerable<object[]> StructuredContentMethods() =>
        ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.UseStructuredContent == true)
                .Select(m => new object[] { t, m }));

    /// <summary>
    /// Primary assertion: build the real <see cref="McpServerTool"/> the server would register
    /// and inspect its generated <c>OutputSchema</c>. It must be a JSON object schema with
    /// <c>"type":"object"</c> — never the permissive boolean schema <c>true</c>/<c>false</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(StructuredContentMethods))]
    public void UseStructuredContent_Method_GeneratesObjectOutputSchema(Type toolType, MethodInfo method)
    {
        // Uninitialized shell: no constructor runs, no DI needed. The tool is never invoked here.
        var shell = RuntimeHelpers.GetUninitializedObject(toolType);
        var protocolTool = McpServerTool.Create(method, shell, options: null).ProtocolTool;

        Assert.True(protocolTool.OutputSchema.HasValue,
            $"{toolType.Name}.{method.Name} ('{protocolTool.Name}') is " +
            "[McpServerTool(UseStructuredContent = true)] but generated no outputSchema at all.");

        var schema = protocolTool.OutputSchema!.Value;
        var rendered = JsonSerializer.Serialize(schema, McpJsonUtilities.DefaultOptions);

        Assert.False(schema.ValueKind is JsonValueKind.True or JsonValueKind.False,
            OpaqueSchemaFailure(toolType, method, protocolTool.Name, rendered));

        Assert.True(
            schema.ValueKind is JsonValueKind.Object
            && schema.TryGetProperty("type", out var typeProperty)
            && typeProperty.ValueKind is JsonValueKind.String
            && typeProperty.GetString() == "object",
            OpaqueSchemaFailure(toolType, method, protocolTool.Name, rendered));
    }

    private static string OpaqueSchemaFailure(Type toolType, MethodInfo method, string toolName, string rendered) =>
        $"{toolType.Name}.{method.Name} (MCP tool '{toolName}') generated the outputSchema {rendered}, " +
        "which is not an object schema (\"type\":\"object\"). An opaque or non-object schema changes the " +
        "structuredContent envelope: SDK 2.x's ShouldWrapValueForLegacyWire inspects the generated schema " +
        "(not the CLR type), so pre-2026-07-28 clients receive {\"result\": <value>} while 2026-07-28+ " +
        "clients receive <value> unwrapped — the same tool ships two different shapes. The usual cause is a " +
        "custom [JsonConverter] on the return type, which makes it opaque to System.Text.Json's schema " +
        "exporter (it then emits the permissive schema `true`). Fix by declaring the wire shape explicitly, " +
        "e.g. [McpServerTool(..., OutputSchemaType = typeof(<shape>))].";

    /// <summary>
    /// Secondary assertion, retained from the original guard: the unwrapped CLR return type must
    /// not be a scalar. Correct as far as it goes — a scalar return would also produce a
    /// non-object schema — but it is not sufficient on its own (see the class remarks).
    /// </summary>
    [Theory]
    [MemberData(nameof(StructuredContentMethods))]
    public void UseStructuredContent_Method_ReturnsNonScalarType(Type toolType, MethodInfo method)
    {
        var returnType = UnwrapAsyncReturnType(method.ReturnType);

        bool isScalarWireType =
            returnType.IsPrimitive
            || returnType.IsEnum
            || returnType == typeof(string)
            || returnType == typeof(decimal)
            || returnType == typeof(Guid)
            || returnType == typeof(DateTime)
            || returnType == typeof(DateTimeOffset);

        Assert.False(isScalarWireType,
            $"{toolType.Name}.{method.Name} is [McpServerTool(UseStructuredContent = true)] but its " +
            $"unwrapped return type is '{returnType.Name}', a scalar, which cannot produce an object " +
            "output schema. Wrap the value in a DTO/record, or remove UseStructuredContent for this tool.");
    }

    /// <summary>
    /// Guards the guard: if a refactor silently removed every UseStructuredContent=true
    /// annotation, the theories above would enumerate zero cases and vacuously pass. Pin a
    /// floor so that disappearance is caught.
    /// </summary>
    [Fact]
    public void AtLeastOneStructuredContentMethod_IsGuarded()
    {
        var count = StructuredContentMethods().Count();
        Assert.True(count >= 20,
            $"Expected at least 20 UseStructuredContent=true tool methods across " +
            $"{string.Join(", ", ToolTypes.Select(t => t.Name))} (matched the 2.2.0 tools/list " +
            $"baseline of 20/25 structured tools), found {count}. If tools were intentionally " +
            "added/removed, adjust this floor.");
    }

    /// <summary>
    /// Wire-level companion to the schema assertion, run against a real MCP server over real
    /// Streamable-HTTP JSON-RPC with a real <see cref="McpClient"/>: <c>azdo_builds</c> (a
    /// <c>LimitedResults&lt;T&gt;</c> tool) must emit <c>structuredContent</c> that agrees with
    /// its own JSON text block on <em>both</em> a pre-SEP-2106 client (<c>2025-06-18</c>) and a
    /// SEP-2106 client (<c>2026-07-28</c>).
    ///
    /// This is precisely what an opaque schema breaks: the SDK applies the <c>{"result": …}</c>
    /// legacy envelope only on the older version, so the two clients disagree with each other
    /// and the older one disagrees with the tool's own JSON body. Asserting agreement with the
    /// text block pins the envelope without hard-coding which envelope the SDK chose.
    /// </summary>
    [Theory]
    [InlineData("2025-06-18")]   // pre-SEP-2106: SDK applies the legacy {"result": …} envelope to non-object schemas
    [InlineData("2026-07-28")]   // SEP-2106: natural (unwrapped) shape
    public async Task LimitedResultsTool_StructuredContentMatchesItsJsonBody_AtEveryProtocolVersion(string protocolVersion)
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns([new AzdoBuild { Id = 1, BuildNumber = "b1" }]);

        using var host = await CreateAzdoMcpHostAsync(api);
        using var httpClient = host.GetTestServer().CreateClient();
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/") }, httpClient);
        await using var client = await McpClient.CreateAsync(
            transport, new McpClientOptions { ProtocolVersion = protocolVersion });

        Assert.Equal(protocolVersion, client.NegotiatedProtocolVersion);

        var result = await client.CallToolAsync("azdo_builds", new Dictionary<string, object?> { ["top"] = 5 });

        Assert.True(result.StructuredContent.HasValue,
            "azdo_builds returned no structuredContent; UseStructuredContent = true should always populate it.");
        var structured = result.StructuredContent!.Value;

        var textBlock = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var textJson = JsonDocument.Parse(textBlock.Text);

        Assert.True(
            JsonElement.DeepEquals(structured, textJson.RootElement),
            $"At protocol version {protocolVersion}, azdo_builds' structuredContent " +
            $"({JsonSerializer.Serialize(structured, McpJsonUtilities.DefaultOptions)}) does not match its own " +
            $"JSON text block ({textBlock.Text}). The SDK wraps structuredContent in a {{\"result\": …}} " +
            "envelope for clients older than 2026-07-28 whenever the tool's generated outputSchema is not an " +
            "object schema, so an opaque schema makes the same tool present two different shapes depending on " +
            "which client is connected. Give the return type a real object schema (OutputSchemaType) so every " +
            "client sees the LimitedResults envelope {results, truncated, total?, note?}.");

        // The pagination envelope itself must survive, not merely be self-consistent.
        Assert.Equal(JsonValueKind.Array, structured.GetProperty("results").ValueKind);
        Assert.Equal(JsonValueKind.False, structured.GetProperty("truncated").ValueKind);
    }

    /// <summary>
    /// The wire schema advertised in <c>tools/list</c> must also describe the real
    /// <c>LimitedResults&lt;T&gt;</c> envelope at every protocol version — including after the
    /// pre-SEP-2106 wire rewrite performed by <c>BuildLegacyWireProtocolTool</c>, which would
    /// otherwise turn an opaque schema into the placeholder
    /// <c>{"type":"object","properties":{"result":true}}</c>.
    /// </summary>
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-28")]
    public async Task LimitedResultsTool_AdvertisesMeaningfulOutputSchema_AtEveryProtocolVersion(string protocolVersion)
    {
        var api = Substitute.For<IAzdoApiClient>();

        using var host = await CreateAzdoMcpHostAsync(api);
        using var httpClient = host.GetTestServer().CreateClient();
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/") }, httpClient);
        await using var client = await McpClient.CreateAsync(
            transport, new McpClientOptions { ProtocolVersion = protocolVersion });

        var tools = await client.ListToolsAsync();
        var builds = tools.Single(t => t.Name == "azdo_builds").ProtocolTool;

        Assert.True(builds.OutputSchema.HasValue, "azdo_builds advertised no outputSchema on the wire.");
        var schema = builds.OutputSchema!.Value;
        var rendered = JsonSerializer.Serialize(schema, McpJsonUtilities.DefaultOptions);

        Assert.True(
            schema.ValueKind is JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.TryGetProperty("results", out _)
            && properties.TryGetProperty("truncated", out _),
            $"At protocol version {protocolVersion}, azdo_builds advertised the outputSchema {rendered}, which " +
            "does not describe the LimitedResults envelope {results, truncated, total?, note?}. A permissive " +
            "`true` schema (what System.Text.Json emits for a type with a custom JsonConverter) tells " +
            "schema-driven clients nothing, and is rewritten to the placeholder " +
            "{\"type\":\"object\",\"properties\":{\"result\":true}} for pre-2026-07-28 clients.");
    }

    private static async Task<IHost> CreateAzdoMcpHostAsync(IAzdoApiClient api) =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(api);
                    services.AddSingleton(Substitute.For<IAzdoTokenAccessor>());
                    services.AddSingleton(sp => new AzdoService(sp.GetRequiredService<IAzdoApiClient>()));

                    services.AddMcpServer()
                        .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                        .WithTools<AzdoMcpTools>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapMcp());
                });
            })
            .StartAsync();

    private static Type UnwrapAsyncReturnType(Type returnType)
    {
        if (returnType.IsGenericType)
        {
            var def = returnType.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
                return returnType.GetGenericArguments()[0];
        }
        return returnType;
    }
}
