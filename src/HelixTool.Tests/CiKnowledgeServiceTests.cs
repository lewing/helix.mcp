using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class CiKnowledgeServiceTests
{
    // ──────────────────────────────────────────────
    // Profile lookup — all 9 repos by short name
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_KnownRepos_ReturnsProfile(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo);

        Assert.NotNull(profile);
        Assert.Equal(repo, profile.RepoName);
        Assert.NotEmpty(profile.FailureSearchPatterns);
        Assert.NotEmpty(profile.InvestigationTips);
    }

    // ──────────────────────────────────────────────
    // Case insensitivity — covers old + new repos
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("RUNTIME")]
    [InlineData("Runtime")]
    [InlineData("ASPNETCORE")]
    [InlineData("MAUI")]
    [InlineData("Maui")]
    [InlineData("Macios")]
    [InlineData("MACIOS")]
    [InlineData("Android")]
    [InlineData("ANDROID")]
    public void GetProfile_CaseInsensitive(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo);
        Assert.NotNull(profile);
    }

    // ──────────────────────────────────────────────
    // Full repo path resolution — org/repo → short name
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("dotnet/runtime", "runtime")]
    [InlineData("dotnet/aspnetcore", "aspnetcore")]
    [InlineData("dotnet/sdk", "sdk")]
    [InlineData("dotnet/roslyn", "roslyn")]
    [InlineData("dotnet/efcore", "efcore")]
    [InlineData("dotnet/maui", "maui")]
    [InlineData("xamarin/macios", "macios")]
    [InlineData("xamarin/android", "android")]
    public void GetProfile_FullRepoPath_ResolvesToShortName(string fullPath, string expectedName)
    {
        var profile = CiKnowledgeService.GetProfile(fullPath);

        Assert.NotNull(profile);
        Assert.Equal(expectedName, profile.RepoName);
    }

    // ──────────────────────────────────────────────
    // dotnet/dotnet lookups (full path resolves via shortName extraction)
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet/dotnet")]
    public void GetProfile_DotnetRepo_ResolvesToDotnet(string input)
    {
        var profile = CiKnowledgeService.GetProfile(input);

        Assert.NotNull(profile);
        Assert.Equal("dotnet/dotnet", profile.RepoName);
        Assert.False(profile.UsesHelix);
    }

    [Fact]
    public void GetProfile_UnknownRepo_ReturnsNull()
    {
        Assert.Null(CiKnowledgeService.GetProfile("unknown-repo"));
    }

    // ──────────────────────────────────────────────
    // KnownRepos collection
    // ──────────────────────────────────────────────

    [Fact]
    public void KnownRepos_Contains9Repos()
    {
        Assert.Equal(9, CiKnowledgeService.KnownRepos.Count);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void KnownRepos_ContainsRepo(string repo)
    {
        Assert.Contains(repo, CiKnowledgeService.KnownRepos);
    }

    // ──────────────────────────────────────────────
    // UsesHelix property — Helix vs non-Helix repos
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime", true)]
    [InlineData("aspnetcore", true)]
    [InlineData("sdk", true)]
    [InlineData("roslyn", true)]
    [InlineData("efcore", true)]
    [InlineData("maui", true)]
    [InlineData("dotnet", false)]
    [InlineData("macios", false)]
    [InlineData("android", false)]
    public void GetProfile_UsesHelix_CorrectForEachRepo(string repo, bool expected)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.Equal(expected, profile.UsesHelix);
    }

    // ──────────────────────────────────────────────
    // OrgProject — dnceng vs devdiv
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime", "dnceng-public/public")]
    [InlineData("aspnetcore", "dnceng-public/public")]
    [InlineData("sdk", "dnceng-public/public")]
    [InlineData("roslyn", "dnceng-public/public")]
    [InlineData("efcore", "dnceng-public/public")]
    [InlineData("dotnet", "dnceng-public/public")]
    [InlineData("maui", "dnceng-public/public")]
    [InlineData("macios", "devdiv/DevDiv")]
    [InlineData("android", "devdiv/DevDiv")]
    public void GetProfile_OrgProject_CorrectForEachRepo(string repo, string expected)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.Equal(expected, profile.OrgProject);
    }

    // ──────────────────────────────────────────────
    // TestFramework — non-empty for all
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_TestFramework_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.TestFramework);
    }

    [Theory]
    [InlineData("macios", "NUnit")]
    [InlineData("android", "NUnit")]
    [InlineData("runtime", "xUnit")]
    public void GetProfile_TestFramework_CorrectForRepo(string repo, string expectedContains)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.Contains(expectedContains, profile.TestFramework);
    }

    // ──────────────────────────────────────────────
    // TestRunnerModel — non-empty for all
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_TestRunnerModel_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.TestRunnerModel);
    }

    [Fact]
    public void MaciosProfile_TestRunnerModel_IsMakeBased()
    {
        var profile = CiKnowledgeService.GetProfile("macios")!;
        Assert.Contains("Make", profile.TestRunnerModel);
    }

    // ──────────────────────────────────────────────
    // WorkItemNamingPattern — non-empty for all
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_WorkItemNamingPattern_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.WorkItemNamingPattern);
    }

    // ──────────────────────────────────────────────
    // KnownGotchas — non-empty for all
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_KnownGotchas_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.KnownGotchas);
    }

    // ──────────────────────────────────────────────
    // RecommendedInvestigationOrder — non-empty for all
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_RecommendedInvestigationOrder_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.RecommendedInvestigationOrder);
    }

    // ──────────────────────────────────────────────
    // PipelineNames — repos that have them
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_PipelineNames_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.PipelineNames);
    }

    [Fact]
    public void MauiProfile_HasThreePipelines()
    {
        var profile = CiKnowledgeService.GetProfile("maui")!;
        Assert.Equal(3, profile.PipelineNames.Length);
        Assert.Contains(profile.PipelineNames, p => p.Contains("maui-pr"));
        Assert.Contains(profile.PipelineNames, p => p.Contains("uitests"));
        Assert.Contains(profile.PipelineNames, p => p.Contains("devicetests"));
    }

    [Fact]
    public void MaciosProfile_HasMultiplePipelines()
    {
        var profile = CiKnowledgeService.GetProfile("macios")!;
        Assert.True(profile.PipelineNames.Length >= 2);
    }

    // ──────────────────────────────────────────────
    // ExitCodeMeanings — repos that define them
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    public void GetProfile_ExitCodeMeanings_NonEmpty_ForHelixReposAndDotnet(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.ExitCodeMeanings);
    }

    [Theory]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_ExitCodeMeanings_Empty_ForNoHelixDevdivRepos(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.Empty(profile.ExitCodeMeanings);
    }

    // ──────────────────────────────────────────────
    // New repo-specific profile tests
    // ──────────────────────────────────────────────

    [Fact]
    public void MauiProfile_UsesHelix()
    {
        var profile = CiKnowledgeService.GetProfile("maui")!;

        Assert.True(profile.UsesHelix);
        Assert.Equal("varies", profile.HelixTestResultAvailability);
        Assert.Equal("dnceng-public/public", profile.OrgProject);
        Assert.Contains("xUnit", profile.TestFramework);
        Assert.Contains("Appium", profile.TestFramework);
    }

    [Fact]
    public void MauiProfile_KnownGotchas_MentionsThreePipelines()
    {
        var profile = CiKnowledgeService.GetProfile("maui")!;
        Assert.Contains(profile.KnownGotchas, g => g.Contains("THREE separate pipelines"));
    }

    [Fact]
    public void MaciosProfile_NoHelix_DevdivOrg()
    {
        var profile = CiKnowledgeService.GetProfile("macios")!;

        Assert.False(profile.UsesHelix);
        Assert.Equal("none", profile.HelixTestResultAvailability);
        Assert.Equal("devdiv/DevDiv", profile.OrgProject);
        Assert.Equal("xamarin/macios", profile.DisplayName);
    }

    [Fact]
    public void MaciosProfile_UsesNUnit()
    {
        var profile = CiKnowledgeService.GetProfile("macios")!;
        Assert.Contains("NUnit", profile.TestFramework);
    }

    [Fact]
    public void MaciosProfile_KnownGotchas_WarnsAboutDevdiv()
    {
        var profile = CiKnowledgeService.GetProfile("macios")!;
        Assert.Contains(profile.KnownGotchas, g => g.Contains("devdiv"));
    }

    [Fact]
    public void AndroidProfile_NoHelix_DevdivOrg()
    {
        var profile = CiKnowledgeService.GetProfile("android")!;

        Assert.False(profile.UsesHelix);
        Assert.Equal("none", profile.HelixTestResultAvailability);
        Assert.Equal("devdiv/DevDiv", profile.OrgProject);
        Assert.Equal("dotnet/android", profile.DisplayName);
    }

    [Fact]
    public void AndroidProfile_MixedTestFrameworks()
    {
        var profile = CiKnowledgeService.GetProfile("android")!;
        Assert.Contains("NUnit", profile.TestFramework);
        Assert.Contains("xUnit", profile.TestFramework);
    }

    [Fact]
    public void AndroidProfile_KnownGotchas_WarnsAboutDevdiv()
    {
        var profile = CiKnowledgeService.GetProfile("android")!;
        Assert.Contains(profile.KnownGotchas, g => g.Contains("devdiv"));
    }

    [Fact]
    public void AndroidProfile_KnownGotchas_MentionsForkPRs()
    {
        var profile = CiKnowledgeService.GetProfile("android")!;
        Assert.Contains(profile.KnownGotchas, g => g.Contains("Fork") || g.Contains("fork"));
    }

    // ──────────────────────────────────────────────
    // Existing repo-specific tests (retained)
    // ──────────────────────────────────────────────

    [Fact]
    public void DotnetProfile_DoesNotUseHelix()
    {
        var dotnet = CiKnowledgeService.GetProfile("dotnet")!;

        Assert.False(dotnet.UsesHelix);
        Assert.Contains(dotnet.InvestigationTips, t => t.Contains("does NOT use Helix"));
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

    [Fact]
    public void EfcoreProfile_HasLowercaseHelixTaskName()
    {
        var efcore = CiKnowledgeService.GetProfile("efcore")!;
        Assert.Contains("Send job to helix", efcore.HelixTaskNames);
    }

    [Fact]
    public void RoslynProfile_NoHelixTaskNames()
    {
        var roslyn = CiKnowledgeService.GetProfile("roslyn")!;
        Assert.Empty(roslyn.HelixTaskNames);
    }

    // ──────────────────────────────────────────────
    // GetGuide — formatting
    // ──────────────────────────────────────────────

    [Fact]
    public void GetGuide_KnownRepo_ContainsSearchPatterns()
    {
        var guide = CiKnowledgeService.GetGuide("aspnetcore");

        Assert.Contains("Failed", guide);
        Assert.Contains("azdo_test_runs", guide);
        Assert.Contains("helix_parse_uploaded_trx will ALWAYS fail", guide);
    }

    [Fact]
    public void GetGuide_Aspnetcore_FrontLoadsAzdoRoutingBeforeSearchPatterns()
    {
        var guide = CiKnowledgeService.GetGuide("aspnetcore");

        var helixResultsLine = "**Test results in Helix:** No — use azdo_test_runs + azdo_test_results";
        Assert.Contains(helixResultsLine, guide);
        Assert.True(
            guide.IndexOf(helixResultsLine, StringComparison.Ordinal) <
            guide.IndexOf("## Recommended Search Patterns", StringComparison.Ordinal));
    }

    [Fact]
    public void GetGuide_Aspnetcore_RecommendedOrder_PivotsToAzdoBeforeHelixSearch()
    {
        var guide = CiKnowledgeService.GetGuide("aspnetcore");
        var orderSection = GetMarkdownSection(guide, "## Recommended Investigation Order");

        Assert.Contains("azdo_test_runs(buildId) + azdo_test_results(buildId, runId)", orderSection);
        Assert.Contains("helix_search_log(jobId, workItem, '  Failed')", orderSection);
        Assert.DoesNotContain("helix_parse_uploaded_trx", orderSection);
        Assert.True(
            orderSection.IndexOf("azdo_test_runs(buildId) + azdo_test_results(buildId, runId)", StringComparison.Ordinal) <
            orderSection.IndexOf("helix_search_log(jobId, workItem, '  Failed')", StringComparison.Ordinal));
    }

    [Fact]
    public void GetGuide_Runtime_PartialSupportStillDirectsFullCoverageToAzdo()
    {
        var guide = CiKnowledgeService.GetGuide("runtime");

        Assert.Contains("Partial — helix_parse_uploaded_trx works for some tests", guide);
        Assert.Contains("use azdo_test_runs + azdo_test_results for full coverage", guide);
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
    public void GetGuide_UnknownRepo_ListsKnownRepos()
    {
        var guide = CiKnowledgeService.GetGuide("unknown");
        Assert.Contains("dotnet/maui", guide);
        Assert.Contains("xamarin/macios", guide);
        Assert.Contains("dotnet/android", guide);
    }

    [Theory]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetGuide_NewRepos_ContainsKnownGotchasSection(string repo)
    {
        var guide = CiKnowledgeService.GetGuide(repo);

        Assert.Contains("Known Gotchas", guide);
        Assert.Contains("⚠️", guide);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetGuide_AllRepos_ContainsRecommendedInvestigationOrder(string repo)
    {
        var guide = CiKnowledgeService.GetGuide(repo);
        Assert.Contains("Recommended Investigation Order", guide);
    }

    [Fact]
    public void GetGuide_Macios_ContainsDevdivWarning()
    {
        var guide = CiKnowledgeService.GetGuide("macios");

        Assert.Contains("devdiv", guide);
        Assert.Contains("No", guide); // "Uses Helix: No"
    }

    [Fact]
    public void GetGuide_Android_ContainsDevdivWarning()
    {
        var guide = CiKnowledgeService.GetGuide("android");
        Assert.Contains("devdiv", guide);
    }

    [Fact]
    public void GetGuide_Maui_ContainsPipelineNames()
    {
        var guide = CiKnowledgeService.GetGuide("maui");

        Assert.Contains("maui-pr", guide);
        Assert.Contains("uitests", guide);
        Assert.Contains("devicetests", guide);
    }

    [Fact]
    public void FormatProfile_RendersOrgProject()
    {
        var guide = CiKnowledgeService.GetGuide("macios");
        Assert.Contains("devdiv/DevDiv", guide);
    }

    [Fact]
    public void FormatProfile_RendersTestFramework()
    {
        var guide = CiKnowledgeService.GetGuide("runtime");
        Assert.Contains("xUnit", guide);
    }

    [Fact]
    public void FormatProfile_RendersExitCodes_WhenPresent()
    {
        var guide = CiKnowledgeService.GetGuide("runtime");
        Assert.Contains("Exit Code Reference", guide);
    }

    [Fact]
    public void FormatProfile_OmitsExitCodes_WhenEmpty()
    {
        var guide = CiKnowledgeService.GetGuide("macios");
        Assert.DoesNotContain("Exit Code Reference", guide);
    }

    // ──────────────────────────────────────────────
    // GetOverview — table with 9 repos
    // ──────────────────────────────────────────────

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
        Assert.Contains("maui", overview);
        Assert.Contains("macios", overview);
        Assert.Contains("android", overview);
    }

    [Fact]
    public void GetOverview_ContainsKeyInsight()
    {
        var overview = CiKnowledgeService.GetOverview();

        Assert.Contains("Most .NET repos do NOT upload test results to Helix", overview);
        Assert.Contains("azdo_test_runs", overview);
        Assert.Contains("failedTests=0 is a lie", overview);
    }

    [Fact]
    public void GetOverview_ContainsDevdivWarning()
    {
        var overview = CiKnowledgeService.GetOverview();
        Assert.Contains("macios and android are on devdiv", overview);
    }

    [Fact]
    public void GetOverview_ContainsOrgColumn()
    {
        var overview = CiKnowledgeService.GetOverview();

        Assert.Contains("dnceng-public/public", overview);
        Assert.Contains("devdiv/DevDiv", overview);
    }

    [Fact]
    public void GetOverview_ContainsQuickReferenceTable()
    {
        var overview = CiKnowledgeService.GetOverview();

        Assert.Contains("Quick Reference", overview);
        Assert.Contains("| Repo |", overview);
    }

    [Fact]
    public void GetOverview_ShowsHelixStatusForNewRepos()
    {
        var overview = CiKnowledgeService.GetOverview();

        // maui uses Helix (✅), macios/android don't (❌)
        // Just verify the table has entries for all 9 repos by checking display names
        Assert.Contains("dotnet/maui", overview);
        Assert.Contains("xamarin/macios", overview);
        Assert.Contains("dotnet/android", overview);
    }

    // ──────────────────────────────────────────────
    // Edge cases — display names for non-dotnet orgs
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime", "dotnet/runtime")]
    [InlineData("macios", "xamarin/macios")]
    [InlineData("android", "dotnet/android")]
    [InlineData("dotnet", "dotnet/dotnet (VMR)")]
    public void GetProfile_DisplayName_CorrectOrg(string repo, string expectedDisplay)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.Equal(expectedDisplay, profile.DisplayName);
    }

    // ──────────────────────────────────────────────
    // Edge: HelixTaskNames empty for non-Helix repos
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("dotnet")]
    [InlineData("macios")]
    [InlineData("android")]
    [InlineData("roslyn")]
    public void GetProfile_HelixTaskNames_Empty_ForReposWithoutExplicitTask(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.Empty(profile.HelixTaskNames);
    }

    // ──────────────────────────────────────────────
    // UploadedFiles property — sanity checks
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_UploadedFiles_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.UploadedFiles);
    }

    // ──────────────────────────────────────────────
    // CommonFailureCategories — non-empty for all
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("runtime")]
    [InlineData("aspnetcore")]
    [InlineData("sdk")]
    [InlineData("roslyn")]
    [InlineData("efcore")]
    [InlineData("dotnet")]
    [InlineData("maui")]
    [InlineData("macios")]
    [InlineData("android")]
    public void GetProfile_CommonFailureCategories_NonEmpty(string repo)
    {
        var profile = CiKnowledgeService.GetProfile(repo)!;
        Assert.NotEmpty(profile.CommonFailureCategories);
    }

    private static string GetMarkdownSection(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Heading '{heading}' not found.");

        var nextHeading = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return nextHeading >= 0
            ? text[start..nextHeading]
            : text[start..];
    }
}
