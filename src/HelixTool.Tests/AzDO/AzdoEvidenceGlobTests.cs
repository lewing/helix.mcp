// Group D — trailing-star glob regression (§9.1 / R5).
//
// StringHelpers.MatchesPattern currently treats "Logs_Build_*" as a literal substring including
// the '*', so it matches nothing. D1 documents that this FAILS on main today.
// After Ripley's R5 fix (add trailing-* branch), all D tests must be green.
//
// Run these first: `dotnet test --filter "FullyQualifiedName~AzdoEvidenceGlobTests"`

using HelixTool.Core;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoEvidenceGlobTests
{
    // ── D1 — trailing-* prefix glob regression (FAILS ON MAIN TODAY) ─────────
    // This is the gate test: D1 must fail on main and pass on the branch (§11.3).

    [Fact]
    public void MatchesPattern_TrailingStar_MatchesPrefix()
    {
        // "Logs_Build_*" should match any name starting with "Logs_Build_"
        Assert.True(StringHelpers.MatchesPattern("Logs_Build_Attempt1_linux__arm64_release", "Logs_Build_*"));
    }

    [Fact]
    public void MatchesPattern_TrailingStar_MatchesPrefixExactly()
    {
        Assert.True(StringHelpers.MatchesPattern("Logs_Build_", "Logs_Build_*"));
    }

    [Fact]
    public void MatchesPattern_TrailingStar_DoesNotMatchNonPrefix()
    {
        Assert.False(StringHelpers.MatchesPattern("Other_Logs_Build_x", "Logs_Build_*"));
    }

    [Fact]
    public void MatchesPattern_TrailingStar_CaseInsensitive()
    {
        Assert.True(StringHelpers.MatchesPattern("LOGS_BUILD_Attempt2_x", "Logs_Build_*"));
        Assert.True(StringHelpers.MatchesPattern("logs_build_foo", "LOGS_BUILD_*"));
    }

    [Theory]
    [InlineData("Logs_Build_Attempt1_linux__arm64_release_CrossAOT_Mono", "Logs_Build_*")]
    [InlineData("Logs_Build_Attempt2_windows__x64_release", "Logs_Build_*")]
    [InlineData("Logs_Build_Foo", "Logs_Build_*")]
    public void MatchesPattern_TrailingStar_MatchesRealArtifactNames(string name, string pattern)
    {
        Assert.True(StringHelpers.MatchesPattern(name, pattern));
    }

    [Fact]
    public void MatchesPattern_TrailingStar_EmptyPrefixMatchesEverything()
    {
        // "*" alone is the catch-all; covered by existing test but confirm parity.
        Assert.True(StringHelpers.MatchesPattern("anything", "*"));
    }

    // ── D2 — *.binlog suffix behaviour unchanged after the fix ────────────────

    [Fact]
    public void MatchesPattern_DotStarSuffix_StillMatchesSuffix()
    {
        Assert.True(StringHelpers.MatchesPattern("build.binlog", "*.binlog"));
        Assert.True(StringHelpers.MatchesPattern("deep/path/build.binlog", "*.binlog"));
        Assert.False(StringHelpers.MatchesPattern("build.binlog.zip", "*.binlog"));
    }

    [Fact]
    public void MatchesPattern_DotStarSuffix_CaseInsensitiveUnchanged()
    {
        Assert.True(StringHelpers.MatchesPattern("BUILD.BINLOG", "*.binlog"));
    }

    // ── D3 — bare wildcard and substring behaviour unchanged ──────────────────

    [Fact]
    public void MatchesPattern_BareWildcard_MatchesEverything()
    {
        Assert.True(StringHelpers.MatchesPattern("", "*"));
        Assert.True(StringHelpers.MatchesPattern("anything_at_all", "*"));
    }

    [Fact]
    public void MatchesPattern_SubstringPattern_StillMatchesContaining()
    {
        // Patterns that are not "*", not "*.ext", and not "prefix*"
        // are treated as substring search — must remain unchanged.
        Assert.True(StringHelpers.MatchesPattern("my-test-results.xml", "test"));
        Assert.False(StringHelpers.MatchesPattern("build.binlog", "trx"));
    }

    [Theory]
    [InlineData("build_results.trx", "*.trx")]
    [InlineData("results.xml", "*.xml")]
    public void MatchesPattern_OtherExtensions_Unchanged(string name, string pattern)
    {
        Assert.True(StringHelpers.MatchesPattern(name, pattern));
    }

    // ── D4 — artifact filter via AzdoService applies the fixed pattern ────────
    // Verifies that GetBuildArtifactsAsync with pattern "Logs_Build_*" returns > 0
    // when the artifact list contains Logs_Build_* names.

    [Fact]
    public async Task GetBuildArtifactsAsync_TrailingStarPattern_ReturnsMatchingArtifacts()
    {
        var mockApi = Substitute.For<IAzdoApiClient>();
        var svc = new AzdoService(mockApi);

        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Id = 1, Name = "Logs_Build_Attempt1_linux_x64", Resource = new() { Type = "Container" } },
            new() { Id = 2, Name = "Logs_Build_Attempt1_windows_x64", Resource = new() { Type = "Container" } },
            new() { Id = 3, Name = "TestResults_linux_x64", Resource = new() { Type = "Container" } },
        };

        mockApi.GetBuildArtifactsAsync("dnceng-public", "public", 1570501, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await svc.GetBuildArtifactsAsync("1570501", pattern: "Logs_Build_*");

        // After fix: both Logs_Build_* artifacts match; TestResults does not.
        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.StartsWith("Logs_Build_", a.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ── D5 — Helix find-files callers: *.binlog suffix pattern unaffected ─────
    // The glob fix only adds a new trailing-* branch; *.ext and * branches are unchanged.

    [Theory]
    [InlineData("my-test.binlog", "*.binlog", true)]
    [InlineData("build.BINLOG", "*.binlog", true)]
    [InlineData("results.trx", "*.binlog", false)]
    [InlineData("helix.trx", "*.trx", true)]
    [InlineData("run_1", "*", true)]
    public void MatchesPattern_HelixUsagePaths_Unchanged(string name, string pattern, bool expected)
    {
        Assert.Equal(expected, StringHelpers.MatchesPattern(name, pattern));
    }

    // ── Extra: mid-pattern * is NOT treated as glob (no regex, ReDoS-safe) ────
    // "Logs_Build_*_linux" has '*' in the middle; it is NOT a valid glob prefix.
    // It should fall through to substring search for "Logs_Build_*_linux" literally.
    // (Documents the boundary so we don't accidentally over-extend the fix.)

    [Fact]
    public void MatchesPattern_MidPattern_Asterisk_NoBehaviorChange()
    {
        // "Logs_Build_*_linux" does not start with '*' alone, does not end with a literal,
        // does not fit the trailing-* branch (not ending with '*'... unless the fix adds that too).
        // The important invariant: this test documents whatever the actual behavior is after fix.
        // The pattern is not a supported glob form — it should NOT silently match too broadly.
        // Treat as substring; "Logs_Build_*_linux" is not contained in "Logs_Build_foo_linux".
        Assert.False(StringHelpers.MatchesPattern("Logs_Build_foo_linux", "Logs_Build_*_linux"));
    }
}
