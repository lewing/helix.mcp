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
public static class McpPresentationBudget
{
    /// <summary>Bytes reserved for JSON-RPC/transport framing on top of the measured CallToolResult.</summary>
    public const int FramingReserveBytes = 256;

    /// <summary>Lower clamp for the effective inline shaping target (Dallas P0-1 review item 1).</summary>
    public const long MinEffectiveMaxResponseBytes = 8_192;

    /// <summary>Upper clamp for the effective inline shaping target.</summary>
    public const long MaxEffectiveMaxResponseBytes = 16_384;

    /// <summary>Clamps a caller-requested maxResponseBytes into the supported shaping range.</summary>
    public static long ClampMaxResponseBytes(long requested) => Math.Clamp(requested, MinEffectiveMaxResponseBytes, MaxEffectiveMaxResponseBytes);

    /// <summary>
    /// Re-invokes <paramref name="project"/> with a shrinking row window until the serialized
    /// result fits <c>maxResponseBytes - FramingReserveBytes</c>, or a single row is reached.
    /// If even one row does not fit, the complete requested page is materialized to a cache-backed
    /// evidence file and a minimal (possibly zero-row) receipt with its delivery descriptor is returned.
    /// The returned <see cref="TimelineProjectionResult"/> is always the complete, final wire shape —
    /// Delivery/Continuation/Note (when present) are attached before the accept-vs-materialize
    /// decision measures it, never bolted on afterward unmeasured.
    ///
    /// The second tuple element is the actual backing-selection logical completeness (row
    /// `selectedTotal == returned` at offset 0, or `issueWindow.issueComplete` for an issue-window
    /// request) computed from the projector's own pre-receipt result — never inferred from which
    /// delivery mode was used. A materialized/file-delivered receipt always reports its own
    /// `Complete=false` (the small receipt's own row count, not the backing data), so callers that
    /// need to know whether the *backing selection* was actually complete (e.g. the CLI's exit
    /// code) must use this value, not <see cref="TimelineProjectionResult.Complete"/>.
    /// </summary>
    public static (TimelineProjectionResult Result, bool LogicallyComplete) ShapeTimeline(
        TimelineProjectionRequest baseRequest,
        Func<TimelineProjectionRequest, TimelineProjectionResult> project,
        IEvidenceDeliveryStore evidenceStore,
        string cachePartition,
        string? backingCacheKey)
    {
        var budget = ClampMaxResponseBytes(baseRequest.MaxResponseBytes) - FramingReserveBytes;

        // Explicit full/all/single-record requests bypass shaping entirely: deliver the complete
        // requested representation via a verified evidence file rather than ever silently shrinking
        // rows to satisfy an inline target. The inline response stays a small metadata receipt when
        // the full representation itself would not fit — never a duplicate huge wire payload.
        if (baseRequest.All || (baseRequest.RecordId is not null && baseRequest.Projection.Equals("full", StringComparison.OrdinalIgnoreCase)))
        {
            var fullResult = project(baseRequest);

            // delivery="file"/"chunked" is a deliberate caller choice of delivery *mechanism*, not
            // merely an overflow fallback — it must always materialize to a verified evidence file,
            // even when the complete selection happens to be small enough to fit inline. Only
            // "auto"/"inline" (or the legacy "all" string) try the inline representation first.
            var explicitFileDelivery = baseRequest.Delivery.Equals("file", StringComparison.OrdinalIgnoreCase)
                || baseRequest.Delivery.Equals("chunked", StringComparison.OrdinalIgnoreCase);
            if (!explicitFileDelivery && Measure(fullResult) <= budget)
                return (fullResult, IsLogicallyComplete(fullResult));

            return (MaterializeReceipt(fullResult, baseRequest, evidenceStore, cachePartition, backingCacheKey), IsLogicallyComplete(fullResult));
        }

        // Do not clamp an invalid caller-supplied limit here — Project() must see and reject it
        // (e.g. limit<=0 is a real validation error, not something shaping should silently fix).
        var limit = baseRequest.Limit;
        TimelineProjectionResult? best = null;
        while (limit >= 1)
        {
            var candidate = project(baseRequest with { Limit = limit });
            if (Measure(candidate) <= budget)
                return (candidate, IsLogicallyComplete(candidate));

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
                    return (candidate, IsLogicallyComplete(candidate));
                best = candidate;
                if (shrinkChars <= 40 && shrinkPreviews <= 1) break;
            }
        }

