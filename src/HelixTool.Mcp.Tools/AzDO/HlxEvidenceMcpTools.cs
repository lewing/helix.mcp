using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;

namespace HelixTool.Mcp.Tools;

/// <summary>
/// Shared read-only evidence reader (design section 2.1a). Reads an hlx-owned evidence reference
/// produced by another tool (for example an oversized <c>azdo_timeline</c> page or record) in
/// bounded text or explicitly requested base64 chunks. Continue with <c>next</c>; full payloads
/// remain available through repeated chunked reads. Access is scoped to the originating
/// credential/cache partition — this is not an arbitrary filesystem reader.
/// </summary>
[McpServerToolType]
public sealed class HlxEvidenceMcpTools
{
    private readonly IEvidenceDeliveryStore _store;
    private readonly CacheOptions _cacheOptions;

    public HlxEvidenceMcpTools(IEvidenceDeliveryStore store, CacheOptions cacheOptions)
    {
        _store = store;
        _cacheOptions = cacheOptions;
    }

    [McpServerTool(Name = "hlx_read_evidence", Title = "Read hlx Evidence Reference", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
     Description("Read an hlx-owned evidence reference in bounded text or explicitly requested base64 chunks. Continue with next; full payloads remain available through file delivery. Access is scoped to the originating credential/cache partition; this is not an arbitrary filesystem reader.")]
    public EvidenceReadResult ReadEvidence(
        [Description("Opaque evidence reference id returned by another tool's 'delivery' field")] string evidenceId,
        [Description("Starting byte offset. Default: 0")] long offsetBytes = 0,
        [Description("Requested chunk size in bytes; no policy maximum, larger explicit values remain valid")] long lengthBytes = 2048,
        [Description("auto (default, resolves from media type), utf8, or base64"), AllowedValues("auto", "utf8", "base64")] string encoding = "auto",
        [Description("Inline shaping target for this one read; does not change totalBytes/sourceComplete. Default: 12288.")] long maxResponseBytes = 12_288)
    {
        var cachePartition = _cacheOptions.AuthTokenHash ?? "public";
        var budget = Math.Max(2048, maxResponseBytes) - 256;
        var originallyRequested = Math.Max(1, lengthBytes);

        try
        {
            var requested = originallyRequested;
            EvidenceReadResult? last = null;
            while (requested >= 1)
            {
                var candidate = _store.Read(evidenceId, offsetBytes, requested, encoding, cachePartition);
                if (McpPresentationBudget.MeasureAny(candidate) <= budget || requested == 1)
                    return FinalizeRangeCompleteness(candidate, originallyRequested);
                last = candidate;
                requested = requested / 2 == requested ? requested - 1 : Math.Max(1, requested / 2);
            }
            return FinalizeRangeCompleteness(last!, originallyRequested);
        }
        catch (EvidenceNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// <see cref="IEvidenceDeliveryStore.Read"/> computes rangeComplete against whatever
    /// (possibly shaping-shrunk) lengthBytes it was actually called with; this restores the
    /// caller's true intent — whether the originally requested range was fully satisfied.
    /// </summary>
    private static EvidenceReadResult FinalizeRangeCompleteness(EvidenceReadResult result, long originallyRequestedLength)
    {
        var trueRangeComplete = (result.OffsetBytes + result.ReturnedBytes) >= result.TotalBytes
            || result.ReturnedBytes >= originallyRequestedLength;
        return result with { RangeComplete = trueRangeComplete };
    }
}
