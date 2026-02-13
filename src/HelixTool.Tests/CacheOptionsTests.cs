// Tests for CacheOptions.GetEffectiveCacheRoot() (L-CACHE-9).
// XDG path resolution, Windows default, explicit override.
// Will compile once Ripley's Cache/CacheOptions.cs is in place.

using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class CacheOptionsTests
{
    // =========================================================================
    // L-CACHE-9: CacheOptions.GetEffectiveCacheRoot()
    // =========================================================================

    [Fact]
    public void GetEffectiveCacheRoot_ExplicitCacheRoot_ReturnsExplicit()
    {
        var opts = new CacheOptions { CacheRoot = "/tmp/my-custom-cache" };

        var result = opts.GetEffectiveCacheRoot();

        // Explicit root + "public" subdirectory for unauthenticated context
        Assert.Equal(Path.Combine("/tmp/my-custom-cache", "public"), result);
    }

    [Fact]
    public void GetEffectiveCacheRoot_ExplicitWindowsPath_ReturnsExplicit()
    {
        var opts = new CacheOptions { CacheRoot = @"C:\Users\test\AppData\Local\hlx-custom" };

        var result = opts.GetEffectiveCacheRoot();

        Assert.Equal(Path.Combine(@"C:\Users\test\AppData\Local\hlx-custom", "public"), result);
    }

    [Fact]
    public void GetEffectiveCacheRoot_NullCacheRoot_ReturnsDefaultPath()
    {
        var opts = new CacheOptions { CacheRoot = null };

        var result = opts.GetEffectiveCacheRoot();

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        // The default path should contain "hlx" and end with "public" (unauthenticated)
        Assert.Contains("hlx", result);
        Assert.EndsWith("public", result);
    }

    [Fact]
    public void GetEffectiveCacheRoot_EmptyCacheRoot_ReturnsDefaultPath()
    {
        var opts = new CacheOptions { CacheRoot = "" };

        var result = opts.GetEffectiveCacheRoot();

        Assert.NotNull(result);
        Assert.Contains("hlx", result);
        Assert.EndsWith("public", result);
    }

    [Fact]
    public void GetEffectiveCacheRoot_DefaultOptions_PathContainsHlx()
    {
        var opts = new CacheOptions();

        var result = opts.GetEffectiveCacheRoot();

        Assert.Contains("hlx", result);
    }

    [Fact]
    public void GetEffectiveCacheRoot_OnWindows_UsesLocalAppData()
    {
        // This test only validates on Windows — skip on non-Windows
        if (!OperatingSystem.IsWindows())
            return;

        var opts = new CacheOptions();
        var result = opts.GetEffectiveCacheRoot();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expected = Path.Combine(localAppData, "hlx", "public");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetEffectiveCacheRoot_OnLinux_UsesXdgCacheHomeIfSet()
    {
        // This test only validates on non-Windows — skip on Windows
        if (OperatingSystem.IsWindows())
            return;

        // Save and override XDG_CACHE_HOME
        var original = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", "/tmp/xdg-test");
            var opts = new CacheOptions();

            var result = opts.GetEffectiveCacheRoot();

            Assert.Equal(Path.Combine("/tmp/xdg-test", "hlx", "public"), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", original);
        }
    }

    [Fact]
    public void GetEffectiveCacheRoot_OnLinux_FallsBackToHomeDotCache()
    {
        // This test only validates on non-Windows — skip on Windows
        if (OperatingSystem.IsWindows())
            return;

        // Clear XDG_CACHE_HOME to test the fallback
        var original = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", null);
            var opts = new CacheOptions();

            var result = opts.GetEffectiveCacheRoot();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var expected = Path.Combine(home, ".cache", "hlx", "public");
            Assert.Equal(expected, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", original);
        }
    }

    // =========================================================================
    // CacheOptions defaults
    // =========================================================================

    [Fact]
    public void DefaultOptions_MaxSizeBytes_Is1GB()
    {
        var opts = new CacheOptions();

        Assert.Equal(1L * 1024 * 1024 * 1024, opts.MaxSizeBytes);
    }

    [Fact]
    public void DefaultOptions_ArtifactMaxAge_Is7Days()
    {
        var opts = new CacheOptions();

        Assert.Equal(TimeSpan.FromDays(7), opts.ArtifactMaxAge);
    }

    [Fact]
    public void DefaultOptions_CacheRoot_IsNull()
    {
        var opts = new CacheOptions();

        Assert.Null(opts.CacheRoot);
    }

    [Fact]
    public void WithMaxSizeBytes_ZeroDisablesCache()
    {
        var opts = new CacheOptions { MaxSizeBytes = 0 };

        Assert.Equal(0, opts.MaxSizeBytes);
    }

    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var original = new CacheOptions();
        var modified = original with { MaxSizeBytes = 500 * 1024 * 1024 };

        Assert.Equal(1L * 1024 * 1024 * 1024, original.MaxSizeBytes);
        Assert.Equal(500L * 1024 * 1024, modified.MaxSizeBytes);
    }
}