        // A one-row page can legitimately land a little over an aggressively tight requested target
        // when the row itself is ordinary-sized and the overage is attributable to envelope/counts/
        // viewId bulk (a zero-row materialized receipt carries that same bulk, so it would not be any
        // smaller or more useful) — a zero-row page is explicitly disallowed by the access-first
        // contract ("never a self-repeating zero-row page"), and the caller already has
        // `next`/`continuation` to keep reading, so returning the one row is strictly better.
        //
        // That tolerance must NOT apply when the row's own incremental cost is itself large: a normal
        // row (even a triage row with full previews) costs at most a few KB, so a row costing much
        // more than that is dominated by one outlier field (e.g. an oversized `name`) — exactly the
        // case the design routes to a verified evidence file instead of an inline accept, regardless
        // of how close the bare envelope is to the budget or how many rows are selected in total.
        const int MaxAcceptableOverageBytes = 1024;
        const int MaxNormalRowBytes = 2048;
        var referenceForEnvelope = unshrunkBest ?? best;
        var envelopeOnlyBytes = referenceForEnvelope is null ? long.MaxValue : Measure(ClearRows(referenceForEnvelope));
        var singleRowBytes = unshrunkBest is null ? long.MaxValue : Measure(unshrunkBest) - envelopeOnlyBytes;
        var allowOverage = singleRowBytes <= MaxNormalRowBytes;
        var accepted = allowOverage && unshrunkBest is not null && Measure(unshrunkBest) <= budget + MaxAcceptableOverageBytes
            ? unshrunkBest
            : best;
        if (allowOverage && accepted is not null && accepted.Returned >= 1 && Measure(accepted) <= budget + MaxAcceptableOverageBytes)
            return (accepted, IsLogicallyComplete(accepted));

