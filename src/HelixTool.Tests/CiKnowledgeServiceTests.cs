using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class CiKnowledgeServiceTests
{
    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("vmr")]
    public void GetProfile_KnownRepos_ReturnsProfile(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo);

        Assert.NotNull(profile);
        Assert.Equal(repo, profile.RepoName);
        Assert.NotEmpty(profile.FailureSearchPatterns);
        Assert.NotEmpty(profile.InvestigationTips);
    }

    [Theory]
    [InlineData("RUNTIME")]
    [InlineData("Runtime")]
    [InlineData("ASPNETCORE")]
    public void GetProfile_CaseInsensitive(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo);
        Assert.NotNull(profile);
    }

    [Theory]
    [InlineData("dotnet/runtime", "runtime")]
    [InlineData("dotnet/aspnetcore", "aspnetcore")]
    [InlineData("dotnet/sdk", "sdk")]
    public void GetProfile_FullRepoPath_ResolvesToShortName(string fullPath, string expectedName)
    {
        var profile = CiKnowledgeService.GetProfile(fullPath);

        Assert.NotNull(profile);
        Assert.Equal(expectedName, profile.RepoName);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet/dotnet")]
    public void GetProfile_DotnetRepo_ResolvesToVmr(string input)
    {
        var profile = CiKnowledgeService.GetProfile(input);

        Assert.NotNull(profile);
        Assert.Equal("vmr", profile.RepoName);
        Assert.False(profile.UsesHelix);
    }

    [Fact]
    public void GetProfile_UnknownRepo_ReturnsNull()
    {
        Assert.Null(CiKnowledgeService.GetProfile("unknown-repo"));
    }

    [Fact]
    public void GetGuide_KnownRepo_ContainsSearchPatterns()
    {
        var guide = CiKnowledgeService.GetGuide("aspnetcore");

        Assert.Contains("Failed", guide);
        Assert.Contains("azdo_test_runs", guide);
        Assert.Contains("helix_test_results will ALWAYS fail", guide);
    }

    [Fact]
    public void GetGuide_UnknownRepo_ReturnsGeneralGuide()
    {
        var guide = CiKnowledgeService.GetGuide("some-unknown-repo");

        Assert.Contains("No specific profile found", guide);
        Assert.Contains("helix_search_log", guide);
        Assert.Contains("azdo_test_runs", guide);
    }

    [Fact]
    public void GetOverview_ContainsAllRepos()
    {
        var overview = CiKnowledgeService.GetOverview();

        Assert.Contains("runtime", overview);
        Assert.Contains("aspnetcore", overview);
        Assert.Contains("sdk", overview);
        Assert.Contains("roslyn", overview);
        Assert.Contains("efcore", overview);
        Assert.Contains("VMR", overview);
    }

    [Fact]
    public void GetOverview_ContainsKeyInsight()
    {
        var overview = CiKnowledgeService.GetOverview();

        Assert.Contains("No major .NET repo uploads TRX files to Helix", overview);
        Assert.Contains("azdo_test_runs", overview);
    }

    [Fact]
    public void KnownRepos_Contains6Repos()
    {
        Assert.Equal(6, CiKnowledgeService.KnownRepos.Count);
    }

    [Fact]
    public void VmrProfile_DoesNotUseHelix()
    {
        var vmr = CiKnowledgeService.GetProfile("vmr")!;

        Assert.False(vmr.UsesHelix);
        Assert.Contains(vmr.InvestigationTips, t => t.Contains("does NOT use Helix"));
    }

    [Fact]
    public void RuntimeProfile_HasXunitPatterns()
    {
        var runtime = CiKnowledgeService.GetProfile("runtime")!;

        Assert.Contains("[FAIL]", runtime.FailureSearchPatterns);
        Assert.Contains("Send to Helix", runtime.HelixTaskNames);
    }

    [Fact]
    public void SdkProfile_HasEmojiTaskName()
    {
        var sdk = CiKnowledgeService.GetProfile("sdk")!;

        Assert.Contains("🟣 Run TestBuild Tests", sdk.HelixTaskNames);
    }
}
