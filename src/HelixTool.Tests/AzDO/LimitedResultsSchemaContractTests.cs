using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Drift guard for the B1 fix: <see cref="LimitedResultsSchema{T}"/> is a hand-written mirror of
/// the JSON emitted by <see cref="LimitedResultsJsonConverter{T}"/>, declared via
/// <c>OutputSchemaType</c> on the six paginated AzDO tools. Nothing in the compiler ties the two
/// together, so these tests pin them to each other: if someone adds, renames, or removes a field
/// in the converter without updating the schema record, the advertised outputSchema would start
/// lying to clients.
/// </summary>
public class LimitedResultsSchemaContractTests
{
    private static HashSet<string> SchemaPropertyNames() =>
        typeof(LimitedResultsSchema<AzdoBuild>)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ConverterPropertyNames(LimitedResults<AzdoBuild> value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void SchemaRecord_DeclaresEveryPropertyTheConverterCanEmit()
    {
        var fullyPopulated = new LimitedResults<AzdoBuild>(
            [new AzdoBuild { Id = 1 }], truncated: true, total: 42, note: "capped");

        Assert.Equal(
            SchemaPropertyNames().OrderBy(n => n, StringComparer.Ordinal),
            ConverterPropertyNames(fullyPopulated).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void SchemaRecord_DeclaresNoPropertyTheConverterNeverEmits()
    {
        // Extra schema properties would advertise fields clients will never receive.
        var maximal = ConverterPropertyNames(
            new LimitedResults<AzdoBuild>([], truncated: false, total: 0, note: "n"));

        Assert.Empty(SchemaPropertyNames().Except(maximal, StringComparer.Ordinal));
    }

    [Fact]
    public void SchemaRecord_AlwaysPresentProperties_AreNotOptional()
    {
        // The converter writes "results" and "truncated" unconditionally; the schema record must
        // not mark them WhenWritingNull, or schema-driven clients would treat them as optional.
        foreach (var name in new[] { nameof(LimitedResultsSchema<AzdoBuild>.Results), nameof(LimitedResultsSchema<AzdoBuild>.Truncated) })
        {
            var property = typeof(LimitedResultsSchema<AzdoBuild>).GetProperty(name)!;
            Assert.Null(property.GetCustomAttribute<JsonIgnoreAttribute>());
        }
    }

    [Fact]
    public void SchemaRecord_ItemType_MatchesTheToolsResultType()
    {
        // Every paginated tool must point OutputSchemaType at LimitedResultsSchema<TItem> whose
        // TItem equals the TItem of the LimitedResults<TItem> it actually returns.
        var mismatches = typeof(AzdoMcpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Method, x.Attribute!.OutputSchemaType, ResultType: UnwrapLimitedResultsItem(x.Method.ReturnType)))
            .Where(x => x.ResultType is not null)
            .Where(x => x.OutputSchemaType != typeof(LimitedResultsSchema<>).MakeGenericType(x.ResultType!))
            .Select(x => $"{x.Method.Name}: expected LimitedResultsSchema<{x.ResultType!.Name}>, found {x.OutputSchemaType?.Name ?? "<none>"}")
            .ToList();

        Assert.Empty(mismatches);
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
