using System.Text.Json;
using System.Text.Json.Serialization;
using HelixTool.Core.AzDO;
using HelixTool.Core.CliSchema;
using Xunit;

namespace HelixTool.Tests;

public class SchemaGeneratorTests
{
    [Fact]
    public void GenerateSchema_Primitives_ReturnExpectedPlaceholders()
    {
        Assert.Equal("<string>", ParseSchema(typeof(string)).GetString());
        Assert.Equal(0, ParseSchema(typeof(int)).GetInt32());
        Assert.False(ParseSchema(typeof(bool)).GetBoolean());
        Assert.Equal(0d, ParseSchema(typeof(double)).GetDouble());
    }

    [Fact]
    public void GenerateSchema_DateTimeTypes_ReturnDatetimePlaceholder()
    {
        Assert.Equal("<datetime>", ParseSchema(typeof(DateTime)).GetString());
        Assert.Equal("<datetime>", ParseSchema(typeof(DateTimeOffset)).GetString());
    }

    [Fact]
    public void GenerateSchema_Guid_ReturnsGuidPlaceholder()
    {
        Assert.Equal("<guid>", ParseSchema(typeof(Guid)).GetString());
    }

    [Fact]
    public void GenerateSchema_Enum_ReturnsPipeSeparatedValues()
    {
        Assert.Equal("<Ready|Running|Complete>", ParseSchema(typeof(SampleState)).GetString());
    }

