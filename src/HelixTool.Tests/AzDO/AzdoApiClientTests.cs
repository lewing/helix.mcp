// Tests for AzdoApiClient — HTTP-based AzDO REST API client.
// Uses FakeHttpMessageHandler for HTTP layer isolation and NSubstitute for IAzdoTokenAccessor.

using System.Net;
using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoApiClientTests
{
    private readonly IAzdoTokenAccessor _mockToken;
    private readonly FakeHttpMessageHandler _handler;
    private readonly AzdoApiClient _client;

    public AzdoApiClientTests()
    {
        _mockToken = Substitute.For<IAzdoTokenAccessor>();
        _mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(BearerCredential("test-token"));
        _handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(_handler);
        _client = new AzdoApiClient(httpClient, _mockToken);
    }

    // ── URL Construction ─────────────────────────────────────────────

    [Fact]
    public async Task GetBuildAsync_BuildsCorrectUrl()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 42 });

        await _client.GetBuildAsync("dnceng", "internal", 42);

        Assert.NotNull(_handler.LastRequest);
        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://dev.azure.com/dnceng/internal/_apis/build/builds/42?api-version=7.0", url);
    }

    [Fact]
    public async Task GetBuildAsync_DifferentOrgProject_BuildsCorrectUrl()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("devdiv", "DevDiv", 99);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://dev.azure.com/devdiv/DevDiv/_apis/build/builds/99?api-version=7.0", url);
    }

    [Fact]
    public async Task GetBuildAsync_SpecialCharsInOrgProject_AreEscaped()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("my org", "my project", 1);

        // Uri.ToString() unescapes percent-encoded chars; AbsoluteUri preserves them
        var url = _handler.LastRequest!.RequestUri!.AbsoluteUri;
        Assert.Contains("my%20org", url);
        Assert.Contains("my%20project", url);
    }

    [Fact]
    public async Task GetTimelineAsync_BuildsCorrectUrl()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = "tid", records = Array.Empty<object>() });

        await _client.GetTimelineAsync("dnceng", "public", 100);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://dev.azure.com/dnceng/public/_apis/build/builds/100/timeline?api-version=7.0", url);
    }

    [Fact]
    public async Task GetBuildLogAsync_BuildsCorrectUrl()
    {
        _handler.ResponseContent = "log content here";

        await _client.GetBuildLogAsync("dnceng", "internal", 50, 7);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://dev.azure.com/dnceng/internal/_apis/build/builds/50/logs/7?api-version=7.0", url);
    }

    [Fact]
    public async Task GetBuildChangesAsync_BuildsCorrectUrl()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });

        await _client.GetBuildChangesAsync("dnceng", "internal", 42);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Equal("https://dev.azure.com/dnceng/internal/_apis/build/builds/42/changes?api-version=7.0", url);
    }

    [Fact]
    public async Task GetTestRunsAsync_BuildsCorrectUrlWithEscapedBuildUri()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });

        await _client.GetTestRunsAsync("dnceng", "internal", 42);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        // buildUri=vstfs:///Build/Build/42 must be URI-escaped
        Assert.Contains("buildUri=vstfs%3A%2F%2F%2FBuild%2FBuild%2F42", url);
        Assert.Contains("api-version=7.0", url);
    }

    [Fact]
    public async Task GetTestResultsAsync_BuildsCorrectUrl()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });

        await _client.GetTestResultsAsync("dnceng", "internal", 999);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("test/runs/999/results", url);
        Assert.Contains("$top=200", url);
        Assert.Contains("outcomes=Failed", url);
        Assert.Contains("api-version=7.0", url);
    }

    // ── ListBuildsAsync filter parameter construction ────────────────

    [Fact]
    public async Task ListBuildsAsync_EmptyFilter_IncludesQueryOrderAndApiVersion()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter();

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("queryOrder=queueTimeDescending", url);
        Assert.Contains("api-version=7.0", url);
        Assert.DoesNotContain("$top=", url);
        Assert.DoesNotContain("branchName=", url);
        Assert.DoesNotContain("definitions=", url);
        Assert.DoesNotContain("statusFilter=", url);
    }

    [Fact]
    public async Task ListBuildsAsync_PrNumber_SetsBranchNameToRefsPull()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter { PrNumber = "12345" };

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("branchName=refs/pull/12345/merge", url);
    }

    [Fact]
    public async Task ListBuildsAsync_Branch_SetsBranchNameEscaped()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter { Branch = "refs/heads/main" };

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("branchName=refs%2Fheads%2Fmain", url);
    }

    [Fact]
    public async Task ListBuildsAsync_PrNumberTakesPrecedenceOverBranch()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter { PrNumber = "100", Branch = "refs/heads/main" };

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("branchName=refs/pull/100/merge", url);
        Assert.DoesNotContain("refs/heads/main", url);
    }

    [Fact]
    public async Task ListBuildsAsync_DefinitionId_SetsDefinitions()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter { DefinitionId = 777 };

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("definitions=777", url);
    }

    [Fact]
    public async Task ListBuildsAsync_StatusFilter_SetsStatusFilter()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter { StatusFilter = "completed" };

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("statusFilter=completed", url);
    }

    [Fact]
    public async Task ListBuildsAsync_Top_SetsTopParameter()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter { Top = 10 };

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("$top=10", url);
    }

    [Fact]
    public async Task ListBuildsAsync_AllFilters_AllPresent()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        var filter = new AzdoBuildFilter
        {
            PrNumber = "42",
            DefinitionId = 100,
            Top = 5,
            StatusFilter = "inProgress"
        };

        await _client.ListBuildsAsync("dnceng", "internal", filter);

        var url = _handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("$top=5", url);
        Assert.Contains("branchName=refs/pull/42/merge", url);
        Assert.Contains("definitions=100", url);
        Assert.Contains("statusFilter=inProgress", url);
        Assert.Contains("queryOrder=queueTimeDescending", url);
    }

    // ── Error Handling ───────────────────────────────────────────────

    [Fact]
    public async Task GetBuildAsync_404_ReturnsNull()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.GetBuildAsync("dnceng", "internal", 999);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimelineAsync_404_ReturnsNull()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.GetTimelineAsync("dnceng", "internal", 999);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBuildLogAsync_404_ReturnsNull()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.GetBuildLogAsync("dnceng", "internal", 999, 1);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListBuildsAsync_404_ReturnsEmptyList()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.ListBuildsAsync("dnceng", "internal", new AzdoBuildFilter());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBuildChangesAsync_404_ReturnsEmptyList()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.GetBuildChangesAsync("dnceng", "internal", 999);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTestRunsAsync_404_ReturnsEmptyList()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.GetTestRunsAsync("dnceng", "internal", 999);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTestResultsAsync_404_ReturnsEmptyList()
    {
        _handler.StatusCode = HttpStatusCode.NotFound;

        var result = await _client.GetTestResultsAsync("dnceng", "internal", 999);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetBuildAsync_AuthFailure_ThrowsWithCredentialSourceAndActionableHints(HttpStatusCode statusCode)
    {
        _mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(BearerCredential("entra-token", "AzureCliCredential"));
        _handler.StatusCode = statusCode;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains("Can't access dnceng/internal", ex.Message);
        Assert.Contains(((int)statusCode).ToString(), ex.Message);
        Assert.Contains("Current auth: AzureCliCredential", ex.Message);
        Assert.Contains("az login", ex.Message);
        Assert.Contains("AZDO_TOKEN", ex.Message);
        Assert.Contains("Build(read) + Test(read)", ex.Message);
        _mockToken.Received(1).InvalidateCachedCredential();
    }

    [Fact]
    public async Task GetBuildAsync_SuccessfulAuthenticatedResponse_SetsAuthTokenHashFromCacheIdentity()
    {
        var cacheOptions = new CacheOptions();
        var credential = new AzdoCredential("entra-token", "Bearer", "AzureCliCredential")
        {
            DisplayToken = "entra-token",
            CacheIdentity = "AzureCliCredential:tenant-id:object-id:subject-id"
        };
        _mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(credential);
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });
        var client = new AzdoApiClient(new HttpClient(_handler), _mockToken, cacheOptions);

        await client.GetBuildAsync("dnceng", "internal", 1);

        Assert.Equal(CacheOptions.ComputeAuthContextHash(credential.CacheIdentity), cacheOptions.AuthTokenHash);
    }

    [Fact]
    public async Task ListBuildsAsync_401_ThrowsWithAuthHint()
    {
        _handler.StatusCode = HttpStatusCode.Unauthorized;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.ListBuildsAsync("dnceng", "internal", new AzdoBuildFilter()));

        Assert.Contains("Authentication failed", ex.Message);
    }

    [Fact]
    public async Task GetBuildLogAsync_401_ThrowsWithAuthHint()
    {
        _handler.StatusCode = HttpStatusCode.Unauthorized;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.GetBuildLogAsync("dnceng", "internal", 1, 1));

        Assert.Contains("Authentication failed", ex.Message);
    }

    [Fact]
    public async Task GetBuildAsync_500_ThrowsWithBodySnippet()
    {
        _handler.StatusCode = HttpStatusCode.InternalServerError;
        _handler.ResponseContent = "Internal server error details";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains("500", ex.Message);
        Assert.Contains("Internal server error details", ex.Message);
    }

    [Fact]
    public async Task GetBuildAsync_500_TruncatesBodyTo500Chars()
    {
        _handler.StatusCode = HttpStatusCode.InternalServerError;
        _handler.ResponseContent = new string('x', 1000);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.GetBuildAsync("dnceng", "internal", 1));

        // Body is truncated at 500 chars + ellipsis character
        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain(new string('x', 600), ex.Message);
        Assert.Contains("…", ex.Message);
    }

    [Fact]
    public async Task GetBuildAsync_500_ShortBodyNotTruncated()
    {
        _handler.StatusCode = HttpStatusCode.InternalServerError;
        _handler.ResponseContent = "short error";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.GetBuildAsync("dnceng", "internal", 1));

        Assert.Contains("short error", ex.Message);
        Assert.DoesNotContain("…", ex.Message);
    }

    [Fact]
    public async Task ListBuildsAsync_500_ThrowsWithBodySnippet()
    {
        _handler.StatusCode = HttpStatusCode.InternalServerError;
        _handler.ResponseContent = "server error";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.ListBuildsAsync("dnceng", "internal", new AzdoBuildFilter()));

        Assert.Contains("500", ex.Message);
        Assert.Contains("server error", ex.Message);
    }

    // ── Auth Header ──────────────────────────────────────────────────

    [Fact]
    public async Task Request_WithBearerCredential_SetsBearerAuthHeader()
    {
        _mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(BearerCredential("my-secret-token"));
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("dnceng", "internal", 1);

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal("Bearer", _handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("my-secret-token", _handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Request_WithBasicCredential_SetsBasicAuthHeader()
    {
        _mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(BasicCredential("my-pat-token"));
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("dnceng", "internal", 1);

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal("Basic", _handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(":my-pat-token")), _handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Request_WithNullCredential_NoAuthHeader()
    {
        _mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns((AzdoCredential?)null);
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("dnceng", "internal", 1);

        Assert.NotNull(_handler.LastRequest);
        Assert.Null(_handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task Request_WithEmptyCredentialToken_NoAuthHeader()
    {
        _mockToken.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(new AzdoCredential("", "Bearer", "Empty credential") { DisplayToken = "" });
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("dnceng", "internal", 1);

        Assert.NotNull(_handler.LastRequest);
        Assert.Null(_handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task TokenAccessor_CalledOncePerRequest()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("dnceng", "internal", 1);

        await _mockToken.Received(1).GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TokenAccessor_CalledForEachSeparateRequest()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await _client.GetBuildAsync("dnceng", "internal", 1);
        await _client.GetBuildAsync("dnceng", "internal", 2);

        await _mockToken.Received(2).GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }

    // ── JSON Deserialization ─────────────────────────────────────────

    [Fact]
    public async Task GetBuildAsync_DeserializesBuildWithJsonPropertyNames()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new
        {
            id = 42,
            buildNumber = "20240101.1",
            status = "completed",
            result = "succeeded",
            sourceBranch = "refs/heads/main",
            sourceVersion = "abc123"
        });

        var result = await _client.GetBuildAsync("dnceng", "internal", 42);

        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
        Assert.Equal("20240101.1", result.BuildNumber);
        Assert.Equal("completed", result.Status);
        Assert.Equal("succeeded", result.Result);
        Assert.Equal("refs/heads/main", result.SourceBranch);
        Assert.Equal("abc123", result.SourceVersion);
    }

    [Fact]
    public async Task GetBuildAsync_CaseInsensitive_HandlesUpperCase()
    {
        // PropertyNameCaseInsensitive = true means PascalCase JSON should deserialize too
        _handler.ResponseContent = """{"Id":7,"BuildNumber":"99.1","Status":"inProgress"}""";

        var result = await _client.GetBuildAsync("dnceng", "internal", 7);

        Assert.NotNull(result);
        Assert.Equal(7, result!.Id);
        Assert.Equal("99.1", result.BuildNumber);
        Assert.Equal("inProgress", result.Status);
    }

    [Fact]
    public async Task GetBuildAsync_NullOptionalFields_HandledGracefully()
    {
        _handler.ResponseContent = """{"id":1}""";

        var result = await _client.GetBuildAsync("dnceng", "internal", 1);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
        Assert.Null(result.BuildNumber);
        Assert.Null(result.Status);
        Assert.Null(result.Result);
        Assert.Null(result.Definition);
        Assert.Null(result.SourceBranch);
        Assert.Null(result.RequestedFor);
        Assert.Null(result.TriggerInfo);
        Assert.Null(result.QueueTime);
    }

    [Fact]
    public async Task GetBuildAsync_NestedObjects_Deserialized()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new
        {
            id = 1,
            definition = new { id = 200, name = "runtime" },
            requestedFor = new { displayName = "Larry" },
            triggerInfo = new Dictionary<string, string>
            {
                { "ci.message", "Merge pull request 42" },
                { "pr.number", "42" }
            }
        });

        var result = await _client.GetBuildAsync("dnceng", "internal", 1);

        Assert.NotNull(result);
        Assert.NotNull(result!.Definition);
        Assert.Equal(200, result.Definition!.Id);
        Assert.Equal("runtime", result.Definition.Name);
        Assert.NotNull(result.RequestedFor);
        Assert.Equal("Larry", result.RequestedFor!.DisplayName);
        Assert.NotNull(result.TriggerInfo);
        Assert.Equal("42", result.TriggerInfo!.PrNumber);
    }

    [Fact]
    public async Task ListBuildsAsync_UnwrapsAzdoListResponse()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = 1, buildNumber = "1.0" },
                new { id = 2, buildNumber = "2.0" }
            },
            count = 2
        });

        var result = await _client.ListBuildsAsync("dnceng", "internal", new AzdoBuildFilter());

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("2.0", result[1].BuildNumber);
    }

    [Fact]
    public async Task ListBuildsAsync_NullValueProperty_ReturnsEmptyList()
    {
        _handler.ResponseContent = """{"value":null,"count":0}""";

        var result = await _client.ListBuildsAsync("dnceng", "internal", new AzdoBuildFilter());

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListBuildsAsync_EmptyValue_ReturnsEmptyList()
    {
        _handler.ResponseContent = """{"value":[],"count":0}""";

        var result = await _client.ListBuildsAsync("dnceng", "internal", new AzdoBuildFilter());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTimelineAsync_DeserializesRecords()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new
        {
            id = "timeline-id",
            records = new[]
            {
                new { id = "r1", type = "Job", name = "Build", state = "completed", result = "succeeded" }
            }
        });

        var result = await _client.GetTimelineAsync("dnceng", "internal", 1);

        Assert.NotNull(result);
        Assert.Equal("timeline-id", result!.Id);
        Assert.Single(result.Records);
        Assert.Equal("Job", result.Records[0].Type);
        Assert.Equal("Build", result.Records[0].Name);
    }

    [Fact]
    public async Task GetBuildLogAsync_ReturnsRawStringContent()
    {
        _handler.ResponseContent = "line 1\nline 2\nline 3";

        var result = await _client.GetBuildLogAsync("dnceng", "internal", 1, 1);

        Assert.Equal("line 1\nline 2\nline 3", result);
    }

    [Fact]
    public async Task GetBuildChangesAsync_DeserializesChanges()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = "abc123", message = "fix bug", author = new { displayName = "Dev" } }
            },
            count = 1
        });

        var result = await _client.GetBuildChangesAsync("dnceng", "internal", 1);

        Assert.Single(result);
        Assert.Equal("abc123", result[0].Id);
        Assert.Equal("fix bug", result[0].Message);
        Assert.Equal("Dev", result[0].Author?.DisplayName);
    }

    [Fact]
    public async Task GetTestRunsAsync_DeserializesTestRuns()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = 10, name = "Run 1", state = "Completed", totalTests = 100, passedTests = 95, failedTests = 5 }
            },
            count = 1
        });

        var result = await _client.GetTestRunsAsync("dnceng", "internal", 42);

        Assert.Single(result);
        Assert.Equal(10, result[0].Id);
        Assert.Equal("Run 1", result[0].Name);
        Assert.Equal(100, result[0].TotalTests);
        Assert.Equal(5, result[0].FailedTests);
    }

    [Fact]
    public async Task GetTestResultsAsync_DeserializesTestResults()
    {
        _handler.ResponseContent = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    id = 1,
                    testCaseTitle = "MyTest",
                    outcome = "Failed",
                    durationInMs = 1234.5,
                    errorMessage = "assert failed",
                    stackTrace = "at MyTest.cs:10"
                }
            },
            count = 1
        });

        var result = await _client.GetTestResultsAsync("dnceng", "internal", 999);

        Assert.Single(result);
        Assert.Equal("MyTest", result[0].TestCaseTitle);
        Assert.Equal("Failed", result[0].Outcome);
        Assert.Equal(1234.5, result[0].DurationInMs);
        Assert.Equal("assert failed", result[0].ErrorMessage);
        Assert.Equal("at MyTest.cs:10", result[0].StackTrace);
    }

    [Fact]
    public async Task GetBuildAsync_ExtraJsonFields_IgnoredGracefully()
    {
        _handler.ResponseContent = """{"id":1,"buildNumber":"1.0","unknownField":"value","nested":{"foo":"bar"}}""";

        var result = await _client.GetBuildAsync("dnceng", "internal", 1);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
        Assert.Equal("1.0", result.BuildNumber);
    }

    // ── Constructor Validation ───────────────────────────────────────

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AzdoApiClient(null!, _mockToken));
    }

    [Fact]
    public void Constructor_NullTokenAccessor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AzdoApiClient(new HttpClient(), null!));
    }

    private static AzdoCredential BearerCredential(string token, string source = "Test credential")
        => new(token, "Bearer", source) { DisplayToken = token };

    private static AzdoCredential BasicCredential(string pat, string source = "AZDO_TOKEN (PAT)")
        => new(Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")), "Basic", source) { DisplayToken = pat };

    // ── FakeHttpMessageHandler ───────────────────────────────────────

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseContent { get; set; } = "{}";
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json")
            });
        }
    }
}
