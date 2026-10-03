using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using HelixTool.Core.AzDO;
using HelixTool.Core.Delivery;

namespace HelixTool.Mcp.Tools;

/// <summary>
/// Shared MCP presentation-budget helper (section 2.1/2.1a of the P0 design). Core owns filtering,
/// expansion, and page computation (<see cref="AzdoTimelineProjector"/>); this type only measures
/// the actual serialized <see cref="CallToolResult"/> and decides whether a smaller requested page
/// still fits the shaping target, or whether the complete requested representation must be
/// materialized to a verified evidence file instead. It never turns a large request into a
/// size-based domain error — it only changes delivery.
/// </summary>
internal static class McpPresentationBudget
{
    /// <summary>Bytes reserved for JSON-RPC/transport framing on top of the measured CallToolResult.</summary>
    public const int FramingReserveBytes = 256;

    /// <summary>
    /// Re-invokes <paramref name="project"/> with a shrinking row window until the serialized
    /// result fits <c>maxResponseBytes - FramingReserveBytes</c>, or a single row is reached.
    /// If even one row does not fit, the complete requested page is materialized to a cache-backed
    /// evidence file and a minimal (possibly zero-row) receipt with its delivery descriptor is returned.
    /// </summary>
    public static (TimelineProjectionResult Result, EvidenceDescriptor? Delivery) ShapeTimeline(
        TimelineProjectionRequest baseRequest,
        Func<TimelineProjectionRequest, TimelineProjectionResult> project,
        IEvidenceDeliveryStore evidenceStore,
        string cachePartition,
        string? backingCacheKey)
    {
        var budget = Math.Max(2048, baseRequest.MaxResponseBytes) - FramingReserveBytes;

        // Explicit full/all/single-record requests bypass shaping entirely: deliver the complete
        // requested representation via a verified evidence file rather than ever silently shrinking
        // rows to satisfy an inline target. The inline response stays a small metadata receipt when
        // the full representation itself would not fit — never a duplicate huge wire payload.
        if (baseRequest.All || (baseRequest.RecordId is not null && baseRequest.Projection.Equals("full", StringComparison.OrdinalIgnoreCase)))
        {
            var fullResult = project(baseRequest);
            if (Measure(fullResult) <= budget)
                return (fullResult, null);

            var bypassDescriptor = MaterializeToEvidence(fullResult, evidenceStore, cachePartition, backingCacheKey);
            return (ClearRows(fullResult) with { Delivery = bypassDescriptor }, bypassDescriptor);
        }

        // Do not clamp an invalid caller-supplied limit here — Project() must see and reject it
        // (e.g. limit<=0 is a real validation error, not something shaping should silently fix).
        var limit = baseRequest.Limit;
        TimelineProjectionResult? best = null;
        while (limit >= 1)
        {
            var candidate = project(baseRequest with { Limit = limit });
            if (Measure(candidate) <= budget)
                return (candidate, null);

            best = candidate;
            if (limit == 1) break;
            // Fine-grained decrement near the actually-selected count avoids skipping past a row
            // count that would have fit (e.g. halving straight past 7 to 6 when 7 already fits).
            limit = limit > 16 ? Math.Max(1, limit / 2) : limit - 1;
        }

        // Even a single row does not fit. For triage, shrink preview shaping before giving up on an
        // inline page (compact/full/summary rows don't use previewChars/previewIssueLimit, so
        // attempting this would only mutate the view's own fingerprint without changing its bytes).
        var unshrunkBest = best;
        if (baseRequest.Projection.Equals("triage", StringComparison.OrdinalIgnoreCase))
        {
            var shrinkChars = baseRequest.PreviewChars;
            var shrinkPreviews = baseRequest.PreviewIssueLimit;
            while ((shrinkChars > 40 || shrinkPreviews > 1) && best is not null)
            {
                shrinkChars = Math.Max(40, shrinkChars / 2);
                shrinkPreviews = Math.Max(1, shrinkPreviews - 1);
                var candidate = project(baseRequest with { Limit = 1, PreviewChars = shrinkChars, PreviewIssueLimit = shrinkPreviews });
                if (Measure(candidate) <= budget)
                    return (candidate, null);
                best = candidate;
                if (shrinkChars <= 40 && shrinkPreviews <= 1) break;
            }
        }

        // A single row carrying real content (envelope/counts/viewId overhead, SDK text+structured
        // duplication, and conservative \uXXXX quote escaping) can legitimately land a little over
        // an aggressively tight requested target when it is part of ordinary multi-row pagination
        // (selectedTotal > 1): a zero-row page is explicitly disallowed by the access-first contract
        // ("never a self-repeating zero-row page"), and the caller already has `next`/`continuation`
        // to keep reading, so a one-row page that is only moderately over budget is still the right
        // answer — truncated=true already reports this honestly.
        //
        // That tolerance must NOT apply to a unique exact-match single-row selection (selectedTotal
        // == 1, e.g. an explicit `name`/`recordId` selector): there is no "next page" concept there,
        // so any amount of over-budget content is exactly the oversized-single-field case the design
        // routes to a verified evidence file, not an inline accept. Materializing a zero-row receipt
        // with a delivery pointer is a complete, lossless, non-repeating response in that case — not
        // the disallowed "self-repeating zero-row page" (there is nothing left to repeat).
        const int MaxAcceptableOverageBytes = 1024;
        var allowOverage = (unshrunkBest?.SelectedTotal ?? best?.SelectedTotal ?? 0) > 1;
        var accepted = allowOverage && unshrunkBest is not null && Measure(unshrunkBest) <= budget + MaxAcceptableOverageBytes
            ? unshrunkBest
            : best;
        if (allowOverage && accepted is not null && accepted.Returned >= 1 && Measure(accepted) <= budget + MaxAcceptableOverageBytes)
            return (accepted, null);

        // Last resort: materialize the complete originally-requested page/set to a verified evidence
        // file rather than silently emitting a many-times-over-budget single field inline. The
        // delivery descriptor itself (evidenceId/path/sha256/etc) is included in this final
        // measurement, since it rides along on the same wire response.
        var completeResult = project(baseRequest);
        var evidence = MaterializeToEvidence(completeResult, evidenceStore, cachePartition, backingCacheKey);
        return (ClearRows(completeResult) with { Delivery = evidence }, evidence);
    }