        // Last resort: materialize the complete originally-requested page/set to a verified evidence
        // file rather than silently emitting a many-times-over-budget single field inline.
        var completeResult = project(baseRequest);
        return (MaterializeReceipt(completeResult, baseRequest, evidenceStore, cachePartition, backingCacheKey), IsLogicallyComplete(completeResult));
    }

    /// <summary>
    /// The single source of truth for whether the BACKING selection (not the wire receipt) is
    /// actually complete — computed once, directly from the projector's own pre-receipt
    /// <see cref="TimelineProjectionResult.Complete"/>, never inferred from request flags
    /// (`all`/`delivery`/`recordId`/`--issue-*`) or which delivery mode was used.
    ///
    /// <see cref="TimelineProjectionResult.Complete"/> already *is* this combined truth: Core sets
    /// it to `issueComplete` for an issue-window request (or the ordinary
    /// `offset==0 && returned>=selectedTotal` row rule otherwise), and then unconditionally forces
    /// it to `false` whenever the timeline graph itself is a malformed/best-effort reconstruction
    /// — regardless of which branch produced the value first. An earlier version of this method
    /// preferred `IssueWindow.IssueComplete` *instead of* `Complete` whenever an issue window was
    /// present, which incorrectly dropped that graph-validity override: a record with all of its
    /// issues present (`issueComplete=true`) but reached through a broken ancestor chain
    /// (`Complete` forced `false`) was wrongly reported as logically complete. `Complete` alone is
    /// never a partial view of the truth — it already *is* the AND of both signals.
    /// </summary>
    public static bool IsLogicallyComplete(TimelineProjectionResult result) => result.Complete;

    /// <summary>
    /// Builds the complete final materialize-to-evidence receipt — descriptor, continuation, and
    /// note all attached — and re-measures that exact object (framing-inclusive), matching what
    /// actually goes over the wire. Earlier revisions measured only the pre-receipt candidate when
    /// deciding to materialize and bolted Continuation/Note on afterward, unmeasured.
    /// </summary>
    private static TimelineProjectionResult MaterializeReceipt(
        TimelineProjectionResult result,
        TimelineProjectionRequest baseRequest,
        IEvidenceDeliveryStore evidenceStore,
        string cachePartition,
        string? backingCacheKey)
    {
        var evidence = MaterializeToEvidence(result, evidenceStore, cachePartition, backingCacheKey);
        var continuation = new TimelineAction
        {
            Tool = "hlx_read_evidence",
            Arguments = new Dictionary<string, object?>
            {
                ["evidenceId"] = evidence.EvidenceId,
                ["offsetBytes"] = 0,
                ["lengthBytes"] = 65536,
                ["encoding"] = "auto"
            }
        };
        var fullNote = $"Requested payload exceeded the {baseRequest.MaxResponseBytes}-byte inline shaping target; the complete selection was written to a verified evidence file. " +
                       $"Read it with hlx_read_evidence(evidenceId='{evidence.EvidenceId}', offsetBytes=0, lengthBytes=65536).";
        var cleared = ClearRows(result);
        var receipt = cleared with { Complete = false, Truncated = true, Delivery = evidence, Continuation = continuation, Note = fullNote };

        // The descriptor/continuation/note themselves ride the same wire response; re-measuring the
        // fully-formed receipt is the actual accept/no-further-shrink decision point. Unlike the
        // earlier no-op measurement, this is now enforced: the free-text Note is the only droppable
        // field (descriptor provenance and the continuation action itself are required, not
        // optional bulk), so a receipt still over budget drops to a terse Note, then no Note at all,
        // before accepting whatever remains as the irreducible floor.
        var budget = ClampMaxResponseBytes(baseRequest.MaxResponseBytes) - FramingReserveBytes;
        if (Measure(receipt) <= budget)
            return receipt;

        var terseNote = $"Oversized; see delivery.evidenceId='{evidence.EvidenceId}'.";
        var terseReceipt = cleared with { Complete = false, Truncated = true, Delivery = evidence, Continuation = continuation, Note = terseNote };
        if (Measure(terseReceipt) <= budget)
            return terseReceipt;

        return cleared with { Complete = false, Truncated = true, Delivery = evidence, Continuation = continuation, Note = null };
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
        var json = JsonSerializer.Serialize(result, ProductionToolJsonOptions);
        return evidenceStore.PutText(json, "application/json", result.ViewId, backingCacheKey, cachePartition);
    }

    private static int Measure(TimelineProjectionResult result) => MeasureAny(result);

    /// <summary>Measures the actual wire bytes of a <see cref="CallToolResult"/> wrapping <paramref name="payload"/>, matching SDK text+structured duplication/escaping/framing.</summary>
    public static int MeasureAny<T>(T payload)
    {
        var structured = JsonSerializer.SerializeToElement(payload, ProductionToolJsonOptions);
        var text = JsonSerializer.Serialize(payload, ProductionToolJsonOptions);
        var callResult = new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured
        };
        return JsonSerializer.SerializeToUtf8Bytes(callResult, McpJsonUtilities.DefaultOptions).Length;
    }

    /// <summary>
    /// The exact <see cref="JsonSerializerOptions"/> both production hosts (<c>hlx mcp</c> and the
    /// standalone <c>HelixTool.Mcp</c> ASP.NET host) register via <c>WithToolsFromAssembly</c>.
    /// Unlike <see cref="McpJsonUtilities.DefaultOptions"/> (which omits null properties), this
    /// bare options object has no <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> set, so
    /// ordinary nullable row fields (<c>monitorEvidence</c>, <c>parentId</c>, <c>contextParentId</c>,
    /// <c>log</c>, ...) are emitted as literal <c>null</c> on the real wire. Measuring shaping
    /// decisions with <see cref="McpJsonUtilities.DefaultOptions"/> instead previously
    /// under-counted every row with any null field, so a page that measured under budget in the
    /// shaping helper could still exceed it on the real transport. Both hosts reference this single
    /// definition directly (rather than constructing their own copy) so they can never drift from
    /// what shaping actually measures.
    /// </summary>
    public static readonly JsonSerializerOptions ProductionToolJsonOptions = new()
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
}
