// Tests for IHelixTokenAccessor / EnvironmentHelixTokenAccessor (L-HTTP-1).
// EnvironmentHelixTokenAccessor wraps a fixed token — used by stdio/CLI transport.
// Will compile once Ripley's IHelixTokenAccessor.cs lands in HelixTool.Core.

using HelixTool.Core;
using HelixTool.Core.Helix;
using Xunit;

namespace HelixTool.Tests;

public class HelixTokenAccessorTests
{
    [Fact]
    public void GetAccessToken_ReturnsTokenPassedInConstructor()
    {
        var accessor = new EnvironmentHelixTokenAccessor("my-secret-token");

        var result = accessor.GetAccessToken();

        Assert.Equal("my-secret-token", result);
    }

    [Fact]
    public void GetAccessToken_NullToken_ReturnsNull()
    {
        var accessor = new EnvironmentHelixTokenAccessor(null);

        var result = accessor.GetAccessToken();

        Assert.Null(result);
    }

    [Fact]
    public void GetAccessToken_EmptyToken_ReturnsEmptyString()
    {
        var accessor = new EnvironmentHelixTokenAccessor("");

        var result = accessor.GetAccessToken();

        Assert.Equal("", result);
    }

    [Fact]
    public void GetAccessToken_CalledMultipleTimes_ReturnsSameValue()
    {
        var accessor = new EnvironmentHelixTokenAccessor("stable-token");

        var first = accessor.GetAccessToken();
        var second = accessor.GetAccessToken();

        Assert.Equal(first, second);
    }

    [Fact]
    public void ImplementsIHelixTokenAccessor()
    {
        IHelixTokenAccessor accessor = new EnvironmentHelixTokenAccessor("test");

        Assert.NotNull(accessor);
        Assert.Equal("test", accessor.GetAccessToken());
    }
}
