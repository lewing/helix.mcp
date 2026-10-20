using System.Collections;
using System.Text.Json;
using HelixTool.Core.Cache;

namespace HelixTool.Core.AzDO;

internal sealed record AzdoCachedPage(string Key, int Count, string Sha256);
internal sealed record AzdoPagedEvidence(bool PagedList, bool SourceComplete, IReadOnlyList<AzdoCachedPage> Pages)
{
    public int Total => Pages.Sum(p => p.Count);
}

internal sealed class AzdoPagedEvidenceList<T>(ICacheStore cache, string completeKey, AzdoPagedEvidence index) : IReadOnlyList<T>
{
    public int Count => index.Total;
    private IReadOnlyList<T> Read(AzdoCachedPage page)
    {
        var prefix = completeKey[..^3] + "page:";
        if (!page.Key.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Paged evidence key is outside its source partition.");
        var json = cache.GetMetadataIgnoringTtlAsync(page.Key).GetAwaiter().GetResult()
            ?? throw new InvalidDataException("Paged evidence is missing a backing page.");
        if (AzdoBuildQueries.Digest(json) != page.Sha256) throw new InvalidDataException("Paged evidence hash verification failed.");
        var rows = JsonSerializer.Deserialize<T[]>(json, AzdoBuildQueries.JsonOptions)!;
        if (rows.Length != page.Count) throw new InvalidDataException("Paged evidence count verification failed.");
        return rows;
    }
    public T this[int position]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(position);
            foreach (var page in index.Pages)
            {
                if (position < page.Count) return Read(page)[position];
                position -= page.Count;
            }
            throw new ArgumentOutOfRangeException(nameof(position));
        }
    }
    public IEnumerator<T> GetEnumerator()
    {
        foreach (var page in index.Pages)
            foreach (var row in Read(page)) yield return row;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
