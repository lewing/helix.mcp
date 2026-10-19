using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using HelixTool.Core.Acquisition;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using Microsoft.DotNet.Helix.Client;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.Helix;

public sealed class HelixUploadedFileDownloadTests
{
    private const string JobId = "c2a682b0-bb4b-46c8-8ac5-8077cd721f78";
    private const string WorkItem = "Regression_o_3";
    private const string FileName = "testResults.xml";
    private const string BlobUrl = "https://results.blob.core.windows.net/job/Regression_o_3/1/testResults.xml?helixlogtype=result";
    private const string ResultsXml = """
        <assemblies><assembly><collection>
          <test name="Example.FailingTest" result="Fail">
            <failure><message>Expected failure</message></failure>
          </test>
        </collection></assembly></assemblies>
        """;

    [Fact]
    public async Task GetFileAsync_UsesListedAbsoluteLinkAndReturnsUnbufferedStream()
    {
        using var handler = new FileHandler();
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await using (var stream = await client.GetFileAsync(FileName, WorkItem, JobId))
        {
            Assert.True(stream.CanRead);
            Assert.Equal(0, handler.Body!.Position);
            Assert.False(handler.BodyDisposed);
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.Equal(ResultsXml, await reader.ReadToEndAsync());
        }

        Assert.True(handler.BodyDisposed);
        Assert.Equal(1, handler.BlobRequests);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith("/files/" + FileName));
    }

    [Theory]
    [InlineData("download")]
    [InlineData("search")]
    [InlineData("results")]
    [InlineData("specific-results")]
    public async Task SharedConsumers_DownloadFromListedBlobWithoutUsingBrokenEndpoint(string consumer)
    {
        using var handler = new FileHandler();
        using var http = new HttpClient(handler);
        var service = new HelixService(CreateClient(http), http);

        if (consumer == "download")
        {
            var paths = await service.DownloadFilesAsync(JobId, WorkItem, FileName);
            try
            {
                Assert.Equal(ResultsXml, await File.ReadAllTextAsync(Assert.Single(paths)));
            }
            finally
            {
                foreach (var path in paths)
                    File.Delete(path);
                Directory.Delete(Path.GetDirectoryName(paths[0])!);
            }
        }
        else if (consumer == "search")
        {
            var result = await service.SearchFileAsync(JobId, WorkItem, FileName, "Expected failure");
            Assert.Single(result.Matches);
        }
        else
        {
            var results = await service.ParseTrxResultsAsync(
                JobId, WorkItem, fileName: consumer == "specific-results" ? FileName : null, maxResults: 10);
            var file = Assert.Single(results);
            Assert.Equal(1, file.TotalTests);
            Assert.Equal(1, file.Failed);
            Assert.Equal("Expected failure", Assert.Single(file.Results).ErrorMessage);
        }

        Assert.Equal(1, handler.BlobRequests);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith("/files/" + FileName));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, AcquisitionErrorKind.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, AcquisitionErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, AcquisitionErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AcquisitionErrorKind.TransportError)]
    public async Task GetFileAsync_HtmlHttpFailurePreservesStatusAndResource(
        HttpStatusCode status, AcquisitionErrorKind kind)
    {
        using var handler = new FileHandler { BlobStatus = status };
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => CreateClient(http).GetFileAsync(FileName, WorkItem, JobId));

        var error = AcquisitionAssertions.Error(ex, kind, "helix", "download_helix_file", (int)status);
        AcquisitionAssertions.Resource(error, "jobId", JobId);
        AcquisitionAssertions.Resource(error, "workItem", WorkItem);
        AcquisitionAssertions.Resource(error, "fileName", FileName);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/container/testResults.xml")]
    [InlineData("file:///tmp/testResults.xml")]
    public async Task GetFileAsync_InvalidListedLinkReportsInvalidResponse(string? link)
    {
        using var handler = new FileHandler { Link = link };
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => CreateClient(http).GetFileAsync(FileName, WorkItem, JobId));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "helix", "download_helix_file");
        Assert.Equal(0, handler.BlobRequests);
    }

    [Fact]
    public async Task GetFileAsync_MissingFileReportsNotFoundWithoutDownloading()
    {
        using var handler = new FileHandler();
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => CreateClient(http).GetFileAsync("missing.xml", WorkItem, JobId));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "helix", "download_helix_file", 404);
        Assert.Equal(0, handler.BlobRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetFileAsync_DistinguishesTimeoutFromCallerCancellation(bool callerCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FileHandler
        {
            BeforeDownload = () =>
            {
                if (callerCancellation)
                    cancellation.Cancel();
                throw new TaskCanceledException("Download interrupted", null, cancellation.Token);
            }
        };
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        if (callerCancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.GetFileAsync(FileName, WorkItem, JobId, cancellation.Token));
        else
        {
            var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(
                () => client.GetFileAsync(FileName, WorkItem, JobId, cancellation.Token));
            AcquisitionAssertions.Error(ex, AcquisitionErrorKind.Timeout, "helix", "download_helix_file");
        }
    }

    [Fact]
    public async Task GetFileAsync_ListedLinkPreservesEncodedFileNameAndQuery()
    {
        const string fileName = "subdir/a file+#.log";
        const string link = "https://results.blob.core.windows.net/job/subdir/a%20file%2B%23.log?sv=1&sig=a%2Bb%3D";
        using var handler = new FileHandler { Name = fileName, Link = link };
        using var http = new HttpClient(handler);
        await using var stream = await CreateClient(http).GetFileAsync(fileName, WorkItem, JobId);

        Assert.Equal(link, handler.Requests[^1].AbsoluteUri);
    }

    [Fact]
    public async Task GetFileAsync_CacheRetainsDownloadedBytesAndSkipsSecondNetworkRequest()
    {
        using var handler = new FileHandler();
        using var http = new HttpClient(handler);
        var cache = Substitute.For<ICacheStore>();
        byte[]? bytes = null;
        cache.GetArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => bytes is null ? null : new MemoryStream(bytes));
        cache.SetArtifactAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                using var copy = new MemoryStream();
                await call.Arg<Stream>().CopyToAsync(copy);
                bytes = copy.ToArray();
            });
        var client = new CachingHelixApiClient(CreateClient(http), cache, new CacheOptions());

        for (var i = 0; i < 2; i++)
        {
            await using var stream = await client.GetFileAsync(FileName, WorkItem, JobId);
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.Equal(ResultsXml, await reader.ReadToEndAsync());
        }

        Assert.True(handler.BodyDisposed);
        Assert.Equal(1, handler.BlobRequests);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static HelixApiClient CreateClient(HttpClient http)
    {
        var options = new HelixApiOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        return HelixApiClient.CreateForTesting(options, http);
    }

    private sealed class FileHandler : HttpMessageHandler
    {
        public string Name { get; init; } = FileName;
        public string? Link { get; init; } = BlobUrl;
        public HttpStatusCode BlobStatus { get; init; } = HttpStatusCode.OK;
        public Action? BeforeDownload { get; init; }
        public List<Uri> Requests { get; } = [];
        public MemoryStream? Body { get; private set; }
        public bool BodyDisposed => Body is not null && !Body.CanRead;
        public int BlobRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.Host == "helix.dot.net" && uri.AbsolutePath.EndsWith("/files"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[] { new { Name, Link } }),
                        Encoding.UTF8, "application/json")
                });

            Assert.Equal(new Uri(Link!), uri);
            Assert.Null(request.Headers.Authorization);
            BlobRequests++;
            BeforeDownload?.Invoke();
            Body = new MemoryStream(Encoding.UTF8.GetBytes(ResultsXml));
            return Task.FromResult(new HttpResponseMessage(BlobStatus)
            {
                Content = BlobStatus == HttpStatusCode.OK
                    ? new StreamContent(Body)
                    : new StringContent("<!DOCTYPE html><title>Error - Helix</title>", Encoding.UTF8, "text/html")
            });
        }
    }
}
