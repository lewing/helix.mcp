using Xunit;
using HelixTool.Core;

namespace HelixTool.Tests;

public class HelixIdResolverUrlTests
{
    private const string TestGuid = "02d8bd09-9400-4e86-8d2b-7a6ca21c5009";

    [Fact]
    public void WorkItemUrl_ExtractsBoth()
    {
        var input = $"https://helix.dot.net/api/2019-06-17/jobs/{TestGuid}/workitems/SomeTest.dll/console";
        var result = HelixIdResolver.TryResolveJobAndWorkItem(input, out var jobId, out var workItem);

        Assert.True(result);
        Assert.Equal(TestGuid, jobId);
        Assert.Equal("SomeTest.dll", workItem);
    }

    [Fact]
    public void WorkItemUrlNoConsole_ExtractsBoth()
    {
        var input = $"https://helix.dot.net/api/2019-06-17/jobs/{TestGuid}/workitems/SomeTest.dll";
        var result = HelixIdResolver.TryResolveJobAndWorkItem(input, out var jobId, out var workItem);

        Assert.True(result);
        Assert.Equal(TestGuid, jobId);
        Assert.Equal("SomeTest.dll", workItem);
    }

    [Fact]
    public void JobUrlOnly_ExtractsJobIdNoWorkItem()
    {
        var input = $"https://helix.dot.net/api/2019-06-17/jobs/{TestGuid}/details";
        var result = HelixIdResolver.TryResolveJobAndWorkItem(input, out var jobId, out var workItem);

        Assert.True(result);
        Assert.Equal(TestGuid, jobId);
        Assert.Null(workItem);
    }

    [Fact]
    public void PlainGuid_ExtractsJobIdNoWorkItem()
    {
        var result = HelixIdResolver.TryResolveJobAndWorkItem(TestGuid, out var jobId, out var workItem);

        Assert.True(result);
        Assert.Equal(TestGuid, jobId);
        Assert.Null(workItem);
    }

    [Fact]
    public void WorkItemWithDots_ExtractsCorrectly()
    {
        var input = $"https://helix.dot.net/api/2019-06-17/jobs/{TestGuid}/workitems/dotnet-watch.Tests.dll.1";
        var result = HelixIdResolver.TryResolveJobAndWorkItem(input, out var jobId, out var workItem);

        Assert.True(result);
        Assert.Equal(TestGuid, jobId);
        Assert.Equal("dotnet-watch.Tests.dll.1", workItem);
    }

    [Fact]
    public void InvalidInput_ReturnsFalse()
    {
        var result = HelixIdResolver.TryResolveJobAndWorkItem("not-a-guid-or-url", out var jobId, out var workItem);

        Assert.False(result);
    }

    [Fact]
    public void UrlWithoutJobsSegment_ReturnsFalse()
    {
        var result = HelixIdResolver.TryResolveJobAndWorkItem("https://helix.dot.net/api/status", out var jobId, out var workItem);

        Assert.False(result);
    }
}