    private static TimelineProjectionResult ClearRows(TimelineProjectionResult result) => result with
    {
        TriageRows = result.TriageRows is null ? null : [],
        CompactRows = result.CompactRows is null ? null : [],
        FullRecords = result.FullRecords is null ? null : [],
        Returned = 0
    };

    private static EvidenceDescriptor MaterializeToEvidence(
        TimelineProjectionResult result,
        IEvidenceDeliveryStore evidenceStore,
        string cachePartition,
        string? backingCacheKey)
    {
        var json = JsonSerializer.Serialize(result, McpJsonOptions);
        return evidenceStore.PutText(json, "application/json", result.ViewId, backingCacheKey, cachePartition);
    }

    private static int Measure(TimelineProjectionResult result) => MeasureAny(result);

    /// <summary>Measures the actual wire bytes of a <see cref="CallToolResult"/> wrapping <paramref name="payload"/>, matching SDK text+structured duplication/escaping/framing.</summary>
    public static int MeasureAny<T>(T payload)
    {
        var structured = JsonSerializer.SerializeToElement(payload, McpJsonOptions);
        var text = JsonSerializer.Serialize(payload, McpJsonOptions);
        var callResult = new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured
        };
        return JsonSerializer.SerializeToUtf8Bytes(callResult, McpJsonUtilities.DefaultOptions).Length;
    }

    private static readonly JsonSerializerOptions McpJsonOptions = McpJsonUtilities.DefaultOptions;
}
