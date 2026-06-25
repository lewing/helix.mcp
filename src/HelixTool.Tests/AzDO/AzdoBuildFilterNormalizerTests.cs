// Direct unit tests for AzdoBuildFilterNormalizer — every rule × every field.
// These are the canonical rule tests; per-layer tests become "delegates to normalizer" smokes.

using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoBuildFilterNormalizerTests
{
    // ── Null/whitespace → null (string fields: PrNumber, Branch, StatusFilter) ─

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Normalize_NullOrWhitespacePrNumber_ReturnsNull(string? value)
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { PrNumber = value });
        Assert.Null(result.PrNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Normalize_NullOrWhitespaceBranch_ReturnsNull(string? value)
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { Branch = value });
        Assert.Null(result.Branch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Normalize_NullOrWhitespaceStatusFilter_ReturnsNull(string? value)
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { StatusFilter = value });
        Assert.Null(result.StatusFilter);
    }

    // ── QueryOrder: null/whitespace → null ───────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Normalize_NullOrWhitespaceQueryOrder_ReturnsNull(string? value)
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { QueryOrder = value });
        Assert.Null(result.QueryOrder);
    }

    // ── QueryOrder: explicit server default collapses to null (case-insensitive) ─

    [Theory]
    [InlineData("queueTimeDescending")]
    [InlineData("QUEUETIMEDESCENDING")]
    [InlineData("queuetimedescending")]
    [InlineData("QueueTimeDescending")]
    [InlineData("  queueTimeDescending  ")]
    public void Normalize_ExplicitDefaultQueryOrder_CollapsesToNull(string value)
    {
        // null and "queueTimeDescending" are semantically identical — AzDO default.
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { QueryOrder = value });
        Assert.Null(result.QueryOrder);
    }

    // ── QueryOrder: non-default values are lowercased ────────────────────────

    [Theory]
    [InlineData("finishTimeDescending", "finishtimedescending")]
    [InlineData("FINISHTIMEDESCENDING", "finishtimedescending")]
    [InlineData("FinishTimeDescending", "finishtimedescending")]
    [InlineData("startTimeAscending", "starttimeascending")]
    [InlineData("STARTTIMEASCENDING", "starttimeascending")]
    [InlineData("  finishTimeDescending  ", "finishtimedescending")]
    public void Normalize_NonDefaultQueryOrder_IsLowercased(string input, string expected)
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { QueryOrder = input });
        Assert.Equal(expected, result.QueryOrder);
    }

    // ── Trim: string fields have leading/trailing whitespace removed ─────────

    [Theory]
    [InlineData("  refs/heads/main  ", "refs/heads/main")]
    [InlineData("main", "main")]
    [InlineData("refs/heads/feature/my-feature", "refs/heads/feature/my-feature")]
    public void Normalize_Branch_Trimmed(string input, string expected)
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { Branch = input });
        Assert.Equal(expected, result.Branch);
    }

    [Theory]
    [InlineData("  42  ", "42")]
    [InlineData("12345", "12345")]
    public void Normalize_PrNumber_Trimmed(string input, string expected)
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { PrNumber = input });
        Assert.Equal(expected, result.PrNumber);
    }

    [Theory]
    [InlineData("  inProgress  ", "inProgress")]
    [InlineData("completed", "completed")]
    [InlineData("  failed  ", "failed")]
    public void Normalize_StatusFilter_TrimmedNotLowercased(string input, string expected)
    {
        // StatusFilter is trimmed but NOT lowercased — AzDO treats it case-sensitively.
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { StatusFilter = input });
        Assert.Equal(expected, result.StatusFilter);
    }

    // ── All string fields null → all null ────────────────────────────────────

    [Fact]
    public void Normalize_AllStringFieldsNull_ReturnsAllNull()
    {
        var result = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter());
        Assert.Null(result.PrNumber);
        Assert.Null(result.Branch);
        Assert.Null(result.StatusFilter);
        Assert.Null(result.QueryOrder);
    }

    // ── Non-string fields pass through unchanged ─────────────────────────────

    [Fact]
    public void Normalize_NonStringFields_Passthrough()
    {
        var minTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maxTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var filter = new AzdoBuildFilter { DefinitionId = 777, Top = 50, MinTime = minTime, MaxTime = maxTime };

        var result = AzdoBuildFilterNormalizer.Normalize(filter);

        Assert.Equal(777, result.DefinitionId);
        Assert.Equal(50, result.Top);
        Assert.Equal(minTime, result.MinTime);
        Assert.Equal(maxTime, result.MaxTime);
    }

    // ── Idempotency: Normalize(Normalize(x)) == Normalize(x) ────────────────

    [Theory]
    [InlineData("  42  ", "  refs/heads/main  ", "  inProgress  ", "finishTimeDescending")]
    [InlineData("100", "develop", "completed", "FINISHTIMEDESCENDING")]
    [InlineData(null, null, null, null)]
    [InlineData("", "  ", "\t", "  queueTimeDescending  ")]
    public void Normalize_IsIdempotent(string? prNumber, string? branch, string? statusFilter, string? queryOrder)
    {
        var filter = new AzdoBuildFilter
        {
            PrNumber = prNumber,
            Branch = branch,
            StatusFilter = statusFilter,
            QueryOrder = queryOrder
        };

        var once = AzdoBuildFilterNormalizer.Normalize(filter);
        var twice = AzdoBuildFilterNormalizer.Normalize(once);

        Assert.Equal(once, twice);
    }

    // ── Immutability: returns new record; original is unchanged ──────────────

    [Fact]
    public void Normalize_ReturnsNewRecord_OriginalUnchanged()
    {
        var original = new AzdoBuildFilter
        {
            PrNumber = "  42  ",
            Branch = "  main  ",
            StatusFilter = "  completed  ",
            QueryOrder = "finishTimeDescending"
        };

        var result = AzdoBuildFilterNormalizer.Normalize(original);

        // Original values are unchanged.
        Assert.Equal("  42  ", original.PrNumber);
        Assert.Equal("  main  ", original.Branch);
        Assert.Equal("  completed  ", original.StatusFilter);
        Assert.Equal("finishTimeDescending", original.QueryOrder);

        // Result is a distinct, normalized instance.
        Assert.NotSame(original, result);
        Assert.Equal("42", result.PrNumber);
        Assert.Equal("main", result.Branch);
        Assert.Equal("completed", result.StatusFilter);
        Assert.Equal("finishtimedescending", result.QueryOrder);
    }

    // ── Record equality: same logical filter → equal records ─────────────────

    [Fact]
    public void Normalize_SameLogicalFilter_ProducesEqualRecords()
    {
        // "finishTimeDescending" and "FINISHTIMEDESCENDING" are the same after normalization.
        var a = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { QueryOrder = "finishTimeDescending" });
        var b = AzdoBuildFilterNormalizer.Normalize(new AzdoBuildFilter { QueryOrder = "FINISHTIMEDESCENDING" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Normalize_DefaultAndNullQueryOrder_ProduceEqualRecords()
    {
        // Explicit "queueTimeDescending" collapses to null, same as an unset QueryOrder.
        var withDefault = AzdoBuildFilterNormalizer.Normalize(
            new AzdoBuildFilter { QueryOrder = "queueTimeDescending" });
        var withNull = AzdoBuildFilterNormalizer.Normalize(
            new AzdoBuildFilter { QueryOrder = null });
        Assert.Equal(withDefault, withNull);
    }
}
