// Unit tests for AzdoService static filter helpers: NormalizeFilter, IsValidFilter, MatchesFilter.
// These are pure predicate tests — no HTTP mocking required.

using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoFilterPresetTests
{
    // ── NormalizeFilter — alias → canonical resolution ───────────────

    [Theory]
    [InlineData("inProgress",  "running")]
    [InlineData("in-progress", "running")]
    [InlineData("active",      "running")]
    [InlineData("notStarted",  "pending")]
    [InlineData("not-started", "pending")]
    public void NormalizeFilter_KnownAlias_ResolvesToCanonical(string alias, string expected)
    {
        Assert.Equal(expected, AzdoService.NormalizeFilter(alias));
    }

    [Theory]
    [InlineData("INPROGRESS",  "running")]
    [InlineData("IN-PROGRESS", "running")]
    [InlineData("ACTIVE",      "running")]
    [InlineData("NotStarted",  "pending")]
    [InlineData("NOT-STARTED", "pending")]
    public void NormalizeFilter_AliasIsCaseInsensitive(string alias, string expected)
    {
        Assert.Equal(expected, AzdoService.NormalizeFilter(alias));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("all")]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData("incomplete")]
    [InlineData("issues")]
    public void NormalizeFilter_CanonicalValue_PassesThrough(string canonical)
    {
        Assert.Equal(canonical, AzdoService.NormalizeFilter(canonical));
    }

    [Fact]
    public void NormalizeFilter_UnknownValue_PassesThroughUnchanged()
    {
        Assert.Equal("banana", AzdoService.NormalizeFilter("banana"));
    }

    // ── IsValidFilter ────────────────────────────────────────────────

    [Theory]
    [InlineData("failed")]
    [InlineData("all")]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData("incomplete")]
    [InlineData("issues")]
    public void IsValidFilter_CanonicalValues_ReturnTrue(string filter)
    {
        Assert.True(AzdoService.IsValidFilter(filter));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("inProgress")]  // alias, not canonical
    [InlineData("active")]      // alias, not canonical
    [InlineData("notStarted")]  // alias, not canonical
    [InlineData("")]
    public void IsValidFilter_InvalidOrAlias_ReturnFalse(string filter)
    {
        Assert.False(AzdoService.IsValidFilter(filter));
    }

    // ── MatchesFilter — 'failed' predicate ───────────────────────────

    [Fact]
    public void MatchesFilter_Failed_ResultFailed_ReturnsTrue()
    {
        var r = Record("completed", "failed");
        Assert.True(AzdoService.MatchesFilter(r, "failed"));
    }

    [Fact]
    public void MatchesFilter_Failed_ResultCanceled_ReturnsTrue()
    {
        // §7.3: result = 'canceled' → result != succeeded → matches 'failed'
        var r = Record("completed", "canceled");
        Assert.True(AzdoService.MatchesFilter(r, "failed"));
    }

    [Fact]
    public void MatchesFilter_Failed_ResultSucceeded_NoIssues_ReturnsFalse()
    {
        var r = Record("completed", "succeeded");
        Assert.False(AzdoService.MatchesFilter(r, "failed"));
    }

    [Fact]
    public void MatchesFilter_Failed_NullResult_NoIssues_ReturnsFalse()
    {
        // §7.1: pending record has result=null — must NOT match 'failed'
        var r = Record("pending", null);
        Assert.False(AzdoService.MatchesFilter(r, "failed"));
    }

    [Fact]
    public void MatchesFilter_Failed_NullResult_HasIssues_ReturnsTrue()
    {
        // Even a pending record matches 'failed' if it has issues
        var r = RecordWithIssues("pending", null);
        Assert.True(AzdoService.MatchesFilter(r, "failed"));
    }

    [Fact]
    public void MatchesFilter_Failed_SucceededWithIssues_ReturnsTrue()
    {
        // succeededWithIssues pattern: result=succeeded but issues.Count > 0
        var r = RecordWithIssues("completed", "succeeded");
        Assert.True(AzdoService.MatchesFilter(r, "failed"));
    }

    // ── MatchesFilter — 'all' predicate ─────────────────────────────

    [Theory]
    [InlineData("completed",  "succeeded")]
    [InlineData("completed",  "failed")]
    [InlineData("inProgress", null)]
    [InlineData("pending",    null)]
    public void MatchesFilter_All_AnyRecord_ReturnsTrue(string state, string? result)
    {
        var r = Record(state, result);
        Assert.True(AzdoService.MatchesFilter(r, "all"));
    }

    // ── MatchesFilter — 'running' predicate ─────────────────────────

    [Fact]
    public void MatchesFilter_Running_InProgressState_ReturnsTrue()
    {
        var r = Record("inProgress", null);
        Assert.True(AzdoService.MatchesFilter(r, "running"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("pending")]
    [InlineData(null)]
    public void MatchesFilter_Running_NonInProgressState_ReturnsFalse(string? state)
    {
        var r = Record(state, null);
        Assert.False(AzdoService.MatchesFilter(r, "running"));
    }

    // ── MatchesFilter — 'pending' predicate ─────────────────────────

    [Fact]
    public void MatchesFilter_Pending_PendingState_ReturnsTrue()
    {
        // §7.1: pending records have state=pending, result=null
        var r = Record("pending", null);
        Assert.True(AzdoService.MatchesFilter(r, "pending"));
    }

    [Theory]
    [InlineData("inProgress")]
    [InlineData("completed")]
    [InlineData(null)]
    public void MatchesFilter_Pending_NonPendingState_ReturnsFalse(string? state)
    {
        var r = Record(state, null);
        Assert.False(AzdoService.MatchesFilter(r, "pending"));
    }

    // ── MatchesFilter — 'incomplete' predicate ───────────────────────

    [Theory]
    [InlineData("inProgress")]
    [InlineData("pending")]
    public void MatchesFilter_Incomplete_NonCompletedState_ReturnsTrue(string state)
    {
        var r = Record(state, null);
        Assert.True(AzdoService.MatchesFilter(r, "incomplete"));
    }

    [Fact]
    public void MatchesFilter_Incomplete_CompletedState_ReturnsFalse()
    {
        var r = Record("completed", "failed");
        Assert.False(AzdoService.MatchesFilter(r, "incomplete"));
    }

    [Fact]
    public void MatchesFilter_Incomplete_CanceledRecord_ReturnsFalse()
    {
        // §7.3: canceled has state=completed → NOT incomplete
        var r = Record("completed", "canceled");
        Assert.False(AzdoService.MatchesFilter(r, "incomplete"));
    }

    // ── MatchesFilter — 'issues' predicate ───────────────────────────

    [Fact]
    public void MatchesFilter_Issues_HasIssues_ReturnsTrue()
    {
        var r = RecordWithIssues("completed", "succeeded");
        Assert.True(AzdoService.MatchesFilter(r, "issues"));
    }

    [Fact]
    public void MatchesFilter_Issues_NoIssues_ReturnsFalse()
    {
        var r = Record("completed", "failed");
        Assert.False(AzdoService.MatchesFilter(r, "issues"));
    }

    [Fact]
    public void MatchesFilter_Issues_NullIssues_ReturnsFalse()
    {
        var r = new AzdoTimelineRecord { State = "completed", Result = "failed", Issues = null };
        Assert.False(AzdoService.MatchesFilter(r, "issues"));
    }

    // ── MatchesFilter — invalid filter throws ────────────────────────

    [Fact]
    public void MatchesFilter_InvalidFilter_ThrowsArgumentException()
    {
        var r = Record("completed", "failed");
        Assert.Throws<ArgumentException>(() => AzdoService.MatchesFilter(r, "banana"));
    }

    // ── GetInvalidFilterMessage — lists canonical values, NOT aliases ─

    [Fact]
    public void GetInvalidFilterMessage_DoesNotMentionAliases()
    {
        var msg = AzdoService.GetInvalidFilterMessage("banana");
        Assert.Contains("failed", msg);
        Assert.Contains("running", msg);
        Assert.Contains("pending", msg);
        Assert.Contains("incomplete", msg);
        Assert.Contains("issues", msg);
        // Aliases must NOT appear in the error message
        Assert.DoesNotContain("inProgress", msg);
        Assert.DoesNotContain("active", msg);
        Assert.DoesNotContain("notStarted", msg);
    }

    // ── Edge case matrix (§7) ────────────────────────────────────────

    [Fact]
    public void EdgeCase_PendingRecord_MatchesPendingAndIncomplete_NotFailedNotRunning()
    {
        // §7.1: State=pending, Result=null
        var r = Record("pending", null);
        Assert.False(AzdoService.MatchesFilter(r, "failed"),    "pending should NOT match failed");
        Assert.False(AzdoService.MatchesFilter(r, "running"),   "pending should NOT match running");
        Assert.True(AzdoService.MatchesFilter(r, "pending"),    "pending should match pending");
        Assert.True(AzdoService.MatchesFilter(r, "incomplete"), "pending should match incomplete");
    }

    [Fact]
    public void EdgeCase_CanceledRecord_MatchesFailed_NotIncompleteNotRunningNotPending()
    {
        // §7.3: State=completed, Result=canceled
        var r = Record("completed", "canceled");
        Assert.True(AzdoService.MatchesFilter(r, "failed"),     "canceled should match failed");
        Assert.False(AzdoService.MatchesFilter(r, "running"),   "canceled should NOT match running");
        Assert.False(AzdoService.MatchesFilter(r, "pending"),   "canceled should NOT match pending");
        Assert.False(AzdoService.MatchesFilter(r, "incomplete"), "canceled (state=completed) should NOT match incomplete");
        Assert.False(AzdoService.MatchesFilter(r, "issues"),    "canceled with no issues should NOT match issues");
    }

    [Fact]
    public void EdgeCase_CompletedWithIssues_MatchesFailedAndIssues_NotRunningPendingIncomplete()
    {
        // State=completed, Result=succeeded, Issues.Count > 0
        var r = RecordWithIssues("completed", "succeeded");
        Assert.True(AzdoService.MatchesFilter(r, "failed"),     "completed+issues should match failed (issues gate)");
        Assert.True(AzdoService.MatchesFilter(r, "issues"),     "completed+issues should match issues");
        Assert.False(AzdoService.MatchesFilter(r, "running"),   "completed+issues should NOT match running");
        Assert.False(AzdoService.MatchesFilter(r, "pending"),   "completed+issues should NOT match pending");
        Assert.False(AzdoService.MatchesFilter(r, "incomplete"), "completed+issues should NOT match incomplete");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AzdoTimelineRecord Record(string? state, string? result) =>
        new() { State = state, Result = result };

    private static AzdoTimelineRecord RecordWithIssues(string? state, string? result) =>
        new()
        {
            State = state,
            Result = result,
            Issues = new List<AzdoIssue> { new() { Type = "warning", Message = "a warning" } }
        };
}
