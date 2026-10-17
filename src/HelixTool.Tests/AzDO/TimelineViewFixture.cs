using System.Text.Json;
using System.IO.Compression;
using HelixTool.Core.AzDO;

namespace HelixTool.Tests.AzDO;

internal static class TimelineViewFixture
{
    public const string MonitorTask = "0ca3371c-1de8-59d8-daef-35ea5811e911";
    public const string MonitorJob = "078edc60-2a90-5618-e72d-426a045b11f0";
    public const string MonitorPhase = "64403e3f-dfc4-5e18-208d-36820c6b1984";
    public const string MonitorStage = "6884a131-87da-5381-61f3-d7acc3b91d76";
    public const string WarningTask = "d4ca02d4-194b-5811-701c-828273e14a89";
    public const string AndroidTask = "e5019902-447d-5e21-c822-5af0405ee703";
    public const string AndroidJob = "1c0dd94e-116d-509f-042d-34216159d046";
    public const string AndroidPhase = "d004588b-a49f-5ffe-ee53-43ca4a2b3a66";
    public const string AndroidStage = "ffea5239-6632-59ea-62d8-1ecf86bde54e";
    public const string MonitorMessage = "Work item 'System.Diagnostics.Process.Tests' in job 'windows-x86 Debug Libraries_CheckedCoreCLR - windows.10.amd64.open.rt (d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a)' failed (Finished, exit code -3).";
    public const string AndroidWarning = ".dotnet/packs/Microsoft.Android.Sdk.Darwin/36.1.115/tools/Xamarin.Android.Common.targets(576,3): warning XA1040: (NETCORE_ENGINEERING_TELEMETRY=Build) The CoreCLR runtime on Android is an experimental feature and not yet suitable for production use. File issues at: https://github.com/dotnet/android/issues";
    public const string XunitWarning = "src/Controls/tests/DeviceTests/Elements/SwipeView/SwipeViewTests.Android.cs(445,22): warning xUnit2031: (NETCORE_ENGINEERING_TELEMETRY=Build) Do not use a Where clause to filter before calling Assert.Single. Use the overload of Assert.Single that accepts a filtering function. (https://xunit.net/xunit.analyzers/rules/xUnit2031)";

    public static AzdoTimeline FullReal(int buildId)
    {
        // Complete anonymous 7.1 captures; strip worker identities, URLs and issue data,
        // retaining every record in provider order, log IDs, original messages and attempts.
        var name = $"HelixTool.Tests.AzDO.Fixtures.timeline-{buildId}.json.gz";
        using var resource = typeof(TimelineViewFixture).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing timeline fixture {name}.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var timeline = JsonSerializer.Deserialize<AzdoTimeline>(gzip)
            ?? throw new InvalidOperationException($"Invalid timeline fixture {name}.");
        var expected = buildId == 1621192 ? 1524 : 189;
        if (timeline.Records.Count != expected)
            throw new InvalidOperationException($"Fixture {buildId} has {timeline.Records.Count}, expected {expected} records.");
        return timeline;
    }

    // Anonymous api-version=7.1 timelines fetched on 2026-10-03. Keep provider ordering,
    // identities, attempts, log IDs and issue text; omit workers, URLs and issue data.
    public static AzdoTimeline Real(int buildId)
    {
        var timeline = JsonSerializer.Deserialize<AzdoTimeline>(buildId == 1621192 ? RuntimeJson : MauiJson)!;
        if (buildId == 1621192)
            return WithIssues(timeline, MonitorTask,
                new() { Type = "warning", Message = MonitorMessage },
                new() { Type = "error", Message = "Failed work item information:" },
                new() { Type = "error", Message = "Bash exited with code '1'." });

        timeline = WithIssues(timeline, WarningTask,
            new() { Type = "warning", Message = AndroidWarning },
            new() { Type = "warning", Message = XunitWarning },
            new() { Type = "warning", Message = AndroidWarning },
            new() { Type = "warning", Message = AndroidWarning },
            new() { Type = "warning", Message = AndroidWarning },
            new() { Type = "warning", Message = AndroidWarning });
        return WithIssues(timeline, "33a0db08-903d-5a23-b2dc-a39b93ae42ef",
            new AzdoIssue { Type = "warning", Message = XunitWarning });
    }

    public static AzdoTimeline WithIssues(AzdoTimeline timeline, string id, params AzdoIssue[] issues) =>
        timeline with { Records = timeline.Records.Select(r => r.Id == id ? r with { Issues = issues } : r).ToArray() };

    public static AzdoTimeline WithSelectorRows()
    {
        var timeline = Real(1621192);
        return timeline with
        {
            Records = [.. timeline.Records,
                Row(1, "skipped", "completed", "Skipped task"),
                Row(2, null, "inProgress", "Running task"),
                Row(3, null, "pending", "Pending task"),
                Row(4, "canceled", "completed", "Canceled task"),
                Row(5, "succeeded", "completed", "Successful task"),
                Row(6, "succeededWithIssues", "completed", "Warning task") with
                { Issues = [new() { Type = "warning", Message = "warning AX123: path/a.cs (attempt 2)" }] },
                Row(7, "abandoned", "completed", "Abandoned task")]
        };
    }

