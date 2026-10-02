using System.Text.Json.Serialization;

namespace HelixTool.Core.Paging;

/// <summary>CLI JSON envelope for list commands that may return a bounded page.</summary>
public sealed record HlxListEnvelope<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;

    [JsonPropertyName("results")]
    public IReadOnlyList<T> Results { get; init; } = [];

    [JsonPropertyName("returned")]
    public int Returned { get; init; }

    [JsonPropertyName("total")]
    public int? Total { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("next")]
    public HlxNextPage? Next { get; init; }

    [JsonPropertyName("cache")]
    public HlxCacheProvenance Cache { get; init; } = new();

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>Normalized paging request for CLI list commands.</summary>
public sealed record HlxPageRequest
{
    public bool All { get; init; }
    public int Offset { get; init; }
    public int? Limit { get; init; }

    public int EffectiveLimit(int defaultLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(defaultLimit, 0);
        return Limit ?? defaultLimit;
    }
}

/// <summary>List paging metadata independent of the result payload type.</summary>
public sealed record HlxPageInfo(
    int Returned,
    int? Total,
    int Offset,
    int? Limit,
    bool Complete,
    bool Truncated,
    HlxNextPage? Next);

/// <summary>Coordinates for the next page.</summary>
public sealed record HlxNextPage(
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit);

/// <summary>Cache keys relevant to a paged CLI result.</summary>
public sealed record HlxCacheProvenance
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("completeKey")]
    public string? CompleteKey { get; init; }
}
