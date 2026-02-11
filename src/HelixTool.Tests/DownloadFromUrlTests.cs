using Xunit;
using HelixTool.Core;
using NSubstitute;

namespace HelixTool.Tests;

public class DownloadFromUrlTests
{
    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public DownloadFromUrlTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    [Fact]
    public async Task DownloadFromUrlAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _svc.DownloadFromUrlAsync(null!));
    }

    [Fact]
    public async Task DownloadFromUrlAsync_ThrowsOnEmpty()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _svc.DownloadFromUrlAsync(""));
    }

    [Fact]
    public async Task DownloadFromUrlAsync_ThrowsOnWhitespace()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _svc.DownloadFromUrlAsync("   "));
    }
}
