using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.Collect;

public sealed partial class IndependentReviewRegressionTests
{
    private const string HardErrorSecret = "SECRET-COLLECTOR-BOUNDARY-TOKEN";
    private const string HardErrorMessage =
        "AZDO_TOKEN=" + HardErrorSecret +
        " https://example.invalid/log?sig=SECRET-COLLECTOR-SAS\n" +
        "   at SecretProvider.Resolve() in /private/credentials.cs:line 42";

    public static IEnumerable<object[]> CollectorHardErrorCases()
    {
        foreach (var phase in new[] { "auth-resolution", "final-auth-status", "final-verification" })
        foreach (var defaultManifest in new[] { false, true })
        foreach (var cli in new[] { false, true })
        foreach (var failureKind in new[] { "unexpected", "provider-timeout" })
            yield return [phase, defaultManifest, cli, failureKind];
    }

    [Theory]
    [MemberData(nameof(CollectorHardErrorCases))]
    public async Task CollectorBoundaryFailure_PersistsSanitizedHardErrorManifest_Finding4170560963(
        string phase, bool defaultManifest, bool cli, string failureKind)
    {
        Exception failure = failureKind == "provider-timeout"
            ? new TaskCanceledException(HardErrorMessage)
            : new InvalidOperationException(HardErrorMessage);
        VerificationFailureCacheStore? verificationStore = null;
        using var fixture = new Fixture(collectorCacheStore: store =>
            verificationStore = new VerificationFailureCacheStore(store,
                phase == "final-verification" ? () => failure : null));
        ConfigureBoundaryFailure(fixture, phase, () => failure);
        var manifestPath = defaultManifest
            ? Path.Combine(fixture.Options.GetEffectiveCacheRoot(), "hlx-collect-manifest.json")
            : Path.Combine(Path.GetDirectoryName(fixture.Policy.ManifestPath!)!, "nested", "failure.json");
        fixture.Policy = fixture.Policy with { ManifestPath = manifestPath };

        CollectManifest manifest;
        if (cli)
        {
            manifest = await fixture.InvokeCliAsync(fixture.Options.CacheRoot, export: null,
                expectedExit: 1, useDefaultManifest: defaultManifest, helixScope: "none");
            AssertSanitizedHardError(fixture.LastStdout + fixture.LastStderr);
        }
        else
        {
            var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
            {
                ManifestPath = defaultManifest ? null : manifestPath,
                HelixScope = "none"
            });
            Assert.Equal(manifestPath, result.ManifestPath);
            manifest = result.Manifest;
        }

