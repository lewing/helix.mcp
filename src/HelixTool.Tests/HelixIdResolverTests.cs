using Xunit;
using HelixTool.Core;

namespace HelixTool.Tests;

public class HelixIdResolverTests
{
    [Fact]
    public void ResolveJobId_BareGuidWithDashes_ReturnsSameGuid()
    {
        var guid = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        Assert.Equal(guid, HelixIdResolver.ResolveJobId(guid));
    }

    [Fact]
    public void ResolveJobId_BareGuidNoDashes_ReturnsSameInput()
    {
        var guid = "d1f9a7c32b4e4f8a9c0de5f6a7b8c9d0";
        Assert.Equal(guid, HelixIdResolver.ResolveJobId(guid));
    }

    [Fact]
    public void ResolveJobId_UpperCaseGuid_ReturnsSameInput()
    {
        var guid = "D1F9A7C3-2B4E-4F8A-9C0D-E5F6A7B8C9D0";
        Assert.Equal(guid, HelixIdResolver.ResolveJobId(guid));
    }

    [Fact]
    public void ResolveJobId_HelixUrl_ExtractsGuid()
    {
        var guid = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        var url = $"https://helix.dot.net/api/jobs/{guid}/details";
        Assert.Equal(guid, HelixIdResolver.ResolveJobId(url));
    }

    [Fact]
    public void ResolveJobId_HelixUrlWithApiVersion_ExtractsGuid()
    {
        var guid = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        var url = $"https://helix.dot.net/api/2019-06-17/jobs/{guid}/details";
        Assert.Equal(guid, HelixIdResolver.ResolveJobId(url));
    }

    [Fact]
    public void ResolveJobId_HelixUrlNoTrailingPath_ExtractsGuid()
    {
        var guid = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        // URL where the GUID is the last segment — "jobs" is at i, guid is at i+1,
        // and i+1 == segments.Length - 1, so the loop condition (i < segments.Length - 1) covers it.
        var url = $"https://helix.dot.net/api/jobs/{guid}";
        // The split gives ["api", "jobs", "{guid}"], loop goes i=0,1.
        // At i=1: segments[1]=="jobs", segments[2] is the guid. ✓
        Assert.Equal(guid, HelixIdResolver.ResolveJobId(url));
    }

    // --- D7 BREAKING CHANGE: Invalid input now throws ArgumentException ---

    [Fact]
    public void ResolveJobId_NonGuidInput_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => HelixIdResolver.ResolveJobId("some-random-string"));
        Assert.Contains("Invalid job ID", ex.Message);
    }

    [Fact]
    public void ResolveJobId_EmptyString_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => HelixIdResolver.ResolveJobId(""));
    }

    [Fact]
    public void ResolveJobId_Null_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => HelixIdResolver.ResolveJobId(null!));
    }

    [Fact]
    public void ResolveJobId_Whitespace_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => HelixIdResolver.ResolveJobId("   "));
    }

    [Fact]
    public void ResolveJobId_UrlWithNoJobsSegment_ThrowsArgumentException()
    {
        var url = "https://helix.dot.net/api/workitems/something";
        var ex = Assert.Throws<ArgumentException>(() => HelixIdResolver.ResolveJobId(url));
        Assert.Contains("Invalid job ID", ex.Message);
    }

    [Fact]
    public void ResolveJobId_UrlWithJobsButNonGuidFollowing_ThrowsArgumentException()
    {
        var url = "https://helix.dot.net/api/jobs/not-a-guid/details";
        var ex = Assert.Throws<ArgumentException>(() => HelixIdResolver.ResolveJobId(url));
        Assert.Contains("Invalid job ID", ex.Message);
    }

    [Fact]
    public void ResolveJobId_UrlWithJobsAsLastSegment_ThrowsArgumentException()
    {
        var url = "https://helix.dot.net/api/jobs";
        var ex = Assert.Throws<ArgumentException>(() => HelixIdResolver.ResolveJobId(url));
        Assert.Contains("Invalid job ID", ex.Message);
    }
}
