using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using HelixTool.Core.AzDO;
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
    private readonly IAzdoTokenAccessor? _tokenAccessor;

    public HlxEvidenceMcpTools(IEvidenceDeliveryStore store, CacheOptions cacheOptions, IAzdoTokenAccessor? tokenAccessor = null)
    {
        _store = store;
        _cacheOptions = cacheOptions;
        _tokenAccessor = tokenAccessor;
    }

    [McpServerTool(Name = "hlx_read_evidence", Title = "Read hlx Evidence Reference", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
     Description("Read an hlx-owned evidence reference in bounded text or explicitly requested base64 chunks. Continue with next; full payloads remain available through file delivery. Access is scoped to the originating credential/cache partition; this is not an arbitrary filesystem reader.")]
    public async Task<EvidenceReadResult> ReadEvidence(
        [Description("Opaque evidence reference id returned by another tool's 'delivery' field")] string evidenceId,
        [Description("Starting byte offset. Default: 0")] long offsetBytes = 0,
        [Description("Requested chunk size in bytes; no policy maximum, larger explicit values remain valid")] long lengthBytes = 2048,
        [Description("auto (default, resolves from media type), utf8, or base64"), AllowedValues("auto", "utf8", "base64")] string encoding = "auto",
        [Description("Inline shaping target for this one read; does not change totalBytes/sourceComplete. Clamped to 8192..16384. Default: 12288.")] long maxResponseBytes = 12_288)
    {
        if (offsetBytes < 0)
            throw new McpException("offsetBytes must be nonnegative.");
        if (lengthBytes <= 0)
            throw new McpException("lengthBytes must be positive.");
        if (maxResponseBytes <= 0)
            throw new McpException("maxResponseBytes must be positive.");

        var cachePartition = await ResolveCachePartitionAsync(default);
        var effectiveMaxResponseBytes = Math.Clamp(maxResponseBytes, 8_192, 16_384);
        var budget = effectiveMaxResponseBytes - 256;
        var originallyRequested = lengthBytes;

        try
        {
            var requested = originallyRequested;
            EvidenceReadResult? last = null;
            while (requested >= 1)
            {
                var candidate = _store.Read(evidenceId, offsetBytes, requested, encoding, cachePartition);
                if (McpPresentationBudget.MeasureAny(candidate) <= budget || requested == 1)
                    return Finalize(candidate, originallyRequested, maxResponseBytes, effectiveMaxResponseBytes);
                last = candidate;
                requested = requested / 2 == requested ? requested - 1 : Math.Max(1, requested / 2);
            }
            return Finalize(last!, originallyRequested, maxResponseBytes, effectiveMaxResponseBytes);
        }
        catch (EvidenceNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// Resolves the same per-request AzDO auth-context partition that <see cref="CachingAzdoApiClient"/>
    /// would have established on <see cref="_cacheOptions"/> for the originating tool call. A follow-up
    /// <c>hlx_read_evidence</c> call frequently runs in its own freshly-scoped <see cref="CacheOptions"/>
    /// instance (e.g. a separate HTTP request) whose <see cref="CacheOptions.AuthTokenHash"/> was never
    /// populated — trusting it directly would silently fall back to the public partition and fail to
    /// find authenticated-partition evidence. Resolving independently here matches the identity the
    /// originating authenticated call actually used, instead of depending on scope-sharing.
    /// </summary>
    private async Task<string> ResolveCachePartitionAsync(CancellationToken ct)
    {
        if (_cacheOptions.EvalMode || _tokenAccessor is null)
            return _cacheOptions.AuthTokenHash ?? "public";

        var credential = await _tokenAccessor.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var authContext = credential?.CacheIdentity ?? credential?.Source;
        // Mirror CachingAzdoApiClient.EnsureAuthTokenHashAsync's side effect: populate this
        // request-scoped CacheOptions' own AuthTokenHash, not just a locally-computed partition
        // string, so anything else in this scope that reads CacheOptions.AuthTokenHash directly
        // (cache-key building, diagnostics, etc.) sees the same resolved identity.
        _cacheOptions.UpdateAuthContext(authContext);
        return _cacheOptions.AuthTokenHash ?? "public";
    }

    /// <summary>
    /// <see cref="IEvidenceDeliveryStore.Read"/> computes rangeComplete against whatever
    /// (possibly shaping-shrunk) lengthBytes it was actually called with; this restores the
    /// caller's true intent — whether the originally requested range was fully satisfied. Also
    /// reports the requested/effective maxResponseBytes pair (Dallas P0-1 review item 1) and carries
    /// the effective value into the continuation's own arguments, so replaying `next` keeps the same
    /// shaping target rather than silently re-defaulting to 12288 on the next hop.
    /// </summary>
    private static EvidenceReadResult Finalize(EvidenceReadResult result, long originallyRequestedLength, long requestedMaxResponseBytes, long effectiveMaxResponseBytes)
    {
        var trueRangeComplete = (result.OffsetBytes + result.ReturnedBytes) >= result.TotalBytes
            || result.ReturnedBytes >= originallyRequestedLength;
        var next = result.Next;
        if (next is not null)
        {
            var args = new Dictionary<string, object?>(next.Arguments) { ["maxResponseBytes"] = effectiveMaxResponseBytes };
            next = next with { Arguments = args };
        }
        return result with
        {
            RangeComplete = trueRangeComplete,
            MaxResponseBytes = effectiveMaxResponseBytes,
            RequestedMaxResponseBytes = requestedMaxResponseBytes,
            Next = next
        };
    }
}
