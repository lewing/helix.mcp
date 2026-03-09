using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Tests for <see cref="AzdoService.GetBuildLogAsync"/> tail optimization:
/// when tailLines is requested and the log is large enough, the service
/// uses startLine to fetch only the tail from the API instead of downloading
/// the full log and trimming client-side.
/// </summary>
public class AzdoServiceTailTests
{
    private readonly IAzdoApiClient _mockApi = Substitute.For<IAzdoApiClient>();
    private readonly AzdoService _svc;

    public AzdoServiceTailTests()
    {
        _svc = new AzdoService(_mockApi);
    }

    // S-1: tailLines=500, lineCount=50000 → optimization fires, startLine=49500
    [Fact]
    public async Task GetBuildLogAsync_LargeLog_UsesStartLineOptimization()
    {
        _mockApi.GetBuildLogsListAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 5, LineCount = 50000 } });

        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5,
                49500, null, Arg.Any<CancellationToken>())
            .Returns("tail content");

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 500);

        Assert.Equal("tail content", result);
        // Verify the range call was made
        await _mockApi.Received(1).GetBuildLogAsync("dnceng-public", "public", 1, 5,
            49500, null, Arg.Any<CancellationToken>());
        // Verify full log was NOT fetched
        await _mockApi.DidNotReceive().GetBuildLogAsync("dnceng-public", "public", 1, 5,
            null, null, Arg.Any<CancellationToken>());
    }

    // S-2: tailLines=500, lineCount=600 → below 2x threshold, full fetch + client trim
    [Fact]
    public async Task GetBuildLogAsync_SmallLog_FullFetchAndClientTrim()
    {
        _mockApi.GetBuildLogsListAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 5, LineCount = 600 } });

        var lines = string.Join('\n', Enumerable.Range(1, 600).Select(i => $"L{i}"));
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5,
                null, null, Arg.Any<CancellationToken>())
            .Returns(lines);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 500);

        // Should return last 500 lines (L101 through L600)
        var resultLines = result!.Split('\n');
        Assert.Equal(500, resultLines.Length);
        Assert.Equal("L101", resultLines[0]);
        Assert.Equal("L600", resultLines[^1]);

        // Full log fetched (not range)
        await _mockApi.Received(1).GetBuildLogAsync("dnceng-public", "public", 1, 5,
            null, null, Arg.Any<CancellationToken>());
        // Range call should NOT have been made
        await _mockApi.DidNotReceive().GetBuildLogAsync("dnceng-public", "public", 1, 5,
            Arg.Is<int?>(s => s != null), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // S-3: tailLines=null → no optimization, GetBuildLogsListAsync NOT called
    [Fact]
    public async Task GetBuildLogAsync_NoTailLines_NoMetadataCall()
    {
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("full log content");

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: null);

        Assert.Equal("full log content", result);
        // GetBuildLogsListAsync should NOT be called when tailLines is null
        await _mockApi.DidNotReceive().GetBuildLogsListAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // S-4: logId not in logs list → fallback to full fetch
    [Fact]
    public async Task GetBuildLogAsync_LogIdNotInList_FallsBackToFullFetch()
    {
        // Logs list has entries for logId 3 and 7, but NOT 5
        _mockApi.GetBuildLogsListAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry>
            {
                new() { Id = 3, LineCount = 50000 },
                new() { Id = 7, LineCount = 80000 }
            });

        var lines = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"L{i}"));
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5,
                null, null, Arg.Any<CancellationToken>())
            .Returns(lines);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 500);

        // Falls back to full fetch since logId=5 is not in the list
        Assert.Equal(lines, result);
        await _mockApi.Received(1).GetBuildLogAsync("dnceng-public", "public", 1, 5,
            null, null, Arg.Any<CancellationToken>());
    }

    // S-5: tailLines=500, lineCount=500 → exact match, below threshold
    [Fact]
    public async Task GetBuildLogAsync_ExactMatch_NoOptimization()
    {
        _mockApi.GetBuildLogsListAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 5, LineCount = 500 } });

        var lines = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"L{i}"));
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5,
                null, null, Arg.Any<CancellationToken>())
            .Returns(lines);

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 500);

        // 500 < 500*2=1000, no optimization. Full fetch, but all lines returned (no trim needed)
        Assert.Equal(lines, result);
        await _mockApi.Received(1).GetBuildLogAsync("dnceng-public", "public", 1, 5,
            null, null, Arg.Any<CancellationToken>());
        await _mockApi.DidNotReceive().GetBuildLogAsync("dnceng-public", "public", 1, 5,
            Arg.Is<int?>(s => s != null), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // S-6: tailLines=500, lineCount=1001 → at 2x+1 threshold, optimization fires
    [Fact]
    public async Task GetBuildLogAsync_JustAboveThreshold_OptimizationFires()
    {
        _mockApi.GetBuildLogsListAsync("dnceng-public", "public", 1, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 5, LineCount = 1001 } });

        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5,
                501, null, Arg.Any<CancellationToken>())
            .Returns("optimized tail");

        var result = await _svc.GetBuildLogAsync("1", 5, tailLines: 500);

        Assert.Equal("optimized tail", result);
        // startLine = 1001 - 500 = 501
        await _mockApi.Received(1).GetBuildLogAsync("dnceng-public", "public", 1, 5,
            501, null, Arg.Any<CancellationToken>());
        // Full log should NOT have been fetched
        await _mockApi.DidNotReceive().GetBuildLogAsync("dnceng-public", "public", 1, 5,
            null, null, Arg.Any<CancellationToken>());
    }
}