    [Fact]
    public void GenerateSchema_SimpleObject_WithoutJsonPropertyName_UsesPropertyNames()
    {
        var schema = GenerateSchema<SimpleDto>();
        var root = ParseSchema(schema);

        Assert.Equal("<string>", root.GetProperty(nameof(SimpleDto.DisplayName)).GetString());
        Assert.Equal(0, root.GetProperty(nameof(SimpleDto.RetryCount)).GetInt32());
        Assert.False(root.GetProperty(nameof(SimpleDto.IsEnabled)).GetBoolean());
        Assert.Contains($"\n  \"{nameof(SimpleDto.DisplayName)}\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateSchema_HonorsJsonPropertyName()
    {
        var schema = GenerateSchema<JsonPropertyNameDto>();
        var root = ParseSchema(schema);

        Assert.Equal("<string>", root.GetProperty("customName").GetString());
        Assert.Equal(0, root.GetProperty("another_name").GetInt32());
        Assert.Equal("<string>", root.GetProperty(nameof(JsonPropertyNameDto.NoAttribute)).GetString());
        Assert.Contains("\"customName\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"another_name\"", schema, StringComparison.Ordinal);
        Assert.Contains($"\"{nameof(JsonPropertyNameDto.NoAttribute)}\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{nameof(JsonPropertyNameDto.PropertyOne)}\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{nameof(JsonPropertyNameDto.PropertyTwo)}\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateSchema_NestedObject_RecursesIntoChildObjects()
    {
        var root = ParseSchema<NestedParentDto>();
        var child = root.GetProperty(nameof(NestedParentDto.Child));

        Assert.Equal("<string>", root.GetProperty(nameof(NestedParentDto.Title)).GetString());
        Assert.Equal("<string>", child.GetProperty(nameof(NestedChildDto.Name)).GetString());
        Assert.Equal(0, child.GetProperty(nameof(NestedChildDto.Count)).GetInt32());
    }

    [Fact]
    public void GenerateSchema_Collections_ReturnSingleElementArrays()
    {
        var root = ParseSchema<CollectionDto>();

        var items = root.GetProperty(nameof(CollectionDto.Items));
        Assert.Single(items.EnumerateArray());
        Assert.Equal("<string>", items[0].GetProperty(nameof(CollectionItemDto.Label)).GetString());

        var scores = root.GetProperty(nameof(CollectionDto.Scores));
        Assert.Single(scores.EnumerateArray());
        Assert.Equal(0, scores[0].GetInt32());

        var flags = root.GetProperty(nameof(CollectionDto.Flags));
        Assert.Single(flags.EnumerateArray());
        Assert.False(flags[0].GetBoolean());
    }

    [Fact]
    public void GenerateSchema_NullableValueTypes_UnwrapsUnderlyingType()
    {
        var root = ParseSchema<NullableDto>();

        Assert.Equal(0, root.GetProperty(nameof(NullableDto.OptionalCount)).GetInt32());
        Assert.False(root.GetProperty(nameof(NullableDto.OptionalEnabled)).GetBoolean());
        Assert.Equal("<datetime>", root.GetProperty(nameof(NullableDto.OptionalUpdated)).GetString());
    }

    [Fact]
    public void GenerateSchema_CircularReferences_ReturnCircularPlaceholder()
    {
        var root = ParseSchema<CircularA>();
        var next = root.GetProperty(nameof(CircularA.Next));

        Assert.Equal("<circular>", next.GetProperty(nameof(CircularB.Previous)).GetString());
    }

    [Fact]
    public void GenerateSchema_DepthLimit_StopsAtLevelFive()
    {
        var root = ParseSchema<DepthLevel1>();
        var level2 = root.GetProperty(nameof(DepthLevel1.Level2));
        var level3 = level2.GetProperty(nameof(DepthLevel2.Level3));
        var level4 = level3.GetProperty(nameof(DepthLevel3.Level4));
        var level5 = level4.GetProperty(nameof(DepthLevel4.Level5));

        Assert.Equal(JsonValueKind.Object, level2.ValueKind);
        Assert.Equal(JsonValueKind.Object, level3.ValueKind);
        Assert.Equal(JsonValueKind.Object, level4.ValueKind);
        Assert.Equal(JsonValueKind.Object, level5.ValueKind);
        Assert.Equal("<circular>", level5.GetProperty(nameof(DepthLevel5.Level6)).GetString());
    }

    [Fact]
    public void GenerateSchema_RealDtoSmokeTest_UsesJsonPropertyNamesForSerializedCoreType()
    {
        var root = ParseSchema<AzdoBuild>();

        Assert.Equal(0, root.GetProperty("id").GetInt32());
        Assert.Equal("<string>", root.GetProperty("buildNumber").GetString());
        Assert.Equal("<string>", root.GetProperty("status").GetString());
        Assert.Equal("<string>", root.GetProperty("result").GetString());
        Assert.Equal("<string>", root.GetProperty("sourceBranch").GetString());
        Assert.Equal("<string>", root.GetProperty("sourceVersion").GetString());
        Assert.Equal("<datetime>", root.GetProperty("queueTime").GetString());
        Assert.Equal("<datetime>", root.GetProperty("startTime").GetString());
        Assert.Equal("<datetime>", root.GetProperty("finishTime").GetString());
        Assert.Equal("<string>", root.GetProperty("url").GetString());

        var definition = root.GetProperty("definition");
        Assert.Equal(0, definition.GetProperty("id").GetInt32());
        Assert.Equal("<string>", definition.GetProperty("name").GetString());

        var requestedFor = root.GetProperty("requestedFor");
        Assert.Equal("<string>", requestedFor.GetProperty("displayName").GetString());

        var triggerInfo = root.GetProperty("triggerInfo");
        Assert.Equal("<string>", triggerInfo.GetProperty("ci.message").GetString());
        Assert.Equal("<string>", triggerInfo.GetProperty("pr.number").GetString());
    }

    [Fact]
    public void GenerateSchema_TypeOverload_MatchesGenericOverload()
    {
        Assert.Equal(GenerateSchema<SimpleDto>(), GenerateSchema(typeof(SimpleDto)));
    }

    private static JsonElement ParseSchema<T>()
        => ParseSchema(GenerateSchema<T>());

    private static JsonElement ParseSchema(Type type)
        => ParseSchema(GenerateSchema(type));

    private static JsonElement ParseSchema(string schema)
        => JsonDocument.Parse(schema).RootElement.Clone();

    private static string GenerateSchema<T>()
        => SchemaGenerator.GenerateSchema<T>();

    private static string GenerateSchema(Type type)
        => SchemaGenerator.GenerateSchema(type);

    public enum SampleState
    {
        Ready,
        Running,
        Complete
    }

    public sealed class SimpleDto
    {
        public string DisplayName { get; init; } = string.Empty;
        public int RetryCount { get; init; }
        public bool IsEnabled { get; init; }
    }

    public sealed class JsonPropertyNameDto
    {
        [JsonPropertyName("customName")]
        public string PropertyOne { get; init; } = string.Empty;

        [JsonPropertyName("another_name")]
        public int PropertyTwo { get; init; }

        public string NoAttribute { get; init; } = string.Empty;
    }

    public sealed class NestedParentDto
    {
        public string Title { get; init; } = string.Empty;
        public NestedChildDto Child { get; init; } = new();
    }

    public sealed class NestedChildDto
    {
        public string Name { get; init; } = string.Empty;
        public int Count { get; init; }
    }

    public sealed class CollectionDto
    {
        public List<CollectionItemDto> Items { get; init; } = [];
        public int[] Scores { get; init; } = [];
        public IReadOnlyList<bool> Flags { get; init; } = [];
    }

    public sealed class CollectionItemDto
    {
        public string Label { get; init; } = string.Empty;
    }

    public sealed class NullableDto
    {
        public int? OptionalCount { get; init; }
        public bool? OptionalEnabled { get; init; }
        public DateTimeOffset? OptionalUpdated { get; init; }
    }

    public sealed class CircularA
    {
        public CircularB? Next { get; init; }
    }

    public sealed class CircularB
    {
        public CircularA? Previous { get; init; }
    }

    public sealed class DepthLevel1
    {
        public DepthLevel2 Level2 { get; init; } = new();
    }

    public sealed class DepthLevel2
    {
        public DepthLevel3 Level3 { get; init; } = new();
    }

    public sealed class DepthLevel3
    {
        public DepthLevel4 Level4 { get; init; } = new();
    }

    public sealed class DepthLevel4
    {
        public DepthLevel5 Level5 { get; init; } = new();
    }

    public sealed class DepthLevel5
    {
        public DepthLevel6 Level6 { get; init; } = new();
    }

    public sealed class DepthLevel6
    {
        public string Value { get; init; } = string.Empty;
    }
}