    public static string SyntheticId(int i) => $"10000000-0000-0000-0000-{i:D12}";

    public static AzdoTimeline FullIssueLookup(bool includeAncestor = true)
    {
        var task = new AzdoTimelineRecord
        {
            Id = SyntheticId(5), ParentId = SyntheticId(6), Type = "Task", Name = "Successful issue-bearing task",
            State = "completed", Result = "succeeded", Attempt = 1, Log = new() { Id = 3005 },
            Issues = [new() { Type = "warning", Message = new string('x', 1024 * 1024) },
                new() { Type = "warning", Message = "second issue" },
                new() { Type = "warning", Message = "third issue" }]
        };
        return new AzdoTimeline
        {
            Id = "c4e04671-d712-4fc6-94d8-eb26de84c004",
            Records = includeAncestor ? [task, new()
            {
                Id = SyntheticId(6), Type = "Job", Name = "Existing job ancestor",
                State = "completed", Result = "succeeded", Attempt = 1, Log = new() { Id = 3006 }
            }] : [task]
        };
    }

    public static AzdoTimeline EscapedLongName(int repeats) => new()
    {
        Id = "c4e04671-d712-4fc6-94d8-eb26de84c004",
        Records = [new()
        {
            Id = MonitorTask, Type = "Task",
            Name = string.Concat(Enumerable.Repeat("quote\"path\\\U0001f680 ", repeats)),
            State = "completed", Result = "failed", Attempt = 1, Log = new() { Id = 1466 },
            Issues = [new() { Type = "error", Message = "error AX42: work item failure" }]
        }]
    };

    public static AzdoTimeline EscapedLongNameWithSecondRow(int repeats)
    {
        var source = EscapedLongName(repeats);
        return source with
        {
            Records = [.. source.Records, source.Records[0] with
            {
                Id = SyntheticId(999), Name = "Second failed task", Log = new() { Id = 1467 }
            }]
        };
    }

    private static AzdoTimelineRecord Row(int i, string? result, string state, string name) =>
        new()
        {
            Id = SyntheticId(i), ParentId = MonitorJob, Type = "Task", Name = name,
            State = state, Result = result, Attempt = 2, Log = new() { Id = 2000 + i }
        };

    private const string RuntimeJson = """
        {"id":"c4e04671-d712-4fc6-94d8-eb26de84c004","records":[
          {"id":"0ca3371c-1de8-59d8-daef-35ea5811e911","parentId":"078edc60-2a90-5618-e72d-426a045b11f0","type":"Task","name":"Monitor Helix Jobs","state":"completed","result":"failed","attempt":1,"startTime":"2026-10-02T14:16:00.15Z","finishTime":"2026-10-02T15:53:53.74Z","log":{"id":1466}},
          {"id":"64403e3f-dfc4-5e18-208d-36820c6b1984","parentId":"6884a131-87da-5381-61f3-d7acc3b91d76","type":"Phase","name":"Monitor Helix Jobs","state":"completed","result":"failed","attempt":1,"log":{"id":25}},
          {"id":"078edc60-2a90-5618-e72d-426a045b11f0","parentId":"64403e3f-dfc4-5e18-208d-36820c6b1984","type":"Job","name":"Monitor Helix Jobs","state":"completed","result":"failed","attempt":1,"log":{"id":1470}},
          {"id":"6884a131-87da-5381-61f3-d7acc3b91d76","type":"Stage","name":"Build","state":"completed","result":"failed","attempt":1}
        ]}
        """;

