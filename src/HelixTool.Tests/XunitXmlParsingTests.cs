using System.Text;
using System.Xml;
using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Tests for xUnit XML format detection and parsing through ParseTrxResultsAsync.
/// Covers IsTestResultFile pattern matching, xUnit XML parsing, multi-assembly files,
/// empty assemblies, skip reasons, error messages, and XXE rejection.
/// </summary>
[Collection("FileSearchConfig")]
public class XunitXmlParsingTests
{
    // Unique GUID per test class to avoid parallel temp dir collisions
    private const string ValidJobId = "a1b2c3d4-e5f6-7890-abcd-ef0123456789";
    private const string WorkItem = "XunitWorkItem";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public XunitXmlParsingTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    /// <summary>Configure mock to return files with given names and content.</summary>
    private void SetupFiles(params (string name, string? content)[] files)
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

    // Standard xUnit XML with mixed results (matches runtime CoreCLR format)
    private const string MixedXunitXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <assemblies>
          <assembly name="System.Runtime.Tests.dll" test-framework="xUnit.net 2.9.3.0"
                    total="5" passed="3" failed="1" skipped="1">
            <collection name="Test collection for System.Tests.MemberAccessExceptionTests"
                        total="5" passed="3" failed="1" skipped="1">
              <test name="System.Tests.ExceptionTests.Ctor_Empty"
                    type="System.Tests.ExceptionTests" method="Ctor_Empty"
                    time="0.0031499" result="Pass" />
              <test name="System.Tests.ExceptionTests.Ctor_String"
                    type="System.Tests.ExceptionTests" method="Ctor_String"
                    time="0.0010001" result="Pass" />
              <test name="System.Tests.ExceptionTests.Ctor_StringException"
                    type="System.Tests.ExceptionTests" method="Ctor_StringException"
                    time="0.0005" result="Pass" />
              <test name="System.Tests.ExceptionTests.ThrowIfNull_Throws"
                    type="System.Tests.ExceptionTests" method="ThrowIfNull_Throws"
                    time="0.0120000" result="Fail">
                <failure>
                  <message>Assert.Equal() Failure: Expected 42, Actual 0</message>
                  <stack-trace>   at System.Tests.ExceptionTests.ThrowIfNull_Throws() in /src/Tests.cs:line 55</stack-trace>
                </failure>
              </test>
              <test name="System.Tests.ExceptionTests.PlatformSpecific_Skipped"
                    type="System.Tests.ExceptionTests" method="PlatformSpecific_Skipped"
                    time="0" result="Skip">
                <reason><![CDATA[Test is platform-specific (Windows only)]]></reason>
              </test>
            </collection>
          </assembly>
        </assemblies>
        """;

    // ========================================================================
    // 1. File pattern detection — IsTestResultFile (public static)
    //    Verifies TestResultFilePatterns against known Helix file naming conventions.
    // ========================================================================

    [Theory]
    [InlineData("testResults.xml", true)]                // iOS/XHarness exact match
    [InlineData("Exceptions.testResults.xml.txt", true)] // CoreCLR XUnitWrapperGenerator
    [InlineData("CoreMangLib.testResults.xml.txt", true)] // CoreCLR variant
    [InlineData("testResults.xml.txt", true)]            // CoreCLR exact name variant
    [InlineData("results.trx", true)]                    // Standard TRX
    [InlineData("helix-linux-03_20250215.trx", true)]    // Machine+timestamp TRX
    [InlineData("random.xml", false)]                    // Generic XML — too generic
    [InlineData("dotnetTestLog.log", false)]              // Log file — not recognized
    [InlineData("AOTBuild.binlog", false)]                // Binary log — not recognized
    [InlineData("console.log", false)]                   // Console log — not recognized
    [InlineData("build.binlog", false)]                  // Build binary log — not recognized
    [InlineData("config.xml", false)]                    // Config XML — not a test result
    [InlineData("app.config.xml", false)]                // App config — not a test result
    public void IsTestResultFile_MatchesKnownPatterns(string fileName, bool expected)
    {
        Assert.Equal(expected, HelixService.IsTestResultFile(fileName));
    }

    [Fact]
    public void TestResultFilePatterns_ContainsExpectedEntries()
    {
        // Verify the patterns array includes all expected dotnet ecosystem patterns
        Assert.Contains("*.trx", HelixService.TestResultFilePatterns);
        Assert.Contains("testResults.xml", HelixService.TestResultFilePatterns);
        Assert.Contains("*.testResults.xml.txt", HelixService.TestResultFilePatterns);
        Assert.Contains("testResults.xml.txt", HelixService.TestResultFilePatterns);
    }

    // ========================================================================
    // 2. Auto-discovery through ParseTrxResultsAsync
    // ========================================================================

    [Fact]
    public async Task XunitXml_DiscoveredWhenNoTrxFilesExist()
    {
        // testResults.xml matches the exact-name pattern in TestResultFilePatterns
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        Assert.Single(results);
        Assert.Equal("testResults.xml", results[0].FileName);
    }

    [Fact]
    public async Task XunitXml_TestResultsXmlTxt_DiscoveredByPattern()
    {
        // Runtime CoreCLR pattern: {name}.testResults.xml.txt
        // Matches "*.testResults.xml.txt" in TestResultFilePatterns
        SetupFiles(("Exceptions.testResults.xml.txt", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        Assert.Single(results);
        Assert.Equal(5, results[0].TotalTests);
    }

    [Fact]
    public async Task XunitXml_SpecificFileByName_ParsesCorrectly()
    {
        // When a specific fileName is requested, bypasses pattern matching
        SetupFiles(("Exceptions.testResults.xml.txt", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem,
            fileName: "Exceptions.testResults.xml.txt");

        Assert.Single(results);
        Assert.Equal(5, results[0].TotalTests);
    }

    [Fact]
    public async Task XunitXml_CoreMangLibPattern_AutoDiscovered()
    {
        // CoreMangLib.testResults.xml.txt — matches *.testResults.xml.txt
        SetupFiles(("CoreMangLib.testResults.xml.txt", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        Assert.Single(results);
        Assert.Equal(5, results[0].TotalTests);
    }

    // ========================================================================
    // 3. File pattern non-matches — files that should NOT be treated as test results
    // ========================================================================

    [Fact]
    public async Task FilePattern_NonTestFiles_NotRecognized()
    {
        // .binlog and .log files don't match any TestResultFilePatterns
        SetupFiles(
            ("dotnetTestLog.log", "some log content"),
            ("AOTBuild.binlog", "binary content"));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));
        Assert.Contains(WorkItem, ex.Message);
    }

    [Fact]
    public async Task FilePattern_GenericXml_NotRecognizedByPatterns()
    {
        // "random.xml" does NOT match any TestResultFilePattern
        // (only "testResults.xml" matches exactly, not any *.xml)
        SetupFiles(("random.xml", """<?xml version="1.0"?><settings/>"""));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));
        Assert.Contains(WorkItem, ex.Message);
    }

    [Fact]
    public async Task FilePattern_TrxFile_RecognizedAsTrx()
    {
        var trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="T1" outcome="Passed" duration="00:00:00.100" computerName="H1" />
              </Results>
            </TestRun>
            """;

