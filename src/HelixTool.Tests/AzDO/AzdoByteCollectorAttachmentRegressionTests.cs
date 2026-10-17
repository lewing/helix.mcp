using HelixTool.Core.AzDO;
using HelixTool.Core.Collect;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed partial class AzdoByteDownloadTests
{
    [Theory]
    [InlineData(null, true, false, true)]
    [InlineData(null, true, true, true)]
    [InlineData("8:run", true, false, true)]
    [InlineData("9:run", true, false, true)]
    [InlineData("8:3", true, false, true)]
    [InlineData("9:4", true, false, true)]
    [InlineData(null, false, false, true)]
    [InlineData(null, true, false, false)]
    public async Task AttachmentCollection_CountsMembersAcrossAllSelectedRunAndResultScopes(
        string? matchingScope, bool selectMembers, bool includeArtifact, bool includeAttachments)
    {
        _client.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });
        _client.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Records = [new() { Id = "job", Type = "Job", Name = "Build", State = "completed", Result = "succeeded" }] });
        _client.GetTestRunsAsync("dnceng-public", "public", 42, int.MaxValue, Arg.Any<CancellationToken>())
            .Returns(new[] { new AzdoTestRun { Id = 8, State = "completed", TotalTests = 2 }, new AzdoTestRun { Id = 9, State = "completed", TotalTests = 2 } });
        var attachments = includeAttachments
            ? new[] { new AzdoTestAttachment { Id = 9, FileName = "diagnostic.zip" } } : [];
        foreach (var runId in new[] { 8, 9 })
        {
            _client.GetTestResultsAsync("dnceng-public", "public", runId, int.MaxValue, null, Arg.Any<CancellationToken>())
                .Returns(new[] { new AzdoTestResult { Id = 3, Outcome = "Passed" }, new AzdoTestResult { Id = 4, Outcome = "Failed" } });
            _client.GetRunTestAttachmentsAsync("dnceng-public", "public", runId, int.MaxValue, Arg.Any<CancellationToken>())
                .Returns(attachments);
            foreach (var resultId in new int?[] { null, 3, 4 })
            {
                if (resultId is int id)
                    _client.GetTestAttachmentsAsync("dnceng-public", "public", runId, id, int.MaxValue, Arg.Any<CancellationToken>())
                        .Returns(attachments);
                var scope = $"{runId}:{resultId?.ToString() ?? "run"}";
                var zip = Zip((scope == matchingScope ? "logs/target.binlog" : "logs/other.txt", ""));
                _client.OpenTestAttachmentAsync("dnceng-public", "public", runId, resultId, 9, Arg.Any<CancellationToken>())
                    .Returns(_ => new AzdoByteStream(new MemoryStream(zip), "application/zip", zip.Length));
            }
        }
        var artifactZip = Zip(("drop/Build.binlog", "artifact"));
        _client.OpenArtifactAsync("dnceng-public", "public", 42, _artifact, null, Arg.Any<CancellationToken>())
            .Returns(_ => new AzdoByteStream(new MemoryStream(artifactZip), "application/zip", artifactZip.Length));
        var cachedClient = new CachingAzdoApiClient(_client, _cache, _options);
        var helix = Substitute.For<IHelixApiClient>();
        var token = Substitute.For<IAzdoTokenAccessor>();
        token.AuthStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new AzdoAuthStatus { IsAuthenticated = false, Source = "anonymous", Path = "anonymous" });
        using var http = new HttpClient();
        var collector = new AzdoBuildCollector(new AzdoService(cachedClient), cachedClient,
            new HelixService(helix, http), helix, token, Substitute.For<IHelixTokenAccessor>(), _options, _cache);
        var pattern = selectMembers ? "**/target.binlog" : null;
        var policy = new CollectPolicy
        {
            BuildIdOrUrl = "42", ManifestPath = Path.Combine(_root, "attachments-manifest.json"),
            DownloadAzdoArtifacts = includeArtifact ? "drop" : null,
            DownloadTestAttachments = "*.zip", DownloadAttachmentMembers = pattern,
            TestScope = "none", LogScope = "none", HelixScope = "none"
        };

        var result = await collector.CollectAzdoBuildAsync(policy);

        var expectGap = !includeAttachments || selectMembers && matchingScope is null;
        Assert.Equal(!expectGap, result.Manifest.Complete);
        Assert.Equal(expectGap ? 2 : 0, result.Manifest.ExitCode);
        var attachmentReports = result.Manifest.ByteDownloads?.Where(r => r.Results.Any(a => a.AttachmentId is not null)).ToArray() ?? [];
        Assert.Equal(includeAttachments ? 6 : 0, attachmentReports.Length);
        Assert.Equal(selectMembers && matchingScope is not null ? 1 : 0,
            attachmentReports.Sum(r => r.Results.Sum(a => a.MembersTotal)));
        if (selectMembers) Assert.All(attachmentReports, r => Assert.Equal(0, r.Bytes));
        if (expectGap)
        {
            var gap = Assert.Single(result.Manifest.Attempts, a => a.Skip?.Kind == "no_matches");
            Assert.True(gap.Required);
            Assert.Equal("download_azdo_bytes", gap.Operation);
            Assert.Equal("azdo_bytes", gap.Phase);
            Assert.Equal("download_azdo_bytes", Assert.Single(result.Manifest.IncompleteDetails).Operation);
        }
        else
        {
            Assert.DoesNotContain(result.Manifest.Attempts, a => a.Skip?.Kind == "no_matches");
            Assert.Empty(result.Manifest.IncompleteDetails);
        }
        var resumed = await collector.CollectAzdoBuildAsync(policy with { Resume = true });
        Assert.Equal(!expectGap, resumed.Manifest.Complete);
        Assert.Equal(expectGap ? 1 : 0, resumed.Manifest.Attempts.Count(a => a.Skip?.Kind == "no_matches"));
        if (selectMembers && includeAttachments)
        {
            var direct = await _service.DownloadTestAttachmentsAsync(8, null, "dnceng-public", "public",
                new() { Pattern = "*.zip", MemberPattern = "**/does-not-exist.binlog" });
            Assert.True(direct.Complete);
            Assert.Empty(Assert.Single(direct.Results).Members);
        }
    }
}
