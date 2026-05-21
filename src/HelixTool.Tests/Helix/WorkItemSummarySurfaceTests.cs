using System.Reflection;
using System.Text.Json;
using HelixTool.Core.Helix;
using Microsoft.DotNet.Helix.Client.Models;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class WorkItemSummarySurfaceTests
{
    [Fact]
    public void WorkItemSummaryAdapter_MapsExitCodeAndConsoleOutputUri()
    {
        var adapted = CreateAdapter(new WorkItemSummary("pr/55", "test", "placeholder", "https://helix.dot.net/api/jobs/job-1/workitems/workitem-1")
        {
            Name = "workitem-1",
            ExitCode = 17,
            ConsoleOutputUri = "https://helix.dot.net/api/2019-06-17/jobs/job-1/workitems/workitem-1/console"
        });

        Assert.Equal("workitem-1", adapted.Name);
        Assert.Equal(17, adapted.ExitCode);
        Assert.Equal("https://helix.dot.net/api/2019-06-17/jobs/job-1/workitems/workitem-1/console", adapted.ConsoleOutputUri);
    }

    [Fact]
    public void WorkItemSummaryAdapter_NullExitCode_ReturnsNull()
    {
        var adapted = CreateAdapter(new WorkItemSummary("pr/55", "test", "placeholder", "https://helix.dot.net/api/jobs/job-1/workitems/workitem-null-exit")
        {
            Name = "workitem-null-exit",
            ExitCode = null,
            ConsoleOutputUri = "https://helix.dot.net/api/2019-06-17/jobs/job-1/workitems/workitem-null-exit/console"
        });

        Assert.Null(adapted.ExitCode);
    }

    [Fact]
    public void WorkItemSummaryAdapter_NullConsoleOutputUri_ReturnsNull()
    {
        var adapted = CreateAdapter(new WorkItemSummary("pr/55", "test", "placeholder", "https://helix.dot.net/api/jobs/job-1/workitems/workitem-null-console")
        {
            Name = "workitem-null-console",
            ExitCode = 0,
            ConsoleOutputUri = null
        });

        Assert.Null(adapted.ConsoleOutputUri);
    }

    [Fact]
    public void WorkItemSummaryDto_RoundTripsExitCodeAndConsoleOutputUri()
    {
        var summary = Substitute.For<IWorkItemSummary>();
        summary.Name.Returns("workitem-1");
        summary.ExitCode.Returns(23);
        summary.ConsoleOutputUri.Returns("https://helix.dot.net/api/2019-06-17/jobs/job-1/workitems/workitem-1/console");

        var roundTripped = RoundTripDto(summary);

        Assert.Equal("workitem-1", roundTripped.Name);
        Assert.Equal(23, roundTripped.ExitCode);
        Assert.Equal("https://helix.dot.net/api/2019-06-17/jobs/job-1/workitems/workitem-1/console", roundTripped.ConsoleOutputUri);
    }

    [Fact]
    public void WorkItemSummaryDto_NullFields_SurviveJsonRoundTrip()
    {
        var summary = Substitute.For<IWorkItemSummary>();
        summary.Name.Returns("workitem-null-fields");
        summary.ExitCode.Returns((int?)null);
        summary.ConsoleOutputUri.Returns((string?)null);

        var roundTripped = RoundTripDto(summary);

        Assert.Equal("workitem-null-fields", roundTripped.Name);
        Assert.Null(roundTripped.ExitCode);
        Assert.Null(roundTripped.ConsoleOutputUri);
    }

    [Fact]
    public void WorkItemSummaryDto_MissingNewFields_DeserializesAsNull()
    {
        var roundTripped = DeserializeDto("{\"Name\":\"workitem-legacy\"}");

        Assert.Equal("workitem-legacy", roundTripped.Name);
        Assert.Null(roundTripped.ExitCode);
        Assert.Null(roundTripped.ConsoleOutputUri);
    }

    private static IWorkItemSummary CreateAdapter(WorkItemSummary summary)
    {
        var adapterType = typeof(HelixApiClient).GetNestedType("WorkItemSummaryAdapter", BindingFlags.NonPublic);
        Assert.NotNull(adapterType);

        var instance = Activator.CreateInstance(
            adapterType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [summary],
            culture: null);

        return Assert.IsAssignableFrom<IWorkItemSummary>(instance);
    }

    private static IWorkItemSummary RoundTripDto(IWorkItemSummary summary)
    {
        var dtoType = GetWorkItemSummaryDtoType();
        var fromMethod = dtoType.GetMethod("From", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(fromMethod);

        var dto = fromMethod!.Invoke(null, [summary]);
        var json = JsonSerializer.Serialize(dto, dtoType);
        var deserialized = JsonSerializer.Deserialize(json, dtoType);

        return Assert.IsAssignableFrom<IWorkItemSummary>(deserialized);
    }

    private static IWorkItemSummary DeserializeDto(string json)
    {
        var dtoType = GetWorkItemSummaryDtoType();
        var deserialized = JsonSerializer.Deserialize(json, dtoType);
        return Assert.IsAssignableFrom<IWorkItemSummary>(deserialized);
    }

    private static Type GetWorkItemSummaryDtoType()
    {
        var dtoType = typeof(CachingHelixApiClient).GetNestedType("WorkItemSummaryDto", BindingFlags.NonPublic);
        Assert.NotNull(dtoType);
        return dtoType!;
    }
}