        // _machine_timestamp.trx — TRX naming pattern
        SetupFiles(("helix-linux-03_20250215.trx", trxXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        Assert.Single(results);
        Assert.Equal(1, results[0].TotalTests);
        Assert.Equal(1, results[0].Passed);
    }

    // ========================================================================
    // 4. xUnit XML parsing — core extraction
    // ========================================================================

    [Fact]
    public async Task XunitXml_ExtractsTestNameAndDuration()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        var passed = trx.Results.First(r => r.TestName == "System.Tests.ExceptionTests.Ctor_Empty");
        Assert.Equal("Passed", passed.Outcome);
        // Duration formatted as "{time}s"
        Assert.Equal("0.0031499s", passed.Duration);
    }

    [Fact]
    public async Task XunitXml_ParsesPassFailSkipCounts()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        var trx = Assert.Single(results);
        Assert.Equal(5, trx.TotalTests);
        Assert.Equal(3, trx.Passed);
        Assert.Equal(1, trx.Failed);
        Assert.Equal(1, trx.Skipped);
    }

    [Fact]
    public async Task XunitXml_FailedTestIncludesMessageAndStackTrace()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        var failed = trx.Results.First(r => r.Outcome == "Failed");
        Assert.Equal("System.Tests.ExceptionTests.ThrowIfNull_Throws", failed.TestName);
        Assert.Contains("Assert.Equal() Failure", failed.ErrorMessage);
        Assert.Contains("Tests.cs:line 55", failed.StackTrace);
    }

    [Fact]
    public async Task XunitXml_SkippedTestIncludesReason()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        var skipped = trx.Results.First(r => r.Outcome == "Skipped");
        Assert.Equal("System.Tests.ExceptionTests.PlatformSpecific_Skipped", skipped.TestName);
        Assert.Contains("Windows only", skipped.ErrorMessage);
    }

    [Fact]
    public async Task XunitXml_DefaultExcludesPassedTests()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        var trx = Assert.Single(results);
        // Passed tests excluded from Results list, but counts still accurate
        Assert.DoesNotContain(trx.Results, r => r.Outcome == "Passed");
        Assert.Equal(3, trx.Passed); // count still reports 3 passed
        Assert.Contains(trx.Results, r => r.Outcome == "Failed");
        Assert.Contains(trx.Results, r => r.Outcome == "Skipped");
    }

    [Fact]
    public async Task XunitXml_IncludePassedReturnsAll()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        Assert.Equal(5, trx.Results.Count);
        Assert.Equal(3, trx.Results.Count(r => r.Outcome == "Passed"));
    }

    [Fact]
    public async Task XunitXml_DurationFormattedAsSeconds()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        var test = trx.Results.First(r => r.TestName.EndsWith("ThrowIfNull_Throws"));
        Assert.Equal("0.0120000s", test.Duration);
    }

    [Fact]
    public async Task XunitXml_ComputerNameIsNull()
    {
        // xUnit XML format doesn't include computerName per test
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        Assert.All(trx.Results, r => Assert.Null(r.ComputerName));
    }

    // ========================================================================
    // 5. Assembly-level and multi-assembly
    // ========================================================================

    [Fact]
    public async Task XunitXml_MultipleAssemblies_AggregatesTests()
    {
        var multiAssemblyXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <assemblies>
              <assembly name="Assembly1.dll" total="2" passed="2" failed="0" skipped="0">
                <collection name="Col1" total="2" passed="2" failed="0" skipped="0">
                  <test name="A1.Test1" type="A1" method="Test1" time="0.001" result="Pass" />
                  <test name="A1.Test2" type="A1" method="Test2" time="0.002" result="Pass" />
                </collection>
              </assembly>
              <assembly name="Assembly2.dll" total="3" passed="1" failed="1" skipped="1">
                <collection name="Col2" total="3" passed="1" failed="1" skipped="1">
                  <test name="A2.Test1" type="A2" method="Test1" time="0.001" result="Pass" />
                  <test name="A2.Test2" type="A2" method="Test2" time="0.010" result="Fail">
                    <failure>
                      <message>Assertion failed</message>
                      <stack-trace>at A2.Test2()</stack-trace>
                    </failure>
                  </test>
                  <test name="A2.Test3" type="A2" method="Test3" time="0" result="Skip">
                    <reason><![CDATA[Not implemented]]></reason>
                  </test>
                </collection>
              </assembly>
            </assemblies>
            """;

        SetupFiles(("testResults.xml", multiAssemblyXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        // ParseXunitFile aggregates all <test> elements across assemblies
        Assert.Equal(5, trx.TotalTests);
        Assert.Equal(3, trx.Passed);
        Assert.Equal(1, trx.Failed);
        Assert.Equal(1, trx.Skipped);
        Assert.Equal(5, trx.Results.Count);
    }

    [Fact]
    public async Task XunitXml_EmptyAssembly_ZeroCounts()
    {
        var emptyXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <assemblies>
              <assembly name="Empty.dll" total="0" passed="0" failed="0" skipped="0">
              </assembly>
            </assemblies>
            """;

        SetupFiles(("testResults.xml", emptyXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        var trx = Assert.Single(results);
        Assert.Equal(0, trx.TotalTests);
        Assert.Equal(0, trx.Passed);
        Assert.Equal(0, trx.Failed);
        Assert.Equal(0, trx.Skipped);
        Assert.Empty(trx.Results);
    }

    [Fact]
    public async Task XunitXml_SingleAssemblyRoot_ParsesCorrectly()
    {
        // Some xUnit outputs use <assembly> as root (no <assemblies> wrapper)
        var singleRoot = """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly name="Single.dll" total="1" passed="1" failed="0" skipped="0">
              <collection name="Col" total="1" passed="1" failed="0" skipped="0">
                <test name="Single.Test1" type="Single" method="Test1" time="0.005" result="Pass" />
              </collection>
            </assembly>
            """;

        SetupFiles(("testResults.xml", singleRoot));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        Assert.Equal(1, trx.TotalTests);
        Assert.Equal(1, trx.Passed);
        Assert.Equal("Single.Test1", trx.Results[0].TestName);
    }

    // ========================================================================
    // 6. Max results limiting
    // ========================================================================

    [Fact]
    public async Task XunitXml_MaxResultsLimitsOutput()
    {
        SetupFiles(("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem,
            includePassed: true, maxResults: 2);

        var trx = Assert.Single(results);
        Assert.Equal(2, trx.Results.Count);
        // TotalTests counts all tests regardless of maxResults
        Assert.Equal(5, trx.TotalTests);
    }

    // ========================================================================
    // 7. Error truncation in xUnit XML
    // ========================================================================

    [Fact]
    public async Task XunitXml_TruncatesLongErrorMessage()
    {
        var longMsg = new string('E', 600);
        var longStack = new string('S', 1200);
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <assemblies>
              <assembly name="Trunc.dll" total="1" passed="0" failed="1" skipped="0">
                <collection name="Col" total="1" passed="0" failed="1" skipped="0">
                  <test name="Trunc.LongError" type="Trunc" method="LongError" time="0.1" result="Fail">
                    <failure>
                      <message>{longMsg}</message>
                      <stack-trace>{longStack}</stack-trace>
                    </failure>
                  </test>
                </collection>
              </assembly>
            </assemblies>
            """;

        SetupFiles(("testResults.xml", xml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        var trx = Assert.Single(results);
        var failed = Assert.Single(trx.Results);
        Assert.NotNull(failed.ErrorMessage);
        Assert.True(failed.ErrorMessage!.Length <= 500 + "... (truncated)".Length);
        Assert.EndsWith("... (truncated)", failed.ErrorMessage);
        Assert.NotNull(failed.StackTrace);
        Assert.True(failed.StackTrace!.Length <= 1000 + "... (truncated)".Length);
        Assert.EndsWith("... (truncated)", failed.StackTrace);
    }

    [Fact]
    public async Task XunitXml_TruncatesLongSkipReason()
    {
        var longReason = new string('R', 600);
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <assemblies>
              <assembly name="Skip.dll" total="1" passed="0" failed="0" skipped="1">
                <collection name="Col" total="1" passed="0" failed="0" skipped="1">
                  <test name="Skip.LongReason" type="Skip" method="LongReason" time="0" result="Skip">
                    <reason><![CDATA[{longReason}]]></reason>
                  </test>
                </collection>
              </assembly>
            </assemblies>
            """;

        SetupFiles(("testResults.xml", xml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem);

        var trx = Assert.Single(results);
        var skipped = Assert.Single(trx.Results);
        Assert.NotNull(skipped.ErrorMessage);
        Assert.True(skipped.ErrorMessage!.Length <= 500 + "... (truncated)".Length);
        Assert.EndsWith("... (truncated)", skipped.ErrorMessage);
    }

    // ========================================================================
    // 8. Error messages — clarity when no test files found
    // ========================================================================

    [Fact]
    public async Task ErrorMessage_MentionsSearchedFilePatterns()
    {
        SetupFiles(("console.log", "log output"));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));

        // Error should mention the patterns that were searched
        Assert.Contains(".trx", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ErrorMessage_IncludesWorkItemName()
    {
        SetupFiles(("console.log", "log output"));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));

        Assert.Contains(WorkItem, ex.Message);
    }

    [Fact]
    public async Task ErrorMessage_NotGenericMcpError()
    {
        SetupFiles(("console.log", "log output"));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));

        // Must NOT be the generic MCP error message
        Assert.DoesNotContain("An error occurred", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ErrorMessage_ListsAvailableFiles()
    {
        SetupFiles(
            ("console.log", "log output"),
            ("build.binlog", "binary"));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));

        // Error should mention available useful files (binlog) and filter out noise (.log)
        Assert.Contains("build.binlog", ex.Message);
    }

    [Fact]
    public async Task ErrorMessage_EmptyFileList_IndicatesNoFiles()
    {
        _mockApi.ListWorkItemFilesAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile>());

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));

        Assert.Contains(WorkItem, ex.Message);
        Assert.Contains("no uploaded files", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ========================================================================
    // 9. Security: XXE prevention in xUnit XML
    // ========================================================================

    [Fact]
    public async Task XunitXml_RejectsXxeDtdDeclaration_BestEffort()
    {
        // XXE in xUnit XML format — best-effort path catches XmlException and skips
        var xxeXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <assemblies>
              <assembly name="Evil.dll" total="1" passed="1" failed="0" skipped="0">
                <collection name="Col" total="1" passed="1" failed="0" skipped="0">
                  <test name="Evil.Test1" type="Evil" method="Test1" time="0.001" result="Pass" />
                </collection>
              </assembly>
            </assemblies>
            """;

        // testResults.xml matches the exact pattern; auto-discovery downloads it
        // Best-effort parsing catches XmlException → 0 results → HelixException
        SetupFiles(("testResults.xml", xxeXml));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem));
        Assert.Contains(WorkItem, ex.Message);
    }

    [Fact]
    public async Task XunitXml_RejectsXxe_SpecificFile()
    {
        // When requesting a specific file with XXE, the strict path propagates XmlException
        var xxeXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE foo [<!ENTITY xxe "injected">]>
            <assemblies>
              <assembly name="Evil.dll" total="1" passed="1" failed="0" skipped="0">
                <collection name="Col" total="1" passed="1" failed="0" skipped="0">
                  <test name="Evil.Test1" type="Evil" method="Test1" time="0.001" result="Pass" />
                </collection>
              </assembly>
            </assemblies>
            """;

        SetupFiles(("evil.xml", xxeXml));

        // Specific file → strict path → XmlException from DtdProcessing.Prohibit
        await Assert.ThrowsAsync<XmlException>(
            () => _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, fileName: "evil.xml"));
    }

    // ========================================================================
    // 10. Mixed format handling — TRX and xUnit XML together
    // ========================================================================

    [Fact]
    public async Task BothFormats_ReturnedTogether()
    {
        var trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="TrxTest" outcome="Passed" duration="00:00:00.010" computerName="H1" />
              </Results>
            </TestRun>
            """;

        // Both .trx and testResults.xml match TestResultFilePatterns
        SetupFiles(
            ("results.trx", trxXml),
            ("testResults.xml", MixedXunitXml));

        var results = await _svc.ParseTrxResultsAsync(ValidJobId, WorkItem, includePassed: true);

        // Both files are parsed and returned
        Assert.Equal(2, results.Count);
        // TRX parsed strictly, xUnit XML parsed best-effort
        Assert.Contains(results, r => r.FileName == "results.trx");
        Assert.Contains(results, r => r.FileName == "testResults.xml");
    }
}
