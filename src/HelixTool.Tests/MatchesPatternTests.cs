using Xunit;
using HelixTool.Core;

namespace HelixTool.Tests;

public class MatchesPatternTests
{
    [Fact]
    public void Wildcard_MatchesEverything()
    {
        Assert.True(HelixService.MatchesPattern("anything.txt", "*"));
        Assert.True(HelixService.MatchesPattern("", "*"));
    }

    [Fact]
    public void ExtensionPattern_MatchesSuffix()
    {
        Assert.True(HelixService.MatchesPattern("build.binlog", "*.binlog"));
        Assert.True(HelixService.MatchesPattern("nested/path/build.binlog", "*.binlog"));
    }

    [Fact]
    public void ExtensionPattern_DoesNotMatchDifferentExtension()
    {
        Assert.False(HelixService.MatchesPattern("results.trx", "*.binlog"));
    }

    [Fact]
    public void ExtensionPattern_CaseInsensitive()
    {
        Assert.True(HelixService.MatchesPattern("Build.BINLOG", "*.binlog"));
        Assert.True(HelixService.MatchesPattern("build.BinLog", "*.BINLOG"));
    }

    [Fact]
    public void SubstringPattern_MatchesContaining()
    {
        Assert.True(HelixService.MatchesPattern("my-test-results.xml", "test"));
        Assert.True(HelixService.MatchesPattern("test-file.txt", "test"));
    }

    [Fact]
    public void SubstringPattern_CaseInsensitive()
    {
        Assert.True(HelixService.MatchesPattern("TestResults.xml", "test"));
        Assert.True(HelixService.MatchesPattern("TESTRESULTS.xml", "test"));
    }

    [Fact]
    public void SubstringPattern_NoMatch()
    {
        Assert.False(HelixService.MatchesPattern("build.binlog", "trx"));
    }

    [Fact]
    public void ExtensionPattern_Trx()
    {
        Assert.True(HelixService.MatchesPattern("results.trx", "*.trx"));
        Assert.False(HelixService.MatchesPattern("results.xml", "*.trx"));
    }

    [Fact]
    public void ExtensionPattern_WithDotOnly()
    {
        // "*.": pattern[1..] is ".", so name must end with "."
        Assert.True(HelixService.MatchesPattern("file.", "*."));
        Assert.False(HelixService.MatchesPattern("file.txt", "*."));
    }
}
