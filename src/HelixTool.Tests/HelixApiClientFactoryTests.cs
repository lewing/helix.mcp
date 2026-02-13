// Tests for IHelixApiClientFactory / HelixApiClientFactory (L-HTTP-2).
// Factory creates IHelixApiClient instances with specific auth tokens.
// Will compile once Ripley's IHelixApiClientFactory.cs lands in HelixTool.Core.

using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class HelixApiClientFactoryTests
{
    [Fact]
    public void Create_WithToken_ReturnsValidClient()
    {
        var factory = new HelixApiClientFactory();

        var client = factory.Create("fake-token-123");

        Assert.NotNull(client);
        Assert.IsAssignableFrom<IHelixApiClient>(client);
    }

    [Fact]
    public void Create_NullToken_ReturnsUnauthenticatedClient()
    {
        var factory = new HelixApiClientFactory();

        var client = factory.Create(null);

        Assert.NotNull(client);
        Assert.IsAssignableFrom<IHelixApiClient>(client);
    }

    [Fact]
    public void Create_DifferentTokens_ReturnsDifferentInstances()
    {
        var factory = new HelixApiClientFactory();

        var client1 = factory.Create("token-a");
        var client2 = factory.Create("token-b");

        Assert.NotSame(client1, client2);
    }

    [Fact]
    public void Create_SameToken_ReturnsDifferentInstances()
    {
        // Factory should always create new instances — caching is not its responsibility
        var factory = new HelixApiClientFactory();

        var client1 = factory.Create("same-token");
        var client2 = factory.Create("same-token");

        Assert.NotSame(client1, client2);
    }

    [Fact]
    public void ImplementsIHelixApiClientFactory()
    {
        IHelixApiClientFactory factory = new HelixApiClientFactory();

        Assert.NotNull(factory);
    }
}
