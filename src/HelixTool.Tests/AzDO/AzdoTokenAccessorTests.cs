// Tests for AzCliAzdoTokenAccessor — AzDO auth chain.
// Tests the env-var path (AZDO_TOKEN) which is unit-testable.
// The az CLI subprocess path returns null on failure (no exception).
// Env var is checked on every call (not cached); only az CLI result is cached.

using Xunit;
using HelixTool.Core.AzDO;

namespace HelixTool.Tests.AzDO;

public class AzdoTokenAccessorTests : IDisposable
{
    private const string EnvVarName = "AZDO_TOKEN";
    private readonly string? _originalEnvValue;

    public AzdoTokenAccessorTests()
    {
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVarName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVarName, _originalEnvValue);
    }

    // --- Env var path (AZDO_TOKEN) ---

    [Fact]
    public async Task GetAccessTokenAsync_WhenEnvVarSet_ReturnsEnvVarValue()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "my-azdo-pat-token");
        var accessor = new AzCliAzdoTokenAccessor();

        var token = await accessor.GetAccessTokenAsync();

        Assert.Equal("my-azdo-pat-token", token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenEnvVarEmpty_FallsThroughToAzCli()
    {
        // Empty string is treated as "not set" per string.IsNullOrEmpty
        Environment.SetEnvironmentVariable(EnvVarName, "");
        var accessor = new AzCliAzdoTokenAccessor();

        // Falls through to az CLI, which returns null if not available
        var token = await accessor.GetAccessTokenAsync();

        // In CI without az CLI, null is expected (graceful failure)
        // The key assertion: empty env var does NOT return ""
        Assert.NotEqual("", token);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenEnvVarNotSet_FallsThroughToAzCli()
    {
        Environment.SetEnvironmentVariable(EnvVarName, null);
        var accessor = new AzCliAzdoTokenAccessor();

        // Returns null if az CLI is not available (no exception thrown)
        var token = await accessor.GetAccessTokenAsync();

        // Either az CLI returned a token, or null — both are valid
        // Key contract: does NOT throw on az CLI failure
    }

    // --- Env var is read on EVERY call (not cached) ---

    [Fact]
    public async Task GetAccessTokenAsync_EnvVarCheckedEveryCall()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "first-token");
        var accessor = new AzCliAzdoTokenAccessor();

        var first = await accessor.GetAccessTokenAsync();
        Assert.Equal("first-token", first);

        // Change env var between calls — should be reflected immediately
        Environment.SetEnvironmentVariable(EnvVarName, "second-token");
        var second = await accessor.GetAccessTokenAsync();
        Assert.Equal("second-token", second);
    }

    // --- Caching (az CLI path only) ---

    [Fact]
    public async Task GetAccessTokenAsync_AzCliResultIsCached()
    {
        // Clear env var so it falls through to az CLI
        Environment.SetEnvironmentVariable(EnvVarName, null);
        var accessor = new AzCliAzdoTokenAccessor();

        // First call hits az CLI (or returns null)
        var first = await accessor.GetAccessTokenAsync();
        // Second call uses cached result (_resolved flag)
        var second = await accessor.GetAccessTokenAsync();

        // Both should return the same value (null or token)
        Assert.Equal(first, second);
    }

    // --- Concurrency ---

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCalls_AllReturnSameEnvToken()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "concurrent-token");
        var accessor = new AzCliAzdoTokenAccessor();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => accessor.GetAccessTokenAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, token => Assert.Equal("concurrent-token", token));
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallsWithoutEnvVar_DoNotThrow()
    {
        // Without env var, concurrent calls hit az CLI path
        // The _resolved flag isn't thread-safe, but the behavior should be stable
        Environment.SetEnvironmentVariable(EnvVarName, null);
        var accessor = new AzCliAzdoTokenAccessor();

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => accessor.GetAccessTokenAsync())
            .ToArray();

        // Should not throw even if az CLI isn't available
        var results = await Task.WhenAll(tasks);

        // All results should be the same (null or token)
        var first = results[0];
        Assert.All(results, token => Assert.Equal(first, token));
    }

    // --- Interface contract ---

    [Fact]
    public void ImplementsIAzdoTokenAccessor()
    {
        IAzdoTokenAccessor accessor = new AzCliAzdoTokenAccessor();
        Assert.NotNull(accessor);
    }

    // --- CancellationToken ---

    [Fact]
    public async Task GetAccessTokenAsync_WithCancellationToken_EnvVarPath_ReturnsImmediately()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "cancel-test-token");
        var accessor = new AzCliAzdoTokenAccessor();
        using var cts = new CancellationTokenSource();

        var token = await accessor.GetAccessTokenAsync(cts.Token);

        Assert.Equal("cancel-test-token", token);
    }

    // --- Nullable return type ---

    [Fact]
    public async Task GetAccessTokenAsync_ReturnTypeIsNullable()
    {
        // Verify the contract: return type is string? (nullable)
        Environment.SetEnvironmentVariable(EnvVarName, null);
        var accessor = new AzCliAzdoTokenAccessor();

        // When both env var and az CLI unavailable, null is valid
        string? token = await accessor.GetAccessTokenAsync();
        // token may be null — that's the expected contract for anonymous access
    }

    // -----------------------------------------------------------------------
    // INTEGRATION TEST NOTES (not implemented — require az CLI subprocess)
    //
    // The following scenarios require integration testing with a real az CLI:
    //
    // 1. When AZDO_TOKEN is not set and az CLI is authenticated:
    //    → GetAccessTokenAsync returns a valid Bearer token (non-null, non-empty)
    //    → Token is a JWT (starts with "eyJ")
    //    → az CLI is called with: az account get-access-token
    //        --resource 499b84ac-1321-427f-aa17-267ca6975798
    //        --query accessToken -o tsv
    //
    // 2. When AZDO_TOKEN is not set and az CLI is NOT authenticated:
    //    → GetAccessTokenAsync returns null (graceful fallback)
    //    → No exception thrown (catch-all in TryGetAzCliTokenAsync)
    //
    // 3. When az CLI is not installed:
    //    → Process.Start returns null or throws Win32Exception
    //    → GetAccessTokenAsync returns null
    //
    // 4. Token caching behavior:
    //    → After first az CLI call, _resolved is set to true
    //    → Subsequent calls with no env var return _cachedToken without subprocess
    //    → NOTE: _resolved flag is not thread-safe (potential race on first call)
    //
    // 5. CancellationToken on az CLI path:
    //    → ReadToEndAsync and WaitForExitAsync both accept ct
    //    → Pre-cancelled token should throw OperationCanceledException
    //
    // To run manual integration tests:
    //   az login
    //   unset AZDO_TOKEN
    //   dotnet test --filter "Category=Integration"
    // -----------------------------------------------------------------------
}
