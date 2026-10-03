using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Mcp.Tools;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class AzdoPagingPr1CliTests
{
    private const string BuildId = "42";
    private static readonly string[] JsonArgs = ["--json"];

    public static IEnumerable<object[]> PagedCommands()
    {
        yield return ["azdo changes", "Changes", 20, CreateApiForChanges(21), new Dictionary<string, object?> { ["buildId"] = BuildId }];
        yield return ["azdo test-runs", "TestRuns", 50, CreateApiForTestRuns(51), new Dictionary<string, object?> { ["buildId"] = BuildId }];
        yield return ["azdo test-results", "TestResults", 200, CreateApiForTestResults(201), new Dictionary<string, object?> { ["buildId"] = BuildId, ["runId"] = 101 }];
        yield return ["azdo test-attachments", "TestAttachments", 100, CreateApiForTestAttachments(101), new Dictionary<string, object?> { ["org"] = "dnceng-public", ["project"] = "public", ["runId"] = 101, ["resultId"] = 202 }];
        yield return ["azdo artifacts", "Artifacts", 100, CreateApiForArtifacts(101), new Dictionary<string, object?> { ["buildId"] = BuildId, ["pattern"] = "*" }];
    }

    public static IEnumerable<object[]> SmallPagedCommands()
    {
        yield return ["azdo changes", "Changes", CreateApiForChanges(3), new Dictionary<string, object?> { ["buildId"] = BuildId }, "id", "change-2"];
        yield return ["azdo test-runs", "TestRuns", CreateApiForTestRuns(3), new Dictionary<string, object?> { ["buildId"] = BuildId }, "id", 2];
        yield return ["azdo test-results", "TestResults", CreateApiForTestResults(3), new Dictionary<string, object?> { ["buildId"] = BuildId, ["runId"] = 101 }, "id", 2];
        yield return ["azdo test-attachments", "TestAttachments", CreateApiForTestAttachments(3), new Dictionary<string, object?> { ["org"] = "dnceng-public", ["project"] = "public", ["runId"] = 101, ["resultId"] = 202 }, "id", 2];
        yield return ["azdo artifacts", "Artifacts", CreateApiForArtifacts(3), new Dictionary<string, object?> { ["buildId"] = BuildId, ["pattern"] = "*" }, "id", 2];
    }

    [Theory]
    [MemberData(nameof(PagedCommands))]
    public async Task JsonDefaultCappedOutput_ReportsTotalTruncatedNextAndExitTwo(
        string commandName,
        string methodName,
        int defaultLimit,
        IAzdoApiClient api,
        Dictionary<string, object?> requiredArgs)
    {
        Assert.NotNull(commandName);
        var commands = CreateCommands(api);

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            methodName,
            requiredArgs.With("json", true),
            JsonArgs);

        Assert.Null(thrown);
        Assert.Equal(2, exitCode);
        using var document = JsonDocument.Parse(stdout);
        AssertEnvelope(document.RootElement, returned: defaultLimit, total: defaultLimit + 1, offset: 0, limit: defaultLimit, complete: false, truncated: true);
        Assert.Equal(defaultLimit, document.RootElement.GetProperty("next").GetProperty("offset").GetInt32());
        Assert.Equal(defaultLimit, document.RootElement.GetProperty("next").GetProperty("limit").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("note").GetString()));
    }

    [Theory]
    [MemberData(nameof(PagedCommands))]
    public async Task JsonDefaultCappedOutput_WithAllowTruncated_ExitsZero(
        string _,
        string methodName,
        int defaultLimit,
        IAzdoApiClient api,
        Dictionary<string, object?> requiredArgs)
    {
        var commands = CreateCommands(api);

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            methodName,
            requiredArgs.With("json", true).With("allowTruncated", true),
            "--json",
            "--allow-truncated");

        Assert.Null(thrown);
        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        AssertEnvelope(document.RootElement, returned: defaultLimit, total: defaultLimit + 1, offset: 0, limit: defaultLimit, complete: false, truncated: true);
    }

    [Theory]
    [MemberData(nameof(SmallPagedCommands))]
    public async Task JsonOffsetLimit_SlicesAndReportsNext(
        string _,
        string methodName,
        IAzdoApiClient api,
        Dictionary<string, object?> requiredArgs,
        string identityProperty,
        object expectedIdentity)
    {
        var commands = CreateCommands(api);

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            methodName,
            requiredArgs.With("json", true).With("offset", 1).With("limit", 1),
            "--json",
            "--offset",
            "1",
            "--limit",
            "1");

        Assert.Null(thrown);
        Assert.Equal(2, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        AssertEnvelope(root, returned: 1, total: 3, offset: 1, limit: 1, complete: false, truncated: true);
        Assert.Equal(2, root.GetProperty("next").GetProperty("offset").GetInt32());
        var item = Assert.Single(root.GetProperty("results").EnumerateArray());
        AssertJsonValue(expectedIdentity, item.GetProperty(identityProperty));
    }

    [Theory]
    [MemberData(nameof(SmallPagedCommands))]
    public async Task JsonAll_ReturnsEverythingCompleteAndExitsZero(
        string _,
        string methodName,
        IAzdoApiClient api,
        Dictionary<string, object?> requiredArgs,
        string _identityProperty,
        object _expectedIdentity)
    {
        Assert.NotNull(_identityProperty);
        Assert.NotNull(_expectedIdentity);
        var commands = CreateCommands(api);

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            methodName,
            requiredArgs.With("json", true).With("all", true),
            "--json",
            "--all");

        Assert.Null(thrown);
        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        AssertEnvelope(document.RootElement, returned: 3, total: 3, offset: 0, limit: null, complete: true, truncated: false);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("next").ValueKind);
    }

    [Theory]
    [MemberData(nameof(SmallPagedCommands))]
    public async Task JsonAllWithOffsetOrLimit_IsRejected(
        string _,
        string methodName,
        IAzdoApiClient api,
        Dictionary<string, object?> requiredArgs,
        string _identityProperty,
        object _expectedIdentity)
    {
        Assert.NotNull(_identityProperty);
        Assert.NotNull(_expectedIdentity);
        var commands = CreateCommands(api);

        var (stdout, stderr, exitCode, thrown) = await CaptureCliAsync(
            commands,
            methodName,
            requiredArgs.With("json", true).With("all", true).With("limit", 1),
            "--json",
            "--all",
            "--limit",
            "1");

        Assert.True(
            thrown is ArgumentException or InvalidOperationException || exitCode == 1,
            $"Expected {methodName} to reject --all with --limit. exit={exitCode}, stdout={stdout}, stderr={stderr}, thrown={thrown}");
    }

    [Fact]
    public async Task TestResultsJson_PreservesExistingItemShapeInsideResultsEnvelope()
    {
        var commands = CreateCommands(CreateApiForTestResults(1));

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            "TestResults",
            new Dictionary<string, object?>
            {
                ["buildId"] = BuildId,
                ["runId"] = 101,
                ["json"] = true,
                ["all"] = true
            },
            "--json",
            "--all");

        Assert.Null(thrown);
        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var item = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(1, item.GetProperty("id").GetInt32());
        Assert.Equal("Test 1", item.GetProperty("testCaseTitle").GetString());
        Assert.Equal("Failed", item.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AzdoLogFullJson_ReturnsFullLogInsteadOfDefaultTail()
    {
        var api = Substitute.For<IAzdoApiClient>();
        var fullLog = string.Join('\n', Enumerable.Range(1, 650).Select(i => $"line {i:D3}"));
        api.GetBuildLogsListAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 7, LineCount = 650 } });
        api.GetBuildLogAsync("dnceng-public", "public", 42, 7, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(fullLog);
        var commands = CreateCommands(api);

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            "Log",
            new Dictionary<string, object?>
            {
                ["buildId"] = BuildId,
                ["logId"] = 7,
                ["full"] = true,
                ["json"] = true
            },
            "--json",
            "--full");

        Assert.Null(thrown);
        Assert.Equal(0, exitCode);
        Assert.Contains("line 001", stdout);
        Assert.Contains("line 650", stdout);
        await api.Received(1).GetBuildLogAsync("dnceng-public", "public", 42, 7, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JsonEnvelopeCacheProvenance_DoesNotLeakSecretTokenMaterial()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"hlx-paging-secrets-{Guid.NewGuid():N}");
        try
        {
            var inner = CreateApiForTestResults(3);
            using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = tempRoot });
            var cacheOptions = new CacheOptions { CacheRoot = tempRoot, MaxSizeBytes = 1024 * 1024 };
            var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
            tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns(new AzdoCredential("super-secret-token?sig=leak", "Bearer", "environment"));
            var caching = new CachingAzdoApiClient(inner, store, cacheOptions, tokenAccessor);
            var commands = new global::AzdoCommands(new AzdoService(caching), tokenAccessor);

            var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
                commands,
                "TestResults",
                new Dictionary<string, object?>
                {
                    ["buildId"] = BuildId,
                    ["runId"] = 101,
                    ["json"] = true,
                    ["all"] = true
                },
                "--json",
                "--all");

            Assert.Null(thrown);
            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("super-secret-token", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=leak", stdout, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout);
            Assert.True(document.RootElement.TryGetProperty("cache", out _));
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    [Fact]
    public async Task BuildChanges_FollowsContinuationTokensBeforeComplete_Finding4168886770()
    {
        var handler = ContinuationTokenHandler.NormalTwoPage();
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        using var httpClient = new HttpClient(handler);
        var client = new AzdoApiClient(httpClient, tokenAccessor);

        var changes = await client.GetBuildChangesAsync("dnceng-public", "public", 42);

        Assert.Equal(["change-1", "change-2"], changes.Select(change => change.Id).ToArray());
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("continuationToken=next-page", handler.RequestUris[1].Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildChanges_RepeatedContinuationToken_FailsInvalidResponse()
    {
        var handler = ContinuationTokenHandler.RepeatedToken();
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        using var httpClient = new HttpClient(handler);
        var client = new AzdoApiClient(httpClient, tokenAccessor);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => client.GetBuildChangesAsync("dnceng-public", "public", 42));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "azdo", "list_build_changes");
        AcquisitionAssertions.Resource(ex.Error, "pageCount", 2);
        AcquisitionAssertions.Resource(ex.Error, "continuationReason", "repeated_token");
    }

    [Fact]
    public async Task BuildChanges_ContinuationPageCap_FailsInvalidResponse()
    {
        var handler = ContinuationTokenHandler.UniqueTokensForever();
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        using var httpClient = new HttpClient(handler);
        var client = new AzdoApiClient(httpClient, tokenAccessor);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => client.GetBuildChangesAsync("dnceng-public", "public", 42));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "azdo", "list_build_changes");
        AcquisitionAssertions.Resource(ex.Error, "pageCount", AzdoApiClient.MaxContinuationPages + 1);
        AcquisitionAssertions.Resource(ex.Error, "continuationReason", "max_pages_exceeded");
        Assert.Equal(AzdoApiClient.MaxContinuationPages, handler.RequestUris.Count);
    }

    private static global::AzdoCommands CreateCommands(IAzdoApiClient api)
        => new(new AzdoService(api), Substitute.For<IAzdoTokenAccessor>());

    private static async Task<(string Stdout, string Stderr, int ExitCode, Exception? Thrown)> CaptureCliAsync(
        global::AzdoCommands commands,
        string methodName,
        IReadOnlyDictionary<string, object?> arguments,
        params string[] commandArguments)
    {
        await TestConsoleCapture.Lock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.ExitCode = 0;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Exception? thrown = null;
            try
            {
                await global::CliAcquisitionErrorPipeline.InvokeAsync(
                    _ => InvokeCommandAsync(commands, methodName, arguments),
                    commandArguments);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            return (stdout.ToString(), stderr.ToString(), Environment.ExitCode, thrown);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
            TestConsoleCapture.Lock.Release();
        }
    }

    private static async Task InvokeCommandAsync(
        global::AzdoCommands commands,
        string methodName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var method = typeof(global::AzdoCommands).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        AssertPagingParameters(method!, methodName, arguments);

        var values = BuildArguments(method!, arguments);
        try
        {
            var task = (Task?)method!.Invoke(commands, values);
            Assert.NotNull(task);
            await task!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void AssertPagingParameters(MethodInfo method, string methodName, IReadOnlyDictionary<string, object?> arguments)
    {
        var parameterNames = method.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var required in arguments.Keys.Where(k => k is "all" or "offset" or "limit" or "allowTruncated" or "full"))
            Assert.True(parameterNames.Contains(required), $"{methodName} must expose CLI parameter '{required}' from the paging design.");
    }

    private static object?[] BuildArguments(MethodInfo method, IReadOnlyDictionary<string, object?> explicitArguments)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.Name is not null && explicitArguments.TryGetValue(parameter.Name, out var value))
            {
                values[i] = value;
                continue;
            }

            if (parameter.Name is "buildIdOrUrl" && explicitArguments.TryGetValue("buildId", out value))
            {
                values[i] = value;
                continue;
            }

            if (parameter.Name is "buildId" && explicitArguments.TryGetValue("buildIdOrUrl", out value))
            {
                values[i] = value;
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue;
                continue;
            }

            throw new InvalidOperationException($"Missing required argument '{parameter.Name}' for {method.Name}.");
        }

        return values;
    }

    private static void AssertEnvelope(
        JsonElement root,
        int returned,
        int total,
        int offset,
        int? limit,
        bool complete,
        bool truncated)
    {
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(returned, root.GetProperty("returned").GetInt32());
        Assert.Equal(total, root.GetProperty("total").GetInt32());
        Assert.Equal(offset, root.GetProperty("offset").GetInt32());
        if (limit.HasValue)
            Assert.Equal(limit.Value, root.GetProperty("limit").GetInt32());
        else
            Assert.Equal(JsonValueKind.Null, root.GetProperty("limit").ValueKind);
        Assert.Equal(complete, root.GetProperty("complete").GetBoolean());
        Assert.Equal(truncated, root.GetProperty("truncated").GetBoolean());
        Assert.Equal(returned, root.GetProperty("results").GetArrayLength());
        Assert.True(root.TryGetProperty("cache", out var cache));
        Assert.True(cache.TryGetProperty("completeKey", out _));
    }

    private static void AssertJsonValue(object expected, JsonElement actual)
    {
        switch (expected)
        {
            case int i:
                Assert.Equal(i, actual.GetInt32());
                break;
            case string s:
                Assert.Equal(s, actual.GetString());
                break;
            default:
                throw new NotSupportedException(expected.GetType().FullName);
        }
    }

    private static IAzdoApiClient CreateApiForChanges(int count)
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildChangesAsync("dnceng-public", "public", 42, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, count)
                .Select(i => new AzdoBuildChange { Id = $"change-{i}", Message = $"Change {i}" })
                .ToList());
        return api;
    }

    private static IAzdoApiClient CreateApiForTestRuns(int count)
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetTestRunsAsync("dnceng-public", "public", 42, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, count)
                .Select(i => new AzdoTestRun { Id = i, Name = $"Run {i}", State = "completed", TotalTests = i })
                .ToList());
        return api;
    }

    private static IAzdoApiClient CreateApiForTestResults(int count)
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetTestResultsAsync("dnceng-public", "public", 101, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, count)
                .Select(i => new AzdoTestResult { Id = i, TestCaseTitle = $"Test {i}", Outcome = "Failed" })
                .ToList());
        return api;
    }

    private static IAzdoApiClient CreateApiForTestAttachments(int count)
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetTestAttachmentsAsync("dnceng-public", "public", 101, 202, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, count)
                .Select(i => new AzdoTestAttachment { Id = i, FileName = $"attachment-{i}.log", Size = i })
                .ToList());
        return api;
    }

    private static IAzdoApiClient CreateApiForArtifacts(int count)
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildArtifactsAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, count)
                .Select(i => new AzdoBuildArtifact { Id = i, Name = $"artifact-{i}" })
                .ToList());
        return api;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed class ContinuationTokenHandler : HttpMessageHandler
    {
        private readonly Func<int, string?> _tokenForRequest;

        private ContinuationTokenHandler(Func<int, string?> tokenForRequest)
        {
            _tokenForRequest = tokenForRequest;
        }

        public List<Uri> RequestUris { get; } = [];

        public static ContinuationTokenHandler NormalTwoPage() =>
            new(requestNumber => requestNumber == 1 ? "next-page" : null);

        public static ContinuationTokenHandler RepeatedToken() =>
            new(requestNumber => requestNumber <= 2 ? "same-page" : null);

        public static ContinuationTokenHandler UniqueTokensForever() =>
            new(requestNumber => $"page-{requestNumber}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var requestNumber = RequestUris.Count;
            var page = $$"""{"count":1,"value":[{"id":"change-{{requestNumber}}","message":"page {{requestNumber}}"}]}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page, Encoding.UTF8, "application/json")
            };
            var token = _tokenForRequest(requestNumber);
            if (!string.IsNullOrWhiteSpace(token))
                response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", token);
            return Task.FromResult(response);
        }
    }
}

[Collection("AzdoTokenEnv")]
public sealed class AzdoPagingPr1CacheCompatibilityTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"hlx-paging-cache-{Guid.NewGuid():N}");

    public void Dispose() => TryDelete(_cacheRoot);

    [Fact]
    public async Task TestResultsAllCli_WritesCompleteCollectionKey()
    {
        var inner = CreateApiForTestResults(3);
        using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = _cacheRoot });
        var cacheOptions = new CacheOptions { CacheRoot = _cacheRoot, MaxSizeBytes = 1024 * 1024 };
        var caching = new CachingAzdoApiClient(inner, store, cacheOptions);
        var commands = new global::AzdoCommands(new AzdoService(caching), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            "TestResults",
            new Dictionary<string, object?>
            {
                ["buildId"] = "42",
                ["runId"] = 101,
                ["json"] = true,
                ["all"] = true
            },
            "--json",
            "--all");

        Assert.Null(thrown);
        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var completeKey = document.RootElement.GetProperty("cache").GetProperty("completeKey").GetString();
        Assert.Equal("azdo:dnceng-public:public:testresults:v3:101:Failed:all", completeKey);
        Assert.NotNull(await store.GetMetadataAsync(completeKey!));
    }

    [Fact]
    public async Task JsonEnvelopeCacheKey_PointsToPersistedEntry_Finding4168886873()
    {
        var inner = CreateApiForTestResults(3);
        using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = _cacheRoot });
        var cacheOptions = new CacheOptions { CacheRoot = _cacheRoot, MaxSizeBytes = 1024 * 1024 };
        var caching = new CachingAzdoApiClient(inner, store, cacheOptions);
        var commands = new global::AzdoCommands(new AzdoService(caching), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            "TestResults",
            new Dictionary<string, object?>
            {
                ["buildId"] = "42",
                ["runId"] = 101,
                ["json"] = true,
                ["offset"] = 1,
                ["limit"] = 1,
                ["allowTruncated"] = true
            },
            "--json",
            "--offset",
            "1",
            "--limit",
            "1",
            "--allow-truncated");

        Assert.Null(thrown);
        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var cacheKey = document.RootElement.GetProperty("cache").GetProperty("key").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cacheKey));
        Assert.NotNull(await store.GetMetadataAsync(cacheKey!));
    }

    [Fact]
    public async Task EvalModeMcpDefaultTestResults_SlicesFromCompleteKeyWithoutExactLegacyKey()
    {
        var complete = Enumerable.Range(1, 250)
            .Select(i => new AzdoTestResult { Id = i, TestCaseTitle = $"Test {i}", Outcome = "Failed" })
            .ToList();
        var snapshotRoot = await SeedSnapshotAsync(async store =>
        {
            await store.SetMetadataAsync(
                "azdo:dnceng-public:public:testresults:v3:101:Failed:all",
                JsonSerializer.Serialize(complete),
                TimeSpan.FromHours(1));
        });

        using var evalStore = new SqliteCacheStore(new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var tools = new AzdoMcpTools(new AzdoService(client), Substitute.For<IAzdoTokenAccessor>(), Substitute.For<HelixTool.Core.Delivery.IEvidenceDeliveryStore>());

        var result = await tools.TestResults("42", 101);

        Assert.Equal(200, result.Count);
        Assert.True(result.Truncated);
        Assert.Equal(1, result[0].Id);
        Assert.Equal(200, result[199].Id);
    }

    [Fact]
    public async Task EvalModeMcpDefaultTestResults_CompleteKeyUnderDefaultLimitIsNotTruncated()
    {
        var complete = Enumerable.Range(1, 150)
            .Select(i => new AzdoTestResult { Id = i, TestCaseTitle = $"Test {i}", Outcome = "Failed" })
            .ToList();
        var snapshotRoot = await SeedSnapshotAsync(async store =>
        {
            await store.SetMetadataAsync(
                "azdo:dnceng-public:public:testresults:v3:101:Failed:all",
                JsonSerializer.Serialize(complete),
                TimeSpan.FromHours(1));
        });

        using var evalStore = new SqliteCacheStore(new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var tools = new AzdoMcpTools(new AzdoService(client), Substitute.For<IAzdoTokenAccessor>(), Substitute.For<HelixTool.Core.Delivery.IEvidenceDeliveryStore>());

        var result = await tools.TestResults("42", 101);

        Assert.Equal(150, result.Count);
        Assert.False(result.Truncated);
        Assert.Equal(1, result[0].Id);
        Assert.Equal(150, result[149].Id);
    }

    [Fact]
    public async Task EvalModeMcpDefaultTestResults_ReplaysLegacyCappedKeyWhenCompleteKeyAbsent()
    {
        var legacy = Enumerable.Range(1, 2)
            .Select(i => new AzdoTestResult { Id = i, TestCaseTitle = $"Legacy {i}", Outcome = "Failed" })
            .ToList();
        var snapshotRoot = await SeedSnapshotAsync(async store =>
        {
            await store.SetMetadataAsync(
                "azdo:dnceng-public:public:testresults:v2:101:200:Failed",
                JsonSerializer.Serialize(legacy),
                TimeSpan.FromHours(1));
        });

        using var evalStore = new SqliteCacheStore(new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var tools = new AzdoMcpTools(new AzdoService(client), Substitute.For<IAzdoTokenAccessor>(), Substitute.For<HelixTool.Core.Delivery.IEvidenceDeliveryStore>());

        var result = await tools.TestResults("42", 101);

        Assert.Equal(2, result.Count);
        Assert.Equal("Legacy 1", result[0].TestCaseTitle);
    }

    [Fact]
    public async Task EvalModeMcpDefaultTestResults_ReplaysRecordedFailureBeforeNotInSnapshot()
    {
        var snapshotRoot = await SeedSnapshotAsync(async store =>
        {
            await store.SetAcquisitionErrorAsync(
                "azdo:dnceng-public:public:testresults:v3:101:Failed:all",
                AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.NotFound,
                    "azdo",
                    "list_test_results",
                    new Dictionary<string, object?> { ["runId"] = 101 },
                    "Recorded provider failure."),
                TimeSpan.FromHours(1));
        });

        using var evalStore = new SqliteCacheStore(new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var tools = new AzdoMcpTools(new AzdoService(client), Substitute.For<IAzdoTokenAccessor>(), Substitute.For<HelixTool.Core.Delivery.IEvidenceDeliveryStore>());

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => tools.TestResults("42", 101));

        Assert.Equal(AcquisitionErrorKind.NotFound, ex.Error.Kind);
        Assert.Equal("azdo", ex.Error.Provider);
        Assert.Equal("snapshot", ex.Error.Source);
    }

    [Fact]
    public async Task EvalModeCliAllTestResults_WithOnlyCappedLiveKey_FailsNotInSnapshot()
    {
        var capped = Enumerable.Range(1, 200)
            .Select(i => new AzdoTestResult { Id = i, TestCaseTitle = $"Capped {i}", Outcome = "Failed" })
            .ToList();
        var snapshotRoot = await SeedSnapshotAsync(async store =>
        {
            await store.SetMetadataAsync(
                "azdo:dnceng-public:public:testresults:v2:101:200:Failed",
                JsonSerializer.Serialize(capped),
                TimeSpan.FromHours(1));
        });

        using var evalStore = new SqliteCacheStore(new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var commands = new global::AzdoCommands(new AzdoService(client), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode, thrown) = await CaptureCliAsync(
            commands,
            "TestResults",
            new Dictionary<string, object?>
            {
                ["buildId"] = "42",
                ["runId"] = 101,
                ["json"] = true,
                ["all"] = true
            },
            "--json",
            "--all");

        Assert.Null(thrown);
        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("not_in_snapshot", error.GetProperty("kind").GetString());
        Assert.Equal("cache", error.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task AzdoLogFull_CachedFullLogServesOfflineMcpDefaultTail()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var fullLog = string.Join('\n', Enumerable.Range(1, 650).Select(i => $"line {i:D3}"));
        inner.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });
        inner.GetBuildLogsListAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 7, LineCount = 650 } });
        inner.GetBuildLogAsync("dnceng-public", "public", 42, 7, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(fullLog);

        var liveOptions = new CacheOptions { CacheRoot = _cacheRoot, MaxSizeBytes = 1024 * 1024 };
        using (var liveStore = new SqliteCacheStore(liveOptions))
        {
            var liveClient = new CachingAzdoApiClient(inner, liveStore, liveOptions);
            var liveCommands = new global::AzdoCommands(new AzdoService(liveClient), Substitute.For<IAzdoTokenAccessor>());
            var (_, _, exitCode, thrown) = await CaptureCliAsync(
                liveCommands,
                "Log",
                new Dictionary<string, object?>
                {
                    ["buildId"] = "42",
                    ["logId"] = 7,
                    ["json"] = true,
                    ["full"] = true
                },
                "--json",
                "--full");
            Assert.Null(thrown);
            Assert.Equal(0, exitCode);
        }

        var snapshotRoot = Path.Combine(_cacheRoot, "public");
        using var evalStore = new SqliteCacheStore(new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var evalClient = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, new CacheOptions { CacheRoot = snapshotRoot, EvalMode = true });
        var tools = new AzdoMcpTools(new AzdoService(evalClient), Substitute.For<IAzdoTokenAccessor>(), Substitute.For<HelixTool.Core.Delivery.IEvidenceDeliveryStore>());

        var tail = await tools.Log("42", 7);

        Assert.DoesNotContain("line 001", tail, StringComparison.Ordinal);
        Assert.Contains("line 151", tail, StringComparison.Ordinal);
        Assert.Contains("line 650", tail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestResultsContinuationWithSkip_DoesNotDuplicateRowsOrOverfillWindowCache_Finding4169759234()
    {
        var directHandler = new TestResultsContinuationSkipHandler();
        var directClient = new AzdoApiClient(new HttpClient(directHandler), Substitute.For<IAzdoTokenAccessor>());

        var complete = await directClient.GetTestResultsAsync("dnceng-public", "public", 101, top: 10_001);

        Assert.Equal(10_001, complete.Count);
        Assert.Equal(10_001, complete.Select(result => result.Id).Distinct().Count());
        Assert.Equal(10_001, complete[^1].Id);
        Assert.DoesNotContain(
            directHandler.RequestUris,
            uri => uri.Query.Contains("$skip=10000", StringComparison.OrdinalIgnoreCase)
                   || uri.Query.Contains("%24skip=10000", StringComparison.OrdinalIgnoreCase));

        var cappedHandler = new TestResultsContinuationSkipHandler();
        var cacheOptions = new CacheOptions { CacheRoot = Path.Combine(_cacheRoot, "continuation-window"), MaxSizeBytes = 1024 * 1024 * 16 };
        using var store = new SqliteCacheStore(cacheOptions);
        var cachedClient = new CachingAzdoApiClient(
            new AzdoApiClient(new HttpClient(cappedHandler), Substitute.For<IAzdoTokenAccessor>()),
            store,
            cacheOptions);

        var capped = await cachedClient.GetTestResultsAsync("dnceng-public", "public", 101, top: 10_000);

        Assert.Equal(10_000, capped.Count);
        Assert.DoesNotContain(cappedHandler.RequestUris, uri => uri.Query.Contains("continuationToken=", StringComparison.OrdinalIgnoreCase));
        var windowKey = AzdoListCacheKeys.TestResultsWindow(cacheOptions, "dnceng-public", "public", 101, null, 0, 10_000);
        var completeKey = AzdoListCacheKeys.TestResultsComplete(cacheOptions, "dnceng-public", "public", 101, null);
        var windowJson = await store.GetMetadataAsync(windowKey);
        Assert.NotNull(windowJson);
        Assert.Equal(10_000, JsonSerializer.Deserialize<List<AzdoTestResult>>(windowJson!)?.Count);
        Assert.Null(await store.GetMetadataAsync(completeKey));
    }

    private async Task<string> SeedSnapshotAsync(Func<SqliteCacheStore, Task> seedAsync)
    {
        var liveRoot = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
        using (var store = new SqliteCacheStore(new CacheOptions { CacheRoot = liveRoot }))
        {
            await seedAsync(store);
        }

        return Path.Combine(liveRoot, "public");
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode, Exception? Thrown)> CaptureCliAsync(
        global::AzdoCommands commands,
        string methodName,
        IReadOnlyDictionary<string, object?> arguments,
        params string[] commandArguments)
        => await AzdoPagingPr1CliTestsCapture.InvokeAsync(commands, methodName, arguments, commandArguments);

    private static IAzdoApiClient CreateApiForTestResults(int count)
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetTestResultsAsync("dnceng-public", "public", 101, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, count)
                .Select(i => new AzdoTestResult { Id = i, TestCaseTitle = $"Test {i}", Outcome = "Failed" })
                .ToList());
        return api;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed class TestResultsContinuationSkipHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var query = request.RequestUri!.Query;
            var isContinuation = query.Contains("continuationToken=", StringComparison.OrdinalIgnoreCase);
            var isSkipWindow = query.Contains("$skip=10000", StringComparison.OrdinalIgnoreCase)
                               || query.Contains("%24skip=10000", StringComparison.OrdinalIgnoreCase);
            var body = isContinuation || isSkipWindow
                ? TestResultsJson(start: 10_001, count: 1)
                : TestResultsJson(start: 1, count: 10_000);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (!isContinuation && !isSkipWindow)
                response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", "second-page");
            return Task.FromResult(response);
        }

        private static string TestResultsJson(int start, int count)
        {
            var values = Enumerable.Range(start, count)
                .Select(id => $$"""{"id":{{id}},"testCaseTitle":"Test {{id}}","outcome":"Failed"}""");
            return $$"""{"count":{{count}},"value":[{{string.Join(",", values)}}]}""";
        }
    }
}

internal static class AzdoPagingPr1CliTestsCapture
{
    public static async Task<(string Stdout, string Stderr, int ExitCode, Exception? Thrown)> InvokeAsync(
        global::AzdoCommands commands,
        string methodName,
        IReadOnlyDictionary<string, object?> arguments,
        params string[] commandArguments)
    {
        await TestConsoleCapture.Lock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.ExitCode = 0;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Exception? thrown = null;
            try
            {
                await global::CliAcquisitionErrorPipeline.InvokeAsync(
                    _ => InvokeCommandAsync(commands, methodName, arguments),
                    commandArguments);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            return (stdout.ToString(), stderr.ToString(), Environment.ExitCode, thrown);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
            TestConsoleCapture.Lock.Release();
        }
    }

    private static async Task InvokeCommandAsync(
        global::AzdoCommands commands,
        string methodName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var method = typeof(global::AzdoCommands).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var values = BuildArguments(method!, arguments);
        try
        {
            var task = (Task?)method!.Invoke(commands, values);
            Assert.NotNull(task);
            await task!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static object?[] BuildArguments(MethodInfo method, IReadOnlyDictionary<string, object?> explicitArguments)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.Name is not null && explicitArguments.TryGetValue(parameter.Name, out var value))
            {
                values[i] = value;
                continue;
            }

            if (parameter.Name is "buildIdOrUrl" && explicitArguments.TryGetValue("buildId", out value))
            {
                values[i] = value;
                continue;
            }

            if (parameter.Name is "buildId" && explicitArguments.TryGetValue("buildIdOrUrl", out value))
            {
                values[i] = value;
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue;
                continue;
            }

            throw new InvalidOperationException($"Missing required argument '{parameter.Name}' for {method.Name}.");
        }

        foreach (var required in explicitArguments.Keys.Where(k => k is "all" or "offset" or "limit" or "allowTruncated" or "full"))
            Assert.Contains(parameters, p => p.Name == required);

        return values;
    }
}

internal static class DictionaryExtensions
{
    public static Dictionary<string, object?> With(
        this Dictionary<string, object?> source,
        string key,
        object? value)
    {
        var copy = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        copy[key] = value;
        return copy;
    }
}