    private const string MauiJson = """
        {"id":"83d15a17-0eb8-462b-96df-c4f2310c0f2f","records":[
          {"id":"e20acdca-eeb3-5ae5-100e-18d2dadbb64f","type":"Stage","name":"net10.0 ios/catalyst/android Helix Tests (Mono)","state":"completed","result":"succeeded","attempt":1},
          {"id":"ffea5239-6632-59ea-62d8-1ecf86bde54e","type":"Stage","name":"net10.0 Android CoreCLR Helix Tests","state":"completed","result":"failed","attempt":1},
          {"id":"1c0dd94e-116d-509f-042d-34216159d046","parentId":"d004588b-a49f-5ffe-ee53-43ca4a2b3a66","type":"Job","name":"Run DeviceTests Android (CoreCLR)","state":"completed","result":"failed","attempt":1,"log":{"id":131}},
          {"id":"54ce2422-1436-5104-981e-342f5dfaed85","parentId":"e20acdca-eeb3-5ae5-100e-18d2dadbb64f","type":"Phase","name":"Build Device Tests (Mono)","state":"completed","result":"succeeded","attempt":1,"log":{"id":5}},
          {"id":"d004588b-a49f-5ffe-ee53-43ca4a2b3a66","parentId":"ffea5239-6632-59ea-62d8-1ecf86bde54e","type":"Phase","name":"Run DeviceTests Android (CoreCLR)","state":"completed","result":"failed","attempt":1,"log":{"id":66}},
          {"id":"3b8b9615-d024-53c3-156e-48db2a0aa843","type":"Stage","name":"net10.0 Android CoreCLR Helix Tests","state":"completed","result":"succeeded","attempt":1},
          {"id":"765641a5-ea20-5e22-c2ea-4d7c8d4b0999","type":"Stage","name":"net10.0 Android Helix Tests (Mono)","state":"completed","result":"failed","attempt":1},
          {"id":"e5019902-447d-5e21-c822-5af0405ee703","parentId":"1c0dd94e-116d-509f-042d-34216159d046","type":"Task","name":"DeviceTestsAndroid_CoreCLR (Windows)","state":"completed","result":"failed","attempt":1,"startTime":"2026-09-17T11:53:26.9533333Z","finishTime":"2026-09-17T12:11:07.4033333Z","log":{"id":122},"issues":[{"type":"error","message":"PowerShell exited with code '1'."}]},
          {"id":"00725272-3d4b-566b-7608-6d58b3794786","parentId":"8f8acecf-107f-5984-4202-78958040070e","type":"Task","name":"DeviceTestsAndroid (Windows)","state":"completed","result":"failed","attempt":1,"log":{"id":146},"issues":[{"type":"error","message":"PowerShell exited with code '1'."}]},
          {"id":"c743b6b4-5eb5-540d-4c96-76c0788a57ce","parentId":"26217327-703b-568a-9bbe-ac5689ad34a8","type":"Task","name":"DeviceTestsWindows (Windows)","state":"completed","result":"failed","attempt":1,"log":{"id":136},"issues":[{"type":"error","message":"PowerShell exited with code '1'."}]},
          {"id":"8f8acecf-107f-5984-4202-78958040070e","parentId":"a261511a-7c1f-5c79-dac5-f0d7e2d65ddf","type":"Job","name":"Run DeviceTests Android (Mono)","state":"completed","result":"failed","attempt":1,"log":{"id":155}},
          {"id":"d4ca02d4-194b-5811-701c-828273e14a89","parentId":"ac0eb1b1-ad4b-5212-615f-a46528cf86b1","type":"Task","name":"Build DeviceTests (CoreCLR)","state":"completed","result":"succeeded","attempt":1,"log":{"id":54}},
          {"id":"dbca3b79-c01d-55a8-8e64-89ee68dd2560","parentId":"54ce2422-1436-5104-981e-342f5dfaed85","type":"Job","name":"Build Device Tests (Mono)","state":"completed","result":"succeeded","attempt":1,"log":{"id":87}},
          {"id":"33a0db08-903d-5a23-b2dc-a39b93ae42ef","parentId":"dbca3b79-c01d-55a8-8e64-89ee68dd2560","type":"Task","name":"Build DeviceTests","state":"completed","result":"succeeded","attempt":1,"log":{"id":75}},
          {"id":"ac0eb1b1-ad4b-5212-615f-a46528cf86b1","parentId":"bb3a4fde-b63f-5133-c0dd-c20ad4a92018","type":"Job","name":"Build Device Tests (CoreCLR)","state":"completed","result":"succeeded","attempt":1,"log":{"id":65}},
          {"id":"26217327-703b-568a-9bbe-ac5689ad34a8","parentId":"ffaf4fd1-e7ee-53a6-8707-da5ecdb05054","type":"Job","name":"Run DeviceTests Windows","state":"completed","result":"failed","attempt":1,"log":{"id":145}},
          {"id":"bb3a4fde-b63f-5133-c0dd-c20ad4a92018","parentId":"3b8b9615-d024-53c3-156e-48db2a0aa843","type":"Phase","name":"Build Device Tests (CoreCLR)","state":"completed","result":"succeeded","attempt":1,"log":{"id":4}},
          {"id":"3c9a716f-4b28-5481-d312-d256e26d988a","type":"Stage","name":"net10.0 Windows Helix Tests","state":"completed","result":"failed","attempt":1},
          {"id":"ffaf4fd1-e7ee-53a6-8707-da5ecdb05054","parentId":"3c9a716f-4b28-5481-d312-d256e26d988a","type":"Phase","name":"Run DeviceTests Windows","state":"completed","result":"failed","attempt":1,"log":{"id":121}},
          {"id":"a261511a-7c1f-5c79-dac5-f0d7e2d65ddf","parentId":"765641a5-ea20-5e22-c2ea-4d7c8d4b0999","type":"Phase","name":"Run DeviceTests Android (Mono)","state":"completed","result":"failed","attempt":1,"log":{"id":88}}
        ]}
        """;
}
