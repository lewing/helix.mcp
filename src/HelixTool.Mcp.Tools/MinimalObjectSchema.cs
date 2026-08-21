namespace HelixTool.Mcp.Tools;

/// <summary>
/// Schema-only marker whose generated JSON Schema is exactly <c>{"type":"object"}</c>.
///
/// <para><b>Why it exists.</b> MCP SDK 2.x decides the <c>structuredContent</c> wire envelope by
/// inspecting a tool's <em>output schema</em>, not its CLR return type
/// (<c>AIFunctionMcpServerTool.ShouldWrapValueForLegacyWire</c>). A schema that is not
/// <c>"type":"object"</c> — notably the permissive boolean schema <c>true</c> that
/// System.Text.Json's exporter emits for any type carrying a custom <c>JsonConverter</c> — makes
/// the SDK wrap the value in <c>{"result": …}</c> for clients that negotiated a protocol version
/// older than <c>2026-07-28</c>, while leaving it unwrapped for SEP-2106 clients. One tool would
/// then present two different shapes. Declaring
/// <c>OutputSchemaType = typeof(MinimalObjectSchema)</c> pins the schema to <c>"type":"object"</c>,
/// so <c>ShouldWrapValueForLegacyWire</c> is <see langword="false"/> at every protocol version and
/// the natural, unwrapped payload is the only shape ever emitted.</para>
///
/// <para><b>Why it is empty.</b> <c>tools/list</c> is fixed context: every advertised byte is paid
/// on every session, by every client, forever. A property-by-property mirror of the payload cost
/// ~3.7 KB (~930 tokens at this repo's 4 bytes/token model) across the six paginated AzDO tools,
/// and would additionally have to be kept in lockstep with a hand-written converter that no
/// compiler check ties it to. The bare object constraint is the honest minimum: it says exactly
/// what the SDK needs to know — the payload is a JSON object — and nothing it would have to keep
/// true by convention. The payload's actual shape stays documented where it is authoritative: in
/// the tool description and in the returned JSON itself.</para>
///
/// <para>Never instantiated and never serialized; only <c>typeof(MinimalObjectSchema)</c> is used.
/// It has no members by design — adding one would enlarge every consumer's advertised schema.
/// <c>LimitedResultsOutputSchemaTests</c> pins both properties (exact schema text, and that all six
/// tools share this one type).</para>
/// </summary>
public sealed record MinimalObjectSchema;
