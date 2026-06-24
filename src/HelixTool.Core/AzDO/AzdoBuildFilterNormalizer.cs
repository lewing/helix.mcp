namespace HelixTool.Core.AzDO;

/// <summary>
/// Single canonical chokepoint for normalizing <see cref="AzdoBuildFilter"/> values.
/// <para>
/// Both <c>AzdoApiClient.ListBuildsAsync</c> (before URL construction) and
/// <c>CachingAzdoApiClient.HashFilter</c> (before cache-key derivation) delegate here.
/// Neither layer re-implements the rules.
/// </para>
/// Rules applied, in order:
/// <list type="bullet">
///   <item>All string fields: <c>IsNullOrWhiteSpace</c> → <see langword="null"/>; otherwise <c>Trim()</c>.</item>
///   <item><c>QueryOrder</c>: after trim, if equal (case-insensitive) to <see cref="AzdoBuildFilterDefaults.QueryOrder"/>, collapse to <see langword="null"/>; otherwise lowercase.</item>
///   <item>Returns a new record (<c>with</c> expression — originals are immutable).</item>
/// </list>
/// </summary>
public static class AzdoBuildFilterNormalizer
{
    /// <summary>
    /// Returns a fully normalized copy of <paramref name="filter"/>.
    /// The original is not modified.
    /// </summary>
    public static AzdoBuildFilter Normalize(AzdoBuildFilter filter) =>
        filter with
        {
            PrNumber = NormalizeString(filter.PrNumber),
            Branch = NormalizeString(filter.Branch),
            StatusFilter = NormalizeString(filter.StatusFilter),
            QueryOrder = NormalizeQueryOrder(filter.QueryOrder),
        };

    private static string? NormalizeString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeQueryOrder(string? value)
    {
        var trimmed = NormalizeString(value);
        if (trimmed is null)
            return null;
        // Collapse explicit server default to null: null and "queueTimeDescending" are semantically identical.
        if (string.Equals(trimmed, AzdoBuildFilterDefaults.QueryOrder, StringComparison.OrdinalIgnoreCase))
            return null;
        // Lowercase so different casings of the same value share a canonical form (AzDO is case-insensitive).
        return trimmed.ToLowerInvariant();
    }
}
