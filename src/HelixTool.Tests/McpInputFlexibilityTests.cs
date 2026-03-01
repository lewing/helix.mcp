using HelixTool.Core;
using ModelContextProtocol;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class McpInputFlexibilityTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItemName = "TestName";
    private const string FullWorkItemUrl = $"https://helix.dot.net/api/2019-06-17/jobs/{ValidJobId}/workitems/{WorkItemName}";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;
    private readonly HelixMcpTools _tools;

    public McpInputFlexibilityTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
        _tools = new HelixMcpTools(_svc);
    }

    [Fact]
    public async Task Files_WithFullUrl_ExtractsWorkItem()
    {
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns("output.txt");
        file.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });

        var result = await _tools.Files(FullWorkItemUrl, workItem: null);

        // Should have parsed successfully and returned file data
        Assert.Single(result.Other);
        Assert.Equal("output.txt", result.Other[0].Name);

        // Verify the mock was called with the extracted jobId and workItem
        await _mockApi.Received(1).ListWorkItemFilesAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Logs_WithFullUrl_ExtractsWorkItem()
    {
        var logStream = new MemoryStream("Test log output\nLine 2\n"u8.ToArray());

        _mockApi.GetConsoleLogAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(logStream);

        var result = await _tools.Logs(FullWorkItemUrl, workItem: null);

        // Should contain log content, not an error JSON
        Assert.Contains("Test log output", result);

        // Verify the mock was called with the extracted jobId and workItem
        await _mockApi.Received(1).GetConsoleLogAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Files_WithMissingWorkItem_ThrowsMcpException()
    {
        // Plain GUID with no workItem — can't extract workItem from a bare GUID
        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.Files(ValidJobId, workItem: null));
        Assert.Contains("Work item name is required", ex.Message);
    }

    [Fact]
    public async Task Files_WithExplicitWorkItem_UsesProvided()
    {
        var explicitWorkItem = "ExplicitlyProvided";

        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns("data.bin");
        file.Link.Returns("https://helix.dot.net/files/data.bin");

        _mockApi.ListWorkItemFilesAsync(explicitWorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });

        var result = await _tools.Files(ValidJobId, workItem: explicitWorkItem);

        // Should succeed with file data
        Assert.Single(result.Other);

        // Verify the explicit workItem was used, not extracted from URL
        await _mockApi.Received(1).ListWorkItemFilesAsync(explicitWorkItem, ValidJobId, Arg.Any<CancellationToken>());
    }
}
