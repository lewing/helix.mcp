using System.Text;
using System.Xml;
using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Tests for ParseTrxResultsAsync — TRX file parsing (US-32).
/// Written proactively against the spec; may need small adjustments once Ripley's code lands.
/// </summary>
public class TrxParsingTests
{
    private const string ValidJobId = "b2c3d4e5-6789-abcd-ef01-234567890abc";
    private const string WorkItem = "MyWorkItem";
    private const string TrxFileName = "testResults.trx";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public TrxParsingTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    /// <summary>Helper: configure mock to return a .trx file with given XML content.</summary>
    private void SetupTrxFile(string trxXml, string fileName = TrxFileName)
    {
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns(fileName);
        file.Link.Returns("https://example.com/" + fileName);

        _mockApi.ListWorkItemFilesAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });

        _mockApi.GetFileAsync(fileName, WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(trxXml)));
    }

    /// <summary>Helper: configure mock to return multiple files, only some being .trx.</summary>
    private void SetupMultipleFiles(params (string name, string? content)[] files)
    {
        var fileList = new List<IWorkItemFile>();
        foreach (var (name, content) in files)
        {
            var f = Substitute.For<IWorkItemFile>();
            f.Name.Returns(name);
            f.Link.Returns("https://example.com/" + name);
            fileList.Add(f);

            if (content != null)
            {
                _mockApi.GetFileAsync(name, WorkItem, ValidJobId, Arg.Any<CancellationToken>())
                    .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(content)));
            }
        }

        _mockApi.ListWorkItemFilesAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(fileList);
    }

    /// <summary>Standard TRX XML with mixed outcomes for reuse across tests.</summary>
    private const string MixedTrxXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="TestMethod1" outcome="Passed" duration="00:00:01.234" computerName="HELIX-01" />
            <UnitTestResult testName="TestMethod2" outcome="Failed" duration="00:00:02.567" computerName="HELIX-01">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Equal() Failure</Message>
                  <StackTrace>   at TestClass.TestMethod2() in TestFile.cs:line 42</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testName="TestMethod3" outcome="NotExecuted" duration="00:00:00.000" computerName="HELIX-01" />
          </Results>
        </TestRun>
        """;

    // ========================================================================
    // 1. Input validation
    // ========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ParseTrx_ThrowsOnNullOrEmptyJobId(string? badJobId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.ParseTrxResultsAsync(badJobId!, WorkItem));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ParseTrx_ThrowsOnNullOrEmptyWorkItem(string? badWorkItem)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, badWorkItem!));
    }

    // ========================================================================
    // 2. Config toggle
    // ========================================================================

    [Fact]
    public async Task ParseTrx_ThrowsWhenDisabledByConfig()
    {
        Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", "true");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));
            Assert.Contains("disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", null);
        }
    }

    // ========================================================================
    // 3. Basic TRX parsing
    // ========================================================================

    [Fact]
    public async Task ParseTrx_ParsesMixedResults_CorrectCounts()
    {
        SetupTrxFile(MixedTrxXml);

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        var trx = Assert.Single(results);
        Assert.Equal(TrxFileName, trx.FileName);
        Assert.Equal(3, trx.TotalTests);
        Assert.Equal(1, trx.Passed);
        Assert.Equal(1, trx.Failed);
        Assert.Equal(1, trx.Skipped);
    }

    [Fact]
    public async Task ParseTrx_FailedTestsIncludeErrorDetails()
    {
        SetupTrxFile(MixedTrxXml);

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        var failed = trx.Results.First(r => r.Outcome == "Failed");
        Assert.Equal("TestMethod2", failed.TestName);
        Assert.Equal("Assert.Equal() Failure", failed.ErrorMessage);
        Assert.Contains("TestFile.cs:line 42", failed.StackTrace);
        Assert.Equal("HELIX-01", failed.ComputerName);
        Assert.NotNull(failed.Duration);
    }

    [Fact]
    public async Task ParseTrx_DefaultExcludesPassedFromResults()
    {
        SetupTrxFile(MixedTrxXml);

        // Default: includePassed = false
        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        var trx = Assert.Single(results);
        // Passed tests should be excluded from the Results list
        Assert.DoesNotContain(trx.Results, r => r.Outcome == "Passed");
        // Failed and skipped should still be present
        Assert.Contains(trx.Results, r => r.Outcome == "Failed");
    }

    // ========================================================================
    // 4. Include passed
    // ========================================================================

    [Fact]
    public async Task ParseTrx_IncludePassedReturnsAllResults()
    {
        SetupTrxFile(MixedTrxXml);

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        Assert.Equal(3, trx.Results.Count);
        Assert.Contains(trx.Results, r => r.Outcome == "Passed");
        Assert.Contains(trx.Results, r => r.Outcome == "Failed");
        Assert.Contains(trx.Results, r => r.TestName == "TestMethod3");
    }

    // ========================================================================
    // 5. Max results
    // ========================================================================

    [Fact]
    public async Task ParseTrx_MaxResultsLimitsOutput()
    {
        SetupTrxFile(MixedTrxXml);

        // With includePassed=true there are 3 results; limit to 1
        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true, maxResults: 1);

        var trx = Assert.Single(results);
        Assert.Single(trx.Results);
    }

    // ========================================================================
    // 6. Error truncation
    // ========================================================================

    [Fact]
    public async Task ParseTrx_TruncatesLongErrorMessageAndStackTrace()
    {
        var longMessage = new string('X', 600);
        var longStackTrace = new string('Y', 1200);
        var trxXml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="LongErrorTest" outcome="Failed" duration="00:00:01.000" computerName="HELIX-01">
                  <Output>
                    <ErrorInfo>
                      <Message>{longMessage}</Message>
                      <StackTrace>{longStackTrace}</StackTrace>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
              </Results>
            </TestRun>
            """;

        SetupTrxFile(trxXml);

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        var failed = Assert.Single(trx.Results);

        // ErrorMessage: 500 char limit + "... (truncated)" suffix
        Assert.NotNull(failed.ErrorMessage);
        Assert.True(failed.ErrorMessage!.Length <= 500 + "... (truncated)".Length);
        Assert.EndsWith("... (truncated)", failed.ErrorMessage);

        // StackTrace: 1000 char limit + "... (truncated)" suffix
        Assert.NotNull(failed.StackTrace);
        Assert.True(failed.StackTrace!.Length <= 1000 + "... (truncated)".Length);
        Assert.EndsWith("... (truncated)", failed.StackTrace);
    }

    // ========================================================================
    // 7. No TRX files
    // ========================================================================

    [Fact]
    public async Task ParseTrx_ThrowsWhenNoTrxFilesFound()
    {
        // Set up files that are NOT .trx
        SetupMultipleFiles(
            ("build.binlog", null),
            ("output.log", null));

        await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));
    }

    // ========================================================================
    // 8. Security: XXE prevention
    // ========================================================================

    [Fact]
    public async Task ParseTrx_RejectsXxeDtdDeclaration()
    {
        var xxeTrxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE foo [<!ENTITY xxe "test">]>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="Test1" outcome="Passed" duration="00:00:00.100" computerName="HELIX-01" />
              </Results>
            </TestRun>
            """;

        SetupTrxFile(xxeTrxXml);

        // DtdProcessing.Prohibit should cause XmlException
        await Assert.ThrowsAsync<XmlException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));
    }
}
