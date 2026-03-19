// Tests for AzdoIdResolver — static URL parser for AzDO build URLs.
// Mirrors HelixIdResolver pattern: pure-function tests, no mocking needed.
// Tests both Resolve() (throws on invalid) and TryResolve() (returns bool).

using Xunit;
using HelixTool.Core.AzDO;

namespace HelixTool.Tests.AzDO;

public class AzdoIdResolverTests
{
    // ===================================================================
    // Resolve() tests — returns tuple, throws ArgumentException on invalid
    // ===================================================================

    // --- dev.azure.com URL format ---

    [Fact]
    public void Resolve_DevAzureComUrl_PublicOrg_ExtractsAllFields()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=123";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng-public", org);
        Assert.Equal("public", project);
        Assert.Equal(123, buildId);
    }

    [Fact]
    public void Resolve_DevAzureComUrl_InternalOrg_ExtractsAllFields()
    {
        var url = "https://dev.azure.com/dnceng/internal/_build/results?buildId=456";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng", org);
        Assert.Equal("internal", project);
        Assert.Equal(456, buildId);
    }

    // --- visualstudio.com URL format (legacy) ---

    [Fact]
    public void Resolve_VisualStudioComUrl_ExtractsOrgFromSubdomain()
    {
        var url = "https://dnceng.visualstudio.com/public/_build/results?buildId=789";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng", org);
        Assert.Equal("public", project);
        Assert.Equal(789, buildId);
    }

    // --- Plain integer (bare buildId) ---

    [Fact]
    public void Resolve_PlainInteger_UsesDefaultOrgAndProject()
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve("123");

        Assert.Equal(AzdoIdResolver.DefaultOrg, org);
        Assert.Equal(AzdoIdResolver.DefaultProject, project);
        Assert.Equal(123, buildId);
    }

    [Fact]
    public void Resolve_PlainIntegerMaxValue_ParsesCorrectly()
    {
        var (_, _, buildId) = AzdoIdResolver.Resolve(int.MaxValue.ToString());

        Assert.Equal(int.MaxValue, buildId);
    }

    // --- REST API URL format: _apis/build/builds/{id} ---

    [Fact]
    public void Resolve_RestApiUrl_DevAzureCom_ExtractsAllFields()
    {
        var url = "https://dev.azure.com/dnceng/7ea9116e-9fac-403d-b258-b31fcf1bb293/_apis/build/builds/2926555";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng", org);
        Assert.Equal("7ea9116e-9fac-403d-b258-b31fcf1bb293", project);
        Assert.Equal(2926555, buildId);
    }

    [Fact]
    public void Resolve_RestApiUrl_VisualStudioCom_ExtractsAllFields()
    {
        var url = "https://dnceng.visualstudio.com/internal/_apis/build/builds/12345";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng", org);
        Assert.Equal("internal", project);
        Assert.Equal(12345, buildId);
    }

    [Fact]
    public void Resolve_RestApiUrl_WithQueryParams_ExtractsFromPath()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_apis/build/builds/999?api-version=7.0";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng-public", org);
        Assert.Equal("public", project);
        Assert.Equal(999, buildId);
    }

    [Fact]
    public void Resolve_RestApiUrl_CapitalBuilds_StillParses()
    {
        var url = "https://dev.azure.com/dnceng/internal/_apis/build/Builds/42";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng", org);
        Assert.Equal("internal", project);
        Assert.Equal(42, buildId);
    }

    [Fact]
    public void TryResolve_RestApiUrl_ReturnsTrue()
    {
        var url = "https://dev.azure.com/dnceng/7ea9116e-9fac-403d-b258-b31fcf1bb293/_apis/build/builds/2926555";

        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out var buildId);

        Assert.True(result);
        Assert.Equal("dnceng", org);
        Assert.Equal("7ea9116e-9fac-403d-b258-b31fcf1bb293", project);
        Assert.Equal(2926555, buildId);
    }

    // --- Query string edge cases ---

    [Fact]
    public void Resolve_BuildIdWithTrailingQueryParams_StillParses()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=123&view=results";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng-public", org);
        Assert.Equal("public", project);
        Assert.Equal(123, buildId);
    }

    [Fact]
    public void Resolve_BuildIdNotFirstQueryParam_StillParses()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?view=results&buildId=999";

        var (_, _, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal(999, buildId);
    }

    [Fact]
    public void Resolve_BuildIdWithFragment_StillParses()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=555#timeline";

        var (_, _, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal(555, buildId);
    }

    // --- Resolve() throws on invalid input ---

    [Fact]
    public void Resolve_Null_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => AzdoIdResolver.Resolve(null!));
    }

    [Fact]
    public void Resolve_EmptyString_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => AzdoIdResolver.Resolve(""));
    }

    [Fact]
    public void Resolve_Whitespace_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => AzdoIdResolver.Resolve("   "));
    }

    [Fact]
    public void Resolve_NonUrlNonInteger_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve("not-a-url"));
        Assert.Contains("Invalid AzDO build reference", ex.Message);
    }

    [Fact]
    public void Resolve_NonAzdoUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve("https://github.com/foo/bar"));
    }

    [Fact]
    public void Resolve_AzdoUrlNoBuildIdParam_ThrowsArgumentException()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results";
        var ex = Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
        Assert.Contains("buildId", ex.Message);
    }

    [Fact]
    public void Resolve_AzdoUrlNonIntegerBuildId_ThrowsArgumentException()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=abc";
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
    }

    [Fact]
    public void Resolve_UnrecognizedHost_ThrowsArgumentException()
    {
        var url = "https://example.com/foo/_build/results?buildId=123";
        var ex = Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
        Assert.Contains("Unrecognized AzDO host", ex.Message);
    }

    // --- Error message quality ---

    [Fact]
    public void Resolve_ErrorMessage_IncludesOriginalInput()
    {
        var badUrl = "https://dev.azure.com/org/proj/_build/results";
        var ex = Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(badUrl));
        Assert.Contains(badUrl, ex.Message);
    }

    // ===================================================================
    // TryResolve() tests — returns bool, never throws
    // ===================================================================

    // --- Valid inputs ---

    [Fact]
    public void TryResolve_DevAzureComUrl_ReturnsTrue()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=123";

        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out var buildId);

        Assert.True(result);
        Assert.Equal("dnceng-public", org);
        Assert.Equal("public", project);
        Assert.Equal(123, buildId);
    }

    [Fact]
    public void TryResolve_VisualStudioComUrl_ReturnsTrue()
    {
        var url = "https://dnceng.visualstudio.com/public/_build/results?buildId=789";

        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out var buildId);

        Assert.True(result);
        Assert.Equal("dnceng", org);
        Assert.Equal("public", project);
        Assert.Equal(789, buildId);
    }

    [Fact]
    public void TryResolve_PlainInteger_ReturnsTrue()
    {
        var result = AzdoIdResolver.TryResolve("123", out var org, out var project, out var buildId);

        Assert.True(result);
        Assert.Equal(AzdoIdResolver.DefaultOrg, org);
        Assert.Equal(AzdoIdResolver.DefaultProject, project);
        Assert.Equal(123, buildId);
    }

    // --- Invalid inputs → returns false ---

    [Fact]
    public void TryResolve_Null_ReturnsFalse()
    {
        var result = AzdoIdResolver.TryResolve(null!, out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryResolve_EmptyString_ReturnsFalse()
    {
        var result = AzdoIdResolver.TryResolve("", out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryResolve_Whitespace_ReturnsFalse()
    {
        var result = AzdoIdResolver.TryResolve("   ", out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryResolve_NotAUrl_ReturnsFalse()
    {
        var result = AzdoIdResolver.TryResolve("not-a-url", out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryResolve_NonAzdoUrl_ReturnsFalse()
    {
        var result = AzdoIdResolver.TryResolve("https://github.com/foo/bar", out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryResolve_AzdoUrlNoBuildIdParam_ReturnsFalse()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results";
        var result = AzdoIdResolver.TryResolve(url, out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryResolve_AzdoUrlNonIntegerBuildId_ReturnsFalse()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=abc";
        var result = AzdoIdResolver.TryResolve(url, out _, out _, out _);
        Assert.False(result);
    }

    // --- Out parameter defaults on failure ---
    // TryResolve initializes out params to DefaultOrg/DefaultProject/0 before attempting

    [Fact]
    public void TryResolve_OnFailure_OutParamsAreDefaults()
    {
        AzdoIdResolver.TryResolve("garbage", out var org, out var project, out var buildId);

        Assert.Equal(AzdoIdResolver.DefaultOrg, org);
        Assert.Equal(AzdoIdResolver.DefaultProject, project);
        Assert.Equal(0, buildId);
    }

    // --- Negative / zero buildId ---
    // NOTE: The current implementation accepts negative and zero buildIds via int.TryParse.
    // These are semantically invalid but syntactically accepted. Document actual behavior.

    [Fact]
    public void Resolve_NegativePlainInteger_Accepted()
    {
        // int.TryParse("-5") succeeds — implementation doesn't validate positivity
        var (_, _, buildId) = AzdoIdResolver.Resolve("-5");
        Assert.Equal(-5, buildId);
    }

    [Fact]
    public void Resolve_ZeroPlainInteger_Accepted()
    {
        var (_, _, buildId) = AzdoIdResolver.Resolve("0");
        Assert.Equal(0, buildId);
    }

    [Fact]
    public void Resolve_NegativeBuildIdInUrl_Accepted()
    {
        // HttpUtility.ParseQueryString + int.TryParse("-1") succeeds
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=-1";
        var (_, _, buildId) = AzdoIdResolver.Resolve(url);
        Assert.Equal(-1, buildId);
    }

    // --- Theory: valid URL variations ---

    [Theory]
    [InlineData("https://dev.azure.com/dnceng-public/public/_build/results?buildId=100", "dnceng-public", "public", 100)]
    [InlineData("https://dev.azure.com/myorg/myproject/_build/results?buildId=42", "myorg", "myproject", 42)]
    [InlineData("https://dev.azure.com/dnceng/internal/_build?buildId=7", "dnceng", "internal", 7)]
    [InlineData("https://dev.azure.com/dnceng-public/public/_build/results?buildId=321&view=logs&j=abc", "dnceng-public", "public", 321)]
    public void TryResolve_VariousValidUrls_Theory(string url, string expectedOrg, string expectedProject, int expectedBuildId)
    {
        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out var buildId);

        Assert.True(result);
        Assert.Equal(expectedOrg, org);
        Assert.Equal(expectedProject, project);
        Assert.Equal(expectedBuildId, buildId);
    }

    // --- Theory: invalid inputs ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("https://google.com")]
    [InlineData("https://dev.azure.com/org/proj/_build/results")]
    [InlineData("https://dev.azure.com/org/proj/_build/results?buildId=notanumber")]
    public void TryResolve_VariousInvalidInputs_Theory(string input)
    {
        var result = AzdoIdResolver.TryResolve(input, out _, out _, out _);
        Assert.False(result);
    }

    // --- Very large buildId ---

    [Fact]
    public void TryResolve_VeryLargeBuildId_ParsesCorrectly()
    {
        var url = "https://dev.azure.com/dnceng-public/public/_build/results?buildId=1276327";

        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out var buildId);

        Assert.True(result);
        Assert.Equal("dnceng-public", org);
        Assert.Equal("public", project);
        Assert.Equal(1276327, buildId);
    }

    [Fact]
    public void TryResolve_IntMaxBuildIdInUrl_ParsesCorrectly()
    {
        var url = $"https://dev.azure.com/dnceng/internal/_build/results?buildId={int.MaxValue}";

        var result = AzdoIdResolver.TryResolve(url, out _, out _, out var buildId);

        Assert.True(result);
        Assert.Equal(int.MaxValue, buildId);
    }

    // --- Constants ---

    [Fact]
    public void DefaultOrg_IsDncengPublic()
    {
        Assert.Equal("dnceng-public", AzdoIdResolver.DefaultOrg);
    }

    [Fact]
    public void DefaultProject_IsPublic()
    {
        Assert.Equal("public", AzdoIdResolver.DefaultProject);
    }

    // ===================================================================
    // Multi-org coverage — real-world URL formats across AzDO orgs
    // ===================================================================

    [Theory]
    [InlineData("https://dev.azure.com/dnceng-public/public/_build/results?buildId=12345", "dnceng-public", "public", 12345)]
    [InlineData("https://dev.azure.com/dnceng/internal/_build/results?buildId=2930070", "dnceng", "internal", 2930070)]
    [InlineData("https://dev.azure.com/devdiv/DevDiv/_build/results?buildId=99999", "devdiv", "DevDiv", 99999)]
    [InlineData("https://dev.azure.com/dotnet/dotnet/_build/results?buildId=88888", "dotnet", "dotnet", 88888)]
    public void Resolve_MultiOrg_ExtractsCorrectOrgAndProject(string url, string expectedOrg, string expectedProject, int expectedBuildId)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal(expectedOrg, org);
        Assert.Equal(expectedProject, project);
        Assert.Equal(expectedBuildId, buildId);
    }

    [Fact]
    public void Resolve_DevDivOrg_DoesNotDefaultToDncengPublic()
    {
        var url = "https://dev.azure.com/devdiv/DevDiv/_build/results?buildId=99999";
        var (org, project, _) = AzdoIdResolver.Resolve(url);

        // devdiv org must NOT fall back to the default dnceng-public
        Assert.NotEqual(AzdoIdResolver.DefaultOrg, org);
        Assert.NotEqual(AzdoIdResolver.DefaultProject, project);
        Assert.Equal("devdiv", org);
        Assert.Equal("DevDiv", project);
    }

    [Fact]
    public void Resolve_DotnetOrg_DoesNotDefaultToDncengPublic()
    {
        var url = "https://dev.azure.com/dotnet/dotnet/_build/results?buildId=88888";
        var (org, project, _) = AzdoIdResolver.Resolve(url);

        Assert.NotEqual(AzdoIdResolver.DefaultOrg, org);
        Assert.Equal("dotnet", org);
        Assert.Equal("dotnet", project);
    }

    // --- visualstudio.com format with non-default org ---

    [Fact]
    public void Resolve_VisualStudioCom_InternalOrg_ExtractsFromSubdomain()
    {
        var url = "https://dnceng.visualstudio.com/internal/_build/results?buildId=77777";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("dnceng", org);
        Assert.Equal("internal", project);
        Assert.Equal(77777, buildId);
    }

    [Fact]
    public void Resolve_VisualStudioCom_DevDivOrg_ExtractsFromSubdomain()
    {
        var url = "https://devdiv.visualstudio.com/DevDiv/_build/results?buildId=55555";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("devdiv", org);
        Assert.Equal("DevDiv", project);
        Assert.Equal(55555, buildId);
    }

    // --- REST API format with various orgs ---

    [Theory]
    [InlineData("https://dev.azure.com/dnceng/internal/_apis/build/builds/66666", "dnceng", "internal", 66666)]
    [InlineData("https://dev.azure.com/devdiv/DevDiv/_apis/build/builds/44444", "devdiv", "DevDiv", 44444)]
    [InlineData("https://dev.azure.com/dotnet/dotnet/_apis/build/builds/33333", "dotnet", "dotnet", 33333)]
    public void Resolve_RestApi_MultiOrg_ExtractsAllFields(string url, string expectedOrg, string expectedProject, int expectedBuildId)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal(expectedOrg, org);
        Assert.Equal(expectedProject, project);
        Assert.Equal(expectedBuildId, buildId);
    }

    // --- URL-encoded project names ---

    [Fact]
    public void Resolve_UrlEncodedProjectName_ExtractsEncodedSegment()
    {
        // URL-encoded spaces: "My Project" → "My%20Project"
        var url = "https://dev.azure.com/myorg/My%20Project/_build/results?buildId=111";

        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("myorg", org);
        // The project segment comes from the URL path — it will be URL-decoded by Uri
        Assert.Equal("My Project", project);
        Assert.Equal(111, buildId);
    }

    // --- Additional invalid URL edge cases ---

    [Theory]
    [InlineData("https://dev.azure.com")] // no path segments
    [InlineData("https://dev.azure.com/")] // empty path
    [InlineData("https://dev.azure.com/org")] // only one segment
    [InlineData("ftp://dev.azure.com/org/proj/_build/results?buildId=1")] // wrong scheme
    public void TryResolve_MalformedAzdoUrls_ReturnsFalse(string url)
    {
        var result = AzdoIdResolver.TryResolve(url, out _, out _, out _);
        Assert.False(result);
    }
}
