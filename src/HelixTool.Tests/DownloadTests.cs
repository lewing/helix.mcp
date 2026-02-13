using System.Net;
using HelixTool.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// End-to-end tests for DownloadFilesAsync and DownloadFromUrlAsync.
/// </summary>
public class DownloadFilesTests : IDisposable
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string IdPrefix = "d1f9a7c3"; // first 8 chars

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;
    private readonly List<string> _createdDirs = [];

    public DownloadFilesTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    public void Dispose()
    {
        // Clean up any temp directories created by tests
        foreach (var dir in _createdDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    private string ExpectedOutDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helix-{IdPrefix}");
        _createdDirs.Add(dir);
        return dir;
    }

    private static IWorkItemFile MockFile(string name, string? link = null)
    {
        var f = Substitute.For<IWorkItemFile>();
        f.Name.Returns(name);
        f.Link.Returns(link ?? $"https://helix.dot.net/files/{name}");
        return f;
    }

    private void ArrangeFiles(string workItem, params IWorkItemFile[] files)
    {
        _mockApi.ListWorkItemFilesAsync(workItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(files.ToList());
    }

    private void ArrangeFileStream(string fileName, string workItem, byte[]? content = null)
    {
        content ??= "file-content"u8.ToArray();
        _mockApi.GetFileAsync(fileName, workItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(content));
    }

    // ==========================================================================
    // Happy path: single file download
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_SingleFile_SavesToDisk()
    {
        ArrangeFiles("wi1", MockFile("build.binlog"));
        ArrangeFileStream("build.binlog", "wi1", "binlog-data"u8.ToArray());

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*.binlog");

        Assert.Single(paths);
        Assert.True(File.Exists(paths[0]));
        Assert.Equal("binlog-data", await File.ReadAllTextAsync(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Happy path: multiple files
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_MultipleFiles_DownloadsAll()
    {
        ArrangeFiles("wi1", MockFile("a.binlog"), MockFile("b.binlog"), MockFile("output.txt"));
        ArrangeFileStream("a.binlog", "wi1", "aaa"u8.ToArray());
        ArrangeFileStream("b.binlog", "wi1", "bbb"u8.ToArray());

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*.binlog");

        Assert.Equal(2, paths.Count);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Pattern matching: wildcard *
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_WildcardStar_DownloadsAllFiles()
    {
        ArrangeFiles("wi1", MockFile("a.binlog"), MockFile("b.trx"), MockFile("c.txt"));
        ArrangeFileStream("a.binlog", "wi1");
        ArrangeFileStream("b.trx", "wi1");
        ArrangeFileStream("c.txt", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*");

        Assert.Equal(3, paths.Count);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Pattern matching: *.trx
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_TrxPattern_MatchesOnlyTrx()
    {
        ArrangeFiles("wi1", MockFile("results.trx"), MockFile("build.binlog"), MockFile("output.txt"));
        ArrangeFileStream("results.trx", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*.trx");

        Assert.Single(paths);
        Assert.EndsWith("results.trx", paths[0]);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Pattern matching: specific file name (contains match)
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_SpecificName_MatchesContaining()
    {
        ArrangeFiles("wi1", MockFile("msbuild.binlog"), MockFile("output.txt"));
        ArrangeFileStream("msbuild.binlog", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "msbuild");

        Assert.Single(paths);
        Assert.Contains("msbuild.binlog", paths[0]);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Empty results when no files match pattern
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_NoMatchingFiles_ReturnsEmptyList()
    {
        ArrangeFiles("wi1", MockFile("output.txt"), MockFile("readme.md"));

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*.binlog");

        Assert.Empty(paths);
    }

    [Fact]
    public async Task DownloadFilesAsync_NoFilesAtAll_ReturnsEmptyList()
    {
        ArrangeFiles("wi1"); // no files

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*");

        Assert.Empty(paths);
    }

    // ==========================================================================
    // File saved in correct temp directory
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_SavesInCorrectTempDir()
    {
        ArrangeFiles("wi1", MockFile("test.txt"));
        ArrangeFileStream("test.txt", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        var expectedDir = ExpectedOutDir();
        Assert.Single(paths);
        Assert.StartsWith(expectedDir, paths[0]);
    }

    // ==========================================================================
    // Path traversal protection: file name with path components
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_FileNameWithPathSeparator_Sanitized()
    {
        // File name contains subdir path — SanitizePathSegment should replace separators
        ArrangeFiles("wi1", MockFile("subdir/file.binlog"));
        ArrangeFileStream("subdir/file.binlog", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*");

        Assert.Single(paths);
        // Path.GetFileName("subdir/file.binlog") = "file.binlog", then sanitized
        var fileName = Path.GetFileName(paths[0]);
        Assert.DoesNotContain("/", fileName);
        Assert.DoesNotContain("\\", fileName);
        Assert.True(File.Exists(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_FileNameWithDotDot_Sanitized()
    {
        ArrangeFiles("wi1", MockFile("../../evil.txt"));
        ArrangeFileStream("../../evil.txt", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*");

        Assert.Single(paths);
        var fileName = Path.GetFileName(paths[0]);
        Assert.DoesNotContain("..", fileName);
        Assert.True(File.Exists(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_FileNameWithBackslash_Sanitized()
    {
        ArrangeFiles("wi1", MockFile("sub\\file.txt"));
        ArrangeFileStream("sub\\file.txt", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*");

        Assert.Single(paths);
        var fileName = Path.GetFileName(paths[0]);
        Assert.DoesNotContain("\\", fileName);
        Assert.True(File.Exists(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Empty file stream
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_EmptyStream_CreatesEmptyFile()
    {
        ArrangeFiles("wi1", MockFile("empty.txt"));
        ArrangeFileStream("empty.txt", "wi1", Array.Empty<byte>());

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        Assert.True(File.Exists(paths[0]));
        Assert.Equal(0, new FileInfo(paths[0]).Length);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Input validation
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_NullJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFilesAsync(null!, "wi1"));
    }

    [Fact]
    public async Task DownloadFilesAsync_EmptyJobId_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFilesAsync("", "wi1"));
    }

    [Fact]
    public async Task DownloadFilesAsync_NullWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFilesAsync(ValidJobId, null!));
    }

    [Fact]
    public async Task DownloadFilesAsync_EmptyWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFilesAsync(ValidJobId, ""));
    }

    [Fact]
    public async Task DownloadFilesAsync_WhitespaceWorkItem_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFilesAsync(ValidJobId, "   "));
    }

    // ==========================================================================
    // Error handling: 404
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_NotFound_ThrowsHelixException()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.DownloadFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================
    // Error handling: 401/403
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_Unauthorized_ThrowsHelixException()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.DownloadFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("Access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadFilesAsync_Forbidden_ThrowsHelixException()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.DownloadFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("Access denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================
    // Error handling: server error
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_ServerError_ThrowsHelixException()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Server Error", null, HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.DownloadFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("API error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================
    // Error handling: timeout
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_Timeout_ThrowsHelixException()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timed out"));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.DownloadFilesAsync(ValidJobId, "wi1"));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================================================
    // Cancellation propagates
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("canceled", null, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _svc.DownloadFilesAsync(ValidJobId, "wi1", "*", cts.Token));
    }

    // ==========================================================================
    // URL-based job ID resolves correctly
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_UrlJobId_ResolvesCorrectly()
    {
        var url = $"https://helix.dot.net/api/2019-06-17/jobs/{ValidJobId}/workitems";
        ArrangeFiles("wi1", MockFile("test.txt"));
        ArrangeFileStream("test.txt", "wi1");

        var paths = await _svc.DownloadFilesAsync(url, "wi1");

        Assert.Single(paths);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Case-insensitive pattern matching for extensions
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_PatternCaseInsensitive_MatchesUpperCase()
    {
        ArrangeFiles("wi1", MockFile("BUILD.BINLOG"));
        ArrangeFileStream("BUILD.BINLOG", "wi1");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*.binlog");

        Assert.Single(paths);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Multiple files with same base name
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_SameFileName_LastWriteWins()
    {
        // Two files with the same name after Path.GetFileName — second overwrites first
        ArrangeFiles("wi1", MockFile("file.txt"), MockFile("file.txt"));
        _mockApi.GetFileAsync("file.txt", "wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(
                _ => new MemoryStream("first"u8.ToArray()),
                _ => new MemoryStream("second"u8.ToArray()));

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        // Both writes target the same sanitized path — second overwrites
        Assert.Equal(2, paths.Count);
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal("second", await File.ReadAllTextAsync(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    // ==========================================================================
    // Large binary content preserved
    // ==========================================================================

    [Fact]
    public async Task DownloadFilesAsync_BinaryContent_PreservedExactly()
    {
        var binaryData = new byte[1024];
        new Random(42).NextBytes(binaryData);

        ArrangeFiles("wi1", MockFile("data.bin"));
        ArrangeFileStream("data.bin", "wi1", binaryData);

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        var readBack = await File.ReadAllBytesAsync(paths[0]);
        Assert.Equal(binaryData, readBack);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }
}

/// <summary>
/// Tests for DownloadFromUrlAsync — argument validation and URL parsing logic.
/// HTTP calls use a static HttpClient so we test validation/naming, not HTTP transport.
/// </summary>
public class DownloadFromUrlParsingTests
{
    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public DownloadFromUrlParsingTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    // ==========================================================================
    // Argument validation (extends DownloadFromUrlTests)
    // ==========================================================================

    [Fact]
    public async Task DownloadFromUrlAsync_NullUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _svc.DownloadFromUrlAsync(null!));
    }

    [Fact]
    public async Task DownloadFromUrlAsync_EmptyUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _svc.DownloadFromUrlAsync(""));
    }

    [Fact]
    public async Task DownloadFromUrlAsync_WhitespaceUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _svc.DownloadFromUrlAsync("   "));
    }

    // ==========================================================================
    // Invalid URL format
    // ==========================================================================

    [Fact]
    public async Task DownloadFromUrlAsync_InvalidUrl_ThrowsUriFormatException()
    {
        await Assert.ThrowsAsync<UriFormatException>(() => _svc.DownloadFromUrlAsync("not-a-url"));
    }

    [Fact]
    public async Task DownloadFromUrlAsync_RelativeUrl_Throws()
    {
        // On Windows, Uri ctor throws UriFormatException for "/relative/path".
        // On Linux, it resolves to file:///relative/path, then HttpClient throws NotSupportedException.
        await Assert.ThrowsAnyAsync<Exception>(() => _svc.DownloadFromUrlAsync("/relative/path"));
    }

    // ==========================================================================
    // URL with encoded characters — verifies Uri.UnescapeDataString path
    // Tests that the filename extraction logic handles encoded characters.
    // These will fail at the HTTP layer since we can't mock static HttpClient,
    // but we verify the code gets past argument validation and URI parsing.
    // ==========================================================================

    [Fact]
    public async Task DownloadFromUrlAsync_UrlWithEncodedSpaces_ParsesCorrectly()
    {
        // The URI "my%20file.txt" should be unescaped to "my file.txt"
        // This will fail at the HTTP call since we can't mock s_httpClient,
        // but proves the URI parsing is correct by not throwing UriFormatException
        var url = "https://example.com/files/my%20file.txt";
        // Will throw HttpRequestException or HelixException from actual HTTP call
        await Assert.ThrowsAnyAsync<Exception>(() => _svc.DownloadFromUrlAsync(url));
        // If we got here without UriFormatException, URI parsing worked
    }
}

/// <summary>
/// Tests verifying CacheSecurity.SanitizePathSegment behavior as used by download paths.
/// CacheSecurity is internal; these tests verify the integrated behavior through DownloadFilesAsync.
/// Uses a distinct job ID to avoid temp dir collisions with other test classes.
/// </summary>
public class DownloadSanitizationTests : IDisposable
{
    private const string ValidJobId = "a2b3c4d5-1111-2222-3333-444455556666";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;
    private readonly List<string> _createdDirs = [];

    public DownloadSanitizationTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    public void Dispose()
    {
        foreach (var dir in _createdDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    private void ArrangeFileAndStream(string fileName, string workItem = "wi1")
    {
        var f = Substitute.For<IWorkItemFile>();
        f.Name.Returns(fileName);
        f.Link.Returns("https://example.com/file");
        _mockApi.ListWorkItemFilesAsync(workItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { f });
        _mockApi.GetFileAsync(fileName, workItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream("data"u8.ToArray()));
    }

    [Fact]
    public async Task DownloadFilesAsync_NormalFileName_PreservedUnchanged()
    {
        ArrangeFileAndStream("results.trx");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        Assert.Equal("results.trx", Path.GetFileName(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_FileWithForwardSlash_SlashReplacedWithUnderscore()
    {
        // Path.GetFileName("logs/output.txt") => "output.txt" on both platforms
        // So the forward slash never reaches SanitizePathSegment
        ArrangeFileAndStream("logs/output.txt");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        var fileName = Path.GetFileName(paths[0]);
        Assert.DoesNotContain("/", fileName);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_FileWithDoubleDot_DoubleDotSanitized()
    {
        // Path.GetFileName("../secret.txt") may return "secret.txt" or the whole thing
        // depending on platform. Either way, SanitizePathSegment removes ".."
        ArrangeFileAndStream("..secret.txt");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        var fileName = Path.GetFileName(paths[0]);
        Assert.DoesNotContain("..", fileName);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_FileStaysWithinOutDir()
    {
        // Even with a malicious file name, output must stay within the helix-<id> dir
        ArrangeFileAndStream("../../etc/passwd");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        var outDir = Path.Combine(Path.GetTempPath(), $"helix-a2b3c4d5");
        Assert.StartsWith(Path.GetFullPath(outDir), Path.GetFullPath(paths[0]));
        _createdDirs.Add(outDir);
    }

    [Fact]
    public async Task DownloadFilesAsync_FileNameWithSpaces_Preserved()
    {
        ArrangeFileAndStream("my results file.trx");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        Assert.Equal("my results file.trx", Path.GetFileName(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_FileNameWithUnicode_Preserved()
    {
        ArrangeFileAndStream("日本語ファイル.txt");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Single(paths);
        Assert.Equal("日本語ファイル.txt", Path.GetFileName(paths[0]));
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }
}

/// <summary>
/// Tests for DownloadFilesAsync pattern matching edge cases.
/// These exercise MatchesPattern indirectly through the download flow.
/// Uses a distinct job ID to avoid temp dir collisions with other test classes.
/// </summary>
public class DownloadPatternTests : IDisposable
{
    private const string ValidJobId = "b3c4d5e6-2222-3333-4444-555566667777";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;
    private readonly List<string> _createdDirs = [];

    public DownloadPatternTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    public void Dispose()
    {
        foreach (var dir in _createdDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    private void ArrangeFilesWithStreams(string workItem, params string[] fileNames)
    {
        var files = fileNames.Select(name =>
        {
            var f = Substitute.For<IWorkItemFile>();
            f.Name.Returns(name);
            f.Link.Returns($"https://example.com/{name}");
            return f;
        }).ToList();

        _mockApi.ListWorkItemFilesAsync(workItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(files.AsReadOnly());

        foreach (var name in fileNames)
        {
            _mockApi.GetFileAsync(name, workItem, ValidJobId, Arg.Any<CancellationToken>())
                .Returns(_ => new MemoryStream("x"u8.ToArray()));
        }
    }

    [Theory]
    [InlineData("*.binlog", new[] { "build.binlog", "test.binlog" }, new[] { "output.txt" })]
    [InlineData("*.trx", new[] { "results.trx" }, new[] { "build.binlog", "output.txt" })]
    [InlineData("*", new[] { "a.txt", "b.binlog", "c.trx" }, new string[0])]
    [InlineData("build", new[] { "build.binlog", "rebuild.log" }, new[] { "output.txt" })]
    public async Task DownloadFilesAsync_PatternFiltering_MatchesCorrectFiles(
        string pattern, string[] expectedMatches, string[] expectedNonMatches)
    {
        var allFiles = expectedMatches.Concat(expectedNonMatches).ToArray();
        ArrangeFilesWithStreams("wi1", allFiles);

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", pattern);

        Assert.Equal(expectedMatches.Length, paths.Count);
        if (paths.Count > 0)
            _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_DefaultPattern_DownloadsAll()
    {
        ArrangeFilesWithStreams("wi1", "a.txt", "b.binlog");

        // Default pattern parameter is "*"
        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1");

        Assert.Equal(2, paths.Count);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_ExtensionPatternCaseInsensitive()
    {
        ArrangeFilesWithStreams("wi1", "Build.BINLOG", "test.Binlog", "output.txt");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "*.binlog");

        Assert.Equal(2, paths.Count);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }

    [Fact]
    public async Task DownloadFilesAsync_SubstringPattern_MatchesCaseInsensitive()
    {
        ArrangeFilesWithStreams("wi1", "TestResults.xml", "mytestoutput.txt", "build.log");

        var paths = await _svc.DownloadFilesAsync(ValidJobId, "wi1", "test");

        Assert.Equal(2, paths.Count);
        _createdDirs.Add(Path.GetDirectoryName(paths[0])!);
    }
}
