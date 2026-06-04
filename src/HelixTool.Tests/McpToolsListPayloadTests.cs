using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HelixTool.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace HelixTool.Tests;

/// <summary>
/// Measures the ground-truth byte cost of the MCP tools/list payload.
/// Implements the McpServerTool.Create + ProtocolTool serialization approach
/// documented in .squad/skills/mcp-wire-format-trim/SKILL.md.
/// </summary>
public class McpToolsListPayloadTests(ITestOutputHelper output)
{
    private static readonly Type[] ToolTypes =
    [
        typeof(AzdoMcpTools),
        typeof(HelixMcpTools),
        typeof(CiKnowledgeTool),
    ];

    [Fact]
    public void ToolsListPayload_ReportActualBytes()
    {
        var rows = ToolTypes
            .SelectMany(t =>
            {
                // Use an uninitialized shell — no constructor runs, no DI needed.
                // We only need the object reference for schema extraction; the tool is never invoked.
                var shell = RuntimeHelpers.GetUninitializedObject(t);
                return t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                    .Select(m => (method: m, target: shell));
            })
            .Select(entry =>
            {
                var (method, target) = entry;
                var mcpTool = McpServerTool.Create(method, target, options: null);
                var proto = mcpTool.ProtocolTool;

                var fullJson = JsonSerializer.Serialize(proto, McpJsonUtilities.DefaultOptions);
                var fullBytes = Encoding.UTF8.GetByteCount(fullJson);

                var inputBytes = Encoding.UTF8.GetByteCount(
                    JsonSerializer.Serialize(proto.InputSchema, McpJsonUtilities.DefaultOptions));

                var outputBytes = proto.OutputSchema.HasValue
                    ? Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(proto.OutputSchema.Value, McpJsonUtilities.DefaultOptions))
                    : 0;

                var descBytes = Encoding.UTF8.GetByteCount(proto.Description ?? "");

                return new
                {
                    Name = proto.Name,
                    FullBytes = fullBytes,
                    InputBytes = inputBytes,
                    OutputBytes = outputBytes,
                    DescBytes = descBytes,
                    HasOutput = proto.OutputSchema.HasValue,
                };
            })
            .OrderByDescending(r => r.FullBytes)
            .ToList();

        // Build the tools/list payload: {"tools":[...]} compact JSON wrapper
        var allProtos = ToolTypes
            .SelectMany(t =>
            {
                var shell = RuntimeHelpers.GetUninitializedObject(t);
                return t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                    .Select(m => McpServerTool.Create(m, shell, options: null).ProtocolTool);
            })
            .ToList();

        var listPayload = JsonSerializer.Serialize(new { tools = allProtos }, McpJsonUtilities.DefaultOptions);
        var listPayloadBytes = Encoding.UTF8.GetByteCount(listPayload);

        int toolCount = rows.Count;
        int inputOnlyTotal = rows.Sum(r => r.InputBytes);
        int outputOnlyTotal = rows.Sum(r => r.OutputBytes);
        int structuredToolCount = rows.Count(r => r.HasOutput);

        // ---- Report ----
        output.WriteLine($"=== tools/list Ground-Truth Measurement ===");
        output.WriteLine($"Total tools:          {toolCount}");
        output.WriteLine($"Structured (output):  {structuredToolCount} / {toolCount}");
        output.WriteLine($"");
        output.WriteLine($"inputSchema total:    {inputOnlyTotal,7} bytes ({inputOnlyTotal / 1024.0:F2} KB)");
        output.WriteLine($"outputSchema total:   {outputOnlyTotal,7} bytes ({outputOnlyTotal / 1024.0:F2} KB)");
        output.WriteLine($"input+output total:   {inputOnlyTotal + outputOnlyTotal,7} bytes ({(inputOnlyTotal + outputOnlyTotal) / 1024.0:F2} KB)");
        output.WriteLine($"");
        output.WriteLine($"tools/list payload:   {listPayloadBytes,7} bytes ({listPayloadBytes / 1024.0:F2} KB)  [{{\"tools\":[...]}} wrapper]");
        output.WriteLine($"");
        output.WriteLine($"--- Fattest tools (full per-tool JSON, sorted desc) ---");
        output.WriteLine($"{"Rank",-4} {"Name",-32} {"Total",6} {"Input",6} {"Output",7} {"Desc",5} HasOutput");
        output.WriteLine(new string('-', 75));
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            output.WriteLine($"{i + 1,-4} {r.Name,-32} {r.FullBytes,6} {r.InputBytes,6} {r.OutputBytes,7} {r.DescBytes,5} {(r.HasOutput ? "yes" : "no")}");
        }
        output.WriteLine($"");
        output.WriteLine($"--- vs. prior estimates ---");
        output.WriteLine($"Issue #74 heuristic:  16,212 bytes (80 bytes/param overcount)");
        output.WriteLine($"Ash static estimate:  11,317 bytes (inputSchema only)");
        output.WriteLine($"Ash range estimate:   15,000–25,000 bytes (with outputSchema)");
        output.WriteLine($"Measured (this run):  {listPayloadBytes} bytes");

        // Sanity assertions — not strict thresholds, just ensuring measurement ran
        Assert.True(toolCount >= 25, $"Expected at least 25 tools, found {toolCount}");
        Assert.True(listPayloadBytes > 5_000, $"Suspiciously small payload: {listPayloadBytes} bytes");
        Assert.True(structuredToolCount >= 15, $"Expected at least 15 structured tools, found {structuredToolCount}");
    }
}
