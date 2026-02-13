using System.Text;
using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

[Collection("FileSearchConfig")]
public class SearchFileTests
{
    private const string ValidJobId = "a3b4c5d6-1234-5678-9abc-def012345678";
    private const string WorkItem = "MyWorkItem";
    private const string FileName = "test.log";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public SearchFileTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    /// <summary>Helper: configure mock to return a file with given content via DownloadFilesAsync flow.</summary>
    private void SetupFileContent(string content, string fileName = FileName)
    {
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns(fileName);
        file.Link.Returns("https://example.com/" + fileName);

        _mockApi.ListWorkItemFilesAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });

        _mockApi.GetFileAsync(fileName, WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>Helper: configure mock to return a file with raw bytes via DownloadFilesAsync flow.</summary>
    private void SetupFileBytes(byte[] bytes, string fileName = FileName)
    {
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns(fileName);
        file.Link.Returns("https://example.com/" + fileName);

        _mockApi.ListWorkItemFilesAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });

        _mockApi.GetFileAsync(fileName, WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));
    }

    // --- Input validation ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchFile_ThrowsOnNullJobId(string? badJobId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchFileAsync(badJobId!, WorkItem, FileName, "pattern"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchFile_ThrowsOnNullWorkItem(string? badWorkItem)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchFileAsync(ValidJobId, badWorkItem!, FileName, "pattern"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchFile_ThrowsOnNullFileName(string? badFileName)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchFileAsync(ValidJobId, WorkItem, badFileName!, "pattern"));
    }

    // --- Config toggle ---

    [Fact]
    public async Task SearchFile_ThrowsWhenDisabledByConfig()
    {
        Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", "true");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _svc.SearchFileAsync(ValidJobId, WorkItem, FileName, "pattern"));
            Assert.Contains("disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", null);
        }
    }

    [Fact]
    public async Task SearchConsoleLog_ThrowsWhenDisabledByConfig()
    {
        Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", "true");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _svc.SearchConsoleLogAsync(ValidJobId, WorkItem, "pattern"));
            Assert.Contains("disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", null);
        }
    }

    // --- Binary file detection ---

    [Fact]
    public async Task SearchFile_DetectsBinaryFile()
    {
        // Content with null bytes → binary detection
        var binaryContent = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F, 0x72, 0x6C, 0x64 };
        SetupFileBytes(binaryContent);

        var result = await _svc.SearchFileAsync(ValidJobId, WorkItem, FileName, "Hello");

        Assert.True(result.IsBinary);
        Assert.Empty(result.Matches);
        Assert.Equal(FileName, result.FileName);
    }

    // --- Basic search ---

    [Fact]
    public async Task SearchFile_FindsMatchingLines()
    {
        var fileContent = "Starting build\nCompilation succeeded\nerror CS1234: Something bad\nDone";
        SetupFileContent(fileContent);

        var result = await _svc.SearchFileAsync(ValidJobId, WorkItem, FileName, "error");

        Assert.Equal(FileName, result.FileName);
        Assert.False(result.IsBinary);
        Assert.Equal(4, result.TotalLines);
        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber); // 1-based
        Assert.Contains("error CS1234", match.Line);
    }

    [Fact]
    public async Task SearchFile_CaseInsensitive()
    {
        var fileContent = "Line 1\nerror happened here\nLine 3";
        SetupFileContent(fileContent);

        // Search with uppercase "ERROR", file has lowercase "error"
        var result = await _svc.SearchFileAsync(ValidJobId, WorkItem, FileName, "ERROR");

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.LineNumber);
        Assert.Contains("error", match.Line);
    }

    [Fact]
    public async Task SearchFile_IncludesContextLines()
    {
        var fileContent = "line A\nline B\nERROR: fail\nline D\nline E";
        SetupFileContent(fileContent);

        var result = await _svc.SearchFileAsync(ValidJobId, WorkItem, FileName, "ERROR", contextLines: 1);

        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber);
        Assert.NotNull(match.Context);
        Assert.Equal(3, match.Context.Count);
        Assert.Equal("line B", match.Context[0]);
        Assert.Equal("ERROR: fail", match.Context[1]);
        Assert.Equal("line D", match.Context[2]);
    }

    // --- Max matches ---

    [Fact]
    public async Task SearchFile_RespectsMaxMatches()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"error on line {i}");
        var fileContent = string.Join("\n", lines);
        SetupFileContent(fileContent);

        var result = await _svc.SearchFileAsync(ValidJobId, WorkItem, FileName, "error", maxMatches: 3);

        Assert.Equal(3, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.Equal(10, result.TotalLines);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(2, result.Matches[1].LineNumber);
        Assert.Equal(3, result.Matches[2].LineNumber);
    }

    // --- No matches ---

    [Fact]
    public async Task SearchFile_ReturnsEmptyForNoMatches()
    {
        var fileContent = "Everything is fine\nNo issues here\nAll good";
        SetupFileContent(fileContent);

        var result = await _svc.SearchFileAsync(ValidJobId, WorkItem, FileName, "FATAL_CRASH");

        Assert.Empty(result.Matches);
        Assert.Equal(3, result.TotalLines);
        Assert.False(result.IsBinary);
        Assert.False(result.Truncated);
        Assert.Equal(FileName, result.FileName);
    }
}
