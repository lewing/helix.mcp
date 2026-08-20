using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HelixTool.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Pins the advertised output schema of the six capped/truncating <see cref="LimitedResults{T}"/>
/// AzDO tools to the minimal object constraint <c>{"type":"object"}</c>, produced by declaring
/// <c>OutputSchemaType = typeof(MinimalObjectSchema)</c>.
///
/// <para><b>Two things must both hold, and they pull in opposite directions.</b></para>
///
/// <para><i>Object-typed.</i> MCP SDK 2.x picks the <c>structuredContent</c> envelope from the
/// output schema, not the CLR type (<c>AIFunctionMcpServerTool.ShouldWrapValueForLegacyWire</c>).
/// <see cref="LimitedResults{T}"/> carries a custom converter, so System.Text.Json's exporter would
/// emit the permissive boolean schema <c>true</c> for it — a non-object schema, which makes the SDK
/// wrap the payload in <c>{"result": …}</c> for pre-<c>2026-07-28</c> clients while leaving it
/// unwrapped for SEP-2106 clients. One tool, two shapes. An object-typed schema removes that
/// branch. (The runtime wire proof lives in <c>StructuredContentReturnTypeTests</c>.)</para>
///
/// <para><i>Minimal.</i> <c>tools/list</c> is fixed context, paid on every session. Advertising the
/// payload property-by-property cost ~3.7 KB across these six tools and had to be hand-maintained
/// against a converter no compiler check tied it to. <c>{"type":"object"}</c> is 17 bytes and states
/// only what the SDK actually acts on.</para>
///
/// <para>So these tests assert the schema is exactly the minimum that is still object-typed —
/// neither weaker (which changes the wire shape) nor richer (which is fixed-context rent).</para>
/// </summary>
public class LimitedResultsOutputSchemaTests
{
    /// <summary>The exact schema text every <see cref="LimitedResults{T}"/> tool must advertise.</summary>
    private const string MinimalObjectSchemaJson = """{"type":"object"}""";

    private const int ExpectedLimitedResultsToolCount = 6;

    public static IEnumerable<object[]> LimitedResultsTools() =>
        typeof(AzdoMcpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Where(m => UnwrapLimitedResultsItem(m.ReturnType) is not null)
            .Select(m => new object[] { m });

    /// <summary>
    /// Guards the guard: if the tools were refactored away from <see cref="LimitedResults{T}"/>, the
    /// theories below would enumerate nothing and vacuously pass.
    /// </summary>
    [Fact]
    public void EverySixLimitedResultsTools_AreDiscovered()
    {
        var names = LimitedResultsTools()
            .Select(row => ((MethodInfo)row[0]).GetCustomAttribute<McpServerToolAttribute>()!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ExpectedLimitedResultsToolCount, names.Count);
    }

    /// <summary>
    /// All six point at the same shared marker type — not six per-item variants. One type means one
    /// place to change the advertised contract, and makes "they all agree" true by construction
    /// rather than by six separate assertions.
    /// </summary>
    [Theory]
    [MemberData(nameof(LimitedResultsTools))]
    public void LimitedResultsTool_DeclaresTheSharedMinimalSchemaType(MethodInfo method)
    {
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.True(
            attribute.OutputSchemaType == typeof(MinimalObjectSchema),
            $"AzdoMcpTools.{method.Name} (MCP tool '{attribute.Name}') returns LimitedResults<T> but declares " +
            $"OutputSchemaType = {attribute.OutputSchemaType?.Name ?? "<none>"}. Every LimitedResults<T> tool must " +
            "share typeof(MinimalObjectSchema): without it System.Text.Json emits the permissive schema `true` for " +
            "the custom-converter type, which is not object-typed, and SDK 2.x then wraps structuredContent in " +
            "{\"result\": …} for pre-2026-07-28 clients only.");
    }

    /// <summary>
    /// The end result on the wire: byte-for-byte <c>{"type":"object"}</c>. Asserting the exact text
    /// (rather than just <c>type == "object"</c>) is deliberate — it catches an enrichment of the
    /// marker type, which would silently re-inflate every one of these tools' fixed-context cost.
    /// </summary>
    [Theory]
    [MemberData(nameof(LimitedResultsTools))]
    public void LimitedResultsTool_AdvertisesExactlyTheMinimalObjectSchema(MethodInfo method)
    {
        var shell = RuntimeHelpers.GetUninitializedObject(typeof(AzdoMcpTools));
        var protocolTool = McpServerTool.Create(method, shell, options: null).ProtocolTool;

        Assert.True(protocolTool.OutputSchema.HasValue,
            $"'{protocolTool.Name}' is UseStructuredContent = true but advertised no outputSchema.");

        var rendered = JsonSerializer.Serialize(protocolTool.OutputSchema!.Value, McpJsonUtilities.DefaultOptions);

        Assert.True(rendered == MinimalObjectSchemaJson,
            $"'{protocolTool.Name}' advertised the outputSchema {rendered}, expected exactly " +
            $"{MinimalObjectSchemaJson}. A weaker schema (e.g. the boolean `true`) changes the structuredContent " +
            "envelope for pre-2026-07-28 clients; a richer one is fixed-context cost paid on every session by " +
            "every client. If MinimalObjectSchema grew a member, remove it — the type is schema-only.");
    }

    /// <summary>
    /// The marker's minimality is load-bearing, so state it directly: any member added to
    /// <see cref="MinimalObjectSchema"/> lands in the advertised schema of all six tools at once.
    /// </summary>
    [Fact]
    public void MinimalObjectSchema_HasNoSerializableMembers()
    {
        var declared = typeof(MinimalObjectSchema)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            // Records synthesize EqualityContract (protected); public surface should be empty.
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(declared);
    }

    private static Type? UnwrapLimitedResultsItem(Type returnType)
    {
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            returnType = returnType.GetGenericArguments()[0];

        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(LimitedResults<>)
            ? returnType.GetGenericArguments()[0]
            : null;
    }
}