        Assert.True(File.Exists(manifestPath), $"Missing manifest after {phase}: {manifestPath}");
        var persistedText = await File.ReadAllTextAsync(manifestPath);
        using var persisted = JsonDocument.Parse(persistedText);
        Assert.False(persisted.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(1, persisted.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("hlx.collect.azdo-build", persisted.RootElement.GetProperty("kind").GetString());
        var source = persisted.RootElement.GetProperty("source");
        Assert.Equal(BuildId, source.GetProperty("buildId").GetInt32());
        Assert.Equal(Org, source.GetProperty("org").GetString());
        Assert.Equal(Project, source.GetProperty("project").GetString());
        Assert.Equal($"https://dev.azure.com/{Org}/{Project}/_build/results?buildId={BuildId}",
            source.GetProperty("buildUrl").GetString());
        Assert.Contains(persisted.RootElement.GetProperty("incompleteDetails").EnumerateArray(),
            detail => detail.GetProperty("code").GetString() == "collector_hard_error");
        AssertSanitizedHardError(persistedText);
        Assert.False(manifest.Complete);
        Assert.Equal(1, manifest.ExitCode);
        Assert.Contains(manifest.IncompleteDetails, detail => detail.Code == "collector_hard_error");
        AssertSanitizedHardError(JsonSerializer.Serialize(manifest));
        Assert.False(manifest.Snapshot.Exported);
        if (phase == "auth-resolution")
        {
            Assert.DoesNotContain(manifest.Attempts, attempt => attempt.Operation == "get_build");
            Assert.Empty(fixture.Handler.Requests);
        }
        else
        {
            Assert.Contains(manifest.Attempts, attempt => attempt.Operation == "get_build" && attempt.Outcome == "ok");
            Assert.NotEmpty(fixture.Handler.Requests);
        }
        Assert.Equal(phase == "final-verification" ? 1 : 0, verificationStore!.Failures);
    }

    [Theory]
    [InlineData("auth-resolution", false)]
    [InlineData("final-auth-status", false)]
    [InlineData("final-verification", false)]
    [InlineData("auth-resolution", true)]
    [InlineData("final-auth-status", true)]
    [InlineData("final-verification", true)]
    public async Task CollectorBoundaryCallerCancellation_PropagatesWithoutHardErrorManifest_Finding4170560963(
        string phase, bool cli)
    {
        using var cancellation = new CancellationTokenSource();
        Exception Cancel()
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        }
        using var fixture = new Fixture(collectorCacheStore: store =>
            new VerificationFailureCacheStore(store, phase == "final-verification" ? Cancel : null));
        ConfigureBoundaryFailure(fixture, phase, Cancel);
        var manifestPath = fixture.Policy.ManifestPath!;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (cli)
            {
                await fixture.InvokeCliAsync(fixture.Options.CacheRoot, export: null,
                    helixScope: "none", ct: cancellation.Token);
            }
            else
            {
                await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with { HelixScope = "none" },
                    ct: cancellation.Token);
            }
        });

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(manifestPath), "Caller cancellation must not create a hard-error manifest.");
        Assert.False(File.Exists(Path.Combine(fixture.Options.GetEffectiveCacheRoot(), "hlx-collect-manifest.json")));
    }

    private static void ConfigureBoundaryFailure(Fixture fixture, string phase, Func<Exception> failure)
    {
        if (phase == "auth-resolution")
        {
            fixture.AzdoToken.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<AzdoCredential?>(failure()));
        }
        else if (phase == "final-auth-status")
        {
            fixture.AzdoToken.AuthStatusAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<AzdoAuthStatus>(failure()));
        }
    }

    private static void AssertSanitizedHardError(string text)
    {
        Assert.DoesNotContain(HardErrorSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-COLLECTOR-SAS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretProvider.Resolve", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/credentials.cs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(InvalidOperationException), text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(TaskCanceledException), text, StringComparison.Ordinal);
    }

    private sealed class VerificationFailureCacheStore(ICacheStore inner, Func<Exception>? failure) : ICacheStore
    {
        public int Failures { get; private set; }

        public Task<string?> GetMetadataIgnoringTtlAsync(string cacheKey, CancellationToken ct = default)
        {
            if (failure is null)
                return inner.GetMetadataIgnoringTtlAsync(cacheKey, ct);
            Failures++;
            return Task.FromException<string?>(failure());
        }

        public Task<string?> GetMetadataAsync(string cacheKey, CancellationToken ct = default) =>
            inner.GetMetadataAsync(cacheKey, ct);
        public Task SetMetadataAsync(string cacheKey, string jsonValue, TimeSpan ttl, CancellationToken ct = default) =>
            inner.SetMetadataAsync(cacheKey, jsonValue, ttl, ct);
        public Task<Stream?> GetArtifactAsync(string cacheKey, CancellationToken ct = default) =>
            inner.GetArtifactAsync(cacheKey, ct);
        public Task SetArtifactAsync(string cacheKey, Stream content, CancellationToken ct = default) =>
            inner.SetArtifactAsync(cacheKey, content, ct);
        public Task<AcquisitionError?> GetAcquisitionErrorAsync(string cacheKey, CancellationToken ct = default) =>
            inner.GetAcquisitionErrorAsync(cacheKey, ct);
        public Task SetAcquisitionErrorAsync(string cacheKey, AcquisitionError error, TimeSpan ttl, CancellationToken ct = default) =>
            inner.SetAcquisitionErrorAsync(cacheKey, error, ttl, ct);
        public Task DeleteAcquisitionErrorAsync(string cacheKey, CancellationToken ct = default) =>
            inner.DeleteAcquisitionErrorAsync(cacheKey, ct);
        public Task<bool?> IsJobCompletedAsync(string jobId, CancellationToken ct = default) =>
            inner.IsJobCompletedAsync(jobId, ct);
        public Task SetJobCompletedAsync(string jobId, bool completed, TimeSpan ttl, CancellationToken ct = default) =>
            inner.SetJobCompletedAsync(jobId, completed, ttl, ct);
        public Task ClearAsync(CancellationToken ct = default) => inner.ClearAsync(ct);
        public Task<CacheStatus> GetStatusAsync(CancellationToken ct = default) => inner.GetStatusAsync(ct);
        public Task EvictExpiredAsync(CancellationToken ct = default) => inner.EvictExpiredAsync(ct);
        public void Dispose() { }
    }
}
