using System.Reflection;
using System.Text.Json;
using HelixTool.Core.Helix;
using Microsoft.DotNet.Helix.Client.Models;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Surface tests for the JobDetails adapter→DTO→cache pipeline, mirroring the pattern
/// established in WorkItemSummarySurfaceTests for the WorkItemSummary pipeline.
/// Covers: JobDetailsAdapter property projection, JobDetailsDto.From() mapping,
/// JSON round-trip, backward-compat deserialization of legacy cache entries, and
/// HelixService's empty-string DockerTag → null normalization.
/// </summary>
public class JobDetailsSurfaceTests
{
    private const string ValidJobId = "b2c3d4e5-f6a7-8901-bcde-f01234567890";

    // ========================================================================
    // 1. Adapter mapping — JobDetailsAdapter delegates to the SDK JobDetails
    // ========================================================================

    [Fact]
    public void JobDetailsAdapter_MapsQueueAliasAndDockerTag()
    {
        var adapted = CreateAdapter(MakeJobDetails(
            queueAlias: "windows.10.amd64.open",
            dockerTag: "ubuntu-22-helix-amd64-20231215123456"));

        Assert.Equal("windows.10.amd64.open", adapted.QueueAlias);
        Assert.Equal("ubuntu-22-helix-amd64-20231215123456", adapted.DockerTag);
    }

    [Fact]
    public void JobDetailsAdapter_NullQueueAlias_ReturnsNull()
    {
        var adapted = CreateAdapter(MakeJobDetails(queueAlias: null, dockerTag: "ubuntu-22-helix-amd64-20231215123456"));

        Assert.Null(adapted.QueueAlias);
    }

    [Fact]
    public void JobDetailsAdapter_NullDockerTag_ReturnsNull()
    {
        var adapted = CreateAdapter(MakeJobDetails(queueAlias: "windows.10.amd64.open", dockerTag: null));

        Assert.Null(adapted.DockerTag);
    }

    // ========================================================================
    // 2. DTO From() mapping + JSON round-trip
    // ========================================================================

    [Fact]
    public void JobDetailsDto_RoundTripsQueueAliasAndDockerTag()
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("job-round-trip");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.QueueAlias.Returns("windows.10.amd64.open");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/55");
        jobDetails.DockerTag.Returns("ubuntu-22-helix-amd64-20231215123456");

        var roundTripped = RoundTripDto(jobDetails);

        Assert.Equal("job-round-trip", roundTripped.Name);
        Assert.Equal("windows.10.amd64.open", roundTripped.QueueAlias);
        Assert.Equal("ubuntu-22-helix-amd64-20231215123456", roundTripped.DockerTag);
    }

    [Fact]
    public void JobDetailsDto_NullFields_SurviveJsonRoundTrip()
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("job-null-fields");
        jobDetails.QueueAlias.Returns((string?)null);
        jobDetails.DockerTag.Returns((string?)null);

        var roundTripped = RoundTripDto(jobDetails);

        Assert.Equal("job-null-fields", roundTripped.Name);
        Assert.Null(roundTripped.QueueAlias);
        Assert.Null(roundTripped.DockerTag);
    }

    // ========================================================================
    // 3. Backward-compat: deserializing a legacy cache entry that predates
    //    QueueAlias and DockerTag should yield null, not throw.
    // ========================================================================

    [Fact]
    public void JobDetailsDto_MissingNewFields_DeserializesAsNull()
    {
        // Simulate a cached JSON blob written before QueueAlias and DockerTag existed.
        var roundTripped = DeserializeDto("{\"Name\":\"job-legacy\",\"QueueId\":\"windows.10.amd64\",\"Creator\":\"testuser\",\"Source\":\"pr/55\"}");

        Assert.Equal("job-legacy", roundTripped.Name);
        Assert.Null(roundTripped.QueueAlias);
        Assert.Null(roundTripped.DockerTag);
    }

    // ========================================================================
    // 4. Empty-string DockerTag → null normalization in HelixService
    // ========================================================================

    [Fact]
    public async Task HelixService_EmptyStringDockerTag_NormalizedToNull()
    {
        var mockApi = Substitute.For<IHelixApiClient>();

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("job-empty-docker");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.QueueAlias.Returns((string?)null);
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/55");
        jobDetails.DockerTag.Returns("");  // empty string — HelixService must normalize to null

        mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);
        mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary>());

        var svc = new HelixService(mockApi, new HttpClient());
        var result = await svc.GetJobStatusAsync(ValidJobId);

        Assert.Null(result.DockerTag);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static JobDetails MakeJobDetails(string? queueAlias, string? dockerTag)
        => new JobDetails(
            "placeholder-job-list",
            new JobWorkItemCounts(0, 0, 0, 1, "https://helix.dot.net/api/jobs/job-1/workitems"),
            "job-1-name",
            "https://helix.dot.net/api/jobs/job-1/wait",
            "pr/55",
            "internal",
            "20240101.1")
        {
            QueueAlias = queueAlias,
            DockerTag = dockerTag
        };

    private static IJobDetails CreateAdapter(JobDetails details)
    {
        var adapterType = typeof(HelixApiClient).GetNestedType("JobDetailsAdapter", BindingFlags.NonPublic);
        Assert.NotNull(adapterType);

        var instance = Activator.CreateInstance(
            adapterType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [details],
            culture: null);

        return Assert.IsAssignableFrom<IJobDetails>(instance);
    }

    private static IJobDetails RoundTripDto(IJobDetails jobDetails)
    {
        var dtoType = GetJobDetailsDtoType();
        var fromMethod = dtoType.GetMethod("From", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(fromMethod);

        var dto = fromMethod!.Invoke(null, [jobDetails]);
        var json = JsonSerializer.Serialize(dto, dtoType);
        var deserialized = JsonSerializer.Deserialize(json, dtoType);

        return Assert.IsAssignableFrom<IJobDetails>(deserialized);
    }

    private static IJobDetails DeserializeDto(string json)
    {
        var dtoType = GetJobDetailsDtoType();
        var deserialized = JsonSerializer.Deserialize(json, dtoType);
        return Assert.IsAssignableFrom<IJobDetails>(deserialized);
    }

    private static Type GetJobDetailsDtoType()
    {
        var dtoType = typeof(CachingHelixApiClient).GetNestedType("JobDetailsDto", BindingFlags.NonPublic);
        Assert.NotNull(dtoType);
        return dtoType!;
    }
}
