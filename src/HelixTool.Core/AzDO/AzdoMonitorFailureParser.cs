using System.Text.RegularExpressions;

namespace HelixTool.Core.AzDO;

internal static class AzdoMonitorFailureParser
{
    private static readonly Regex HelixJobIdRegex = new(
        @"jobs/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FailedWorkItemRegex = new(
        @"Work item (.+?) in job ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}) has failed",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MonitorFailedWorkItemRegex = new(
        @"Work item '(?<wi>[^']+)' in job '(?<job>[^']*)' failed\s*(?:\((?<details>[^)]*)\))?\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex JobGuidInTextRegex = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExitCodeRegex = new(
        @"exit code (?<exit>-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<AzdoParsedMonitorFailure> ParseMessage(string message)
    {
        var scan = ParseMessageCore(message);
        return scan.Failures;
    }

    public static AzdoMonitorFailureScan ScanTimeline(
        AzdoTimeline timeline,
        IReadOnlyCollection<string> selectedJobIds)
    {
        var selected = selectedJobIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
            return new AzdoMonitorFailureScan();

        var tasksByJob = timeline.Records
            .Where(record =>
                string.Equals(record.Type, "Task", StringComparison.OrdinalIgnoreCase)
                && record.ParentId is { Length: > 0 }
                && selected.Contains(record.ParentId))
            .GroupBy(record => record.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var monitorJobs = new List<AzdoMonitorJobEvidence>();
        foreach (var job in timeline.Records.Where(record =>
            string.Equals(record.Type, "Job", StringComparison.OrdinalIgnoreCase)
            && record.Id is { Length: > 0 }
            && selected.Contains(record.Id)))
        {
            tasksByJob.TryGetValue(job.Id!, out var tasks);
            tasks ??= [];

            var taskEvidence = new List<AzdoMonitorTaskEvidence>(tasks.Count);
            var failures = new List<AzdoParsedMonitorFailure>();
            var failureKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolvedCount = 0;
            var monitorLike = ContainsMonitorAndHelix(job.Name);

            foreach (var task in tasks)
            {
                taskEvidence.Add(new AzdoMonitorTaskEvidence
                {
                    TaskId = task.Id ?? "",
                    TaskName = task.Name ?? "",
                    TaskResult = task.Result,
                    TaskState = task.State
                });

                if (ContainsMonitorAndHelix(task.Name))
                    monitorLike = true;

                foreach (var issue in task.Issues ?? [])
                {
                    if (string.IsNullOrEmpty(issue.Message))
                        continue;

                    var messageScan = ParseMessageCore(issue.Message!);
                    if (messageScan.IsMonitorLike)
                        monitorLike = true;
                    unresolvedCount += messageScan.UnresolvedFailureEntryCount;

                    foreach (var failure in messageScan.Failures)
                    {
                        var key = $"{job.Id}\0{failure.HelixJobId}\0{failure.WorkItem}";
                        if (!failureKeys.Add(key))
                            continue;

                        failures.Add(failure with
                        {
                            MonitorTaskId = task.Id,
                            MonitorTaskName = task.Name
                        });
                    }
                }
            }

            if (failures.Count > 0 || monitorLike || unresolvedCount > 0)
            {
                monitorJobs.Add(new AzdoMonitorJobEvidence
                {
                    MonitorJobId = job.Id!,
                    MonitorJobName = job.Name ?? "",
                    MonitorJobResult = job.Result,
                    MonitorJobOrder = job.Order,
                    MonitorJobAttempt = job.Attempt,
                    Tasks = taskEvidence,
                    Failures = failures,
                    UnresolvedFailureEntryCount = unresolvedCount,
                    IsMonitorLike = monitorLike || failures.Count > 0 || unresolvedCount > 0
                });
            }
        }

        return new AzdoMonitorFailureScan { MonitorJobs = monitorJobs };
    }

    private static MessageScan ParseMessageCore(string message)
    {
        var failures = new List<AzdoParsedMonitorFailure>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedCount = 0;
        var monitorLike = IsMonitorLikeMessage(message);

        foreach (Match match in FailedWorkItemRegex.Matches(message))
        {
            AddFailure(
                failures,
                keys,
                BuildFailure(
                    match.Groups[2].Value,
                    jobText: match.Groups[2].Value,
                    workItem: match.Groups[1].Value,
                    details: null,
                    sourceFormat: "legacy"));
        }

        var lines = message.Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            foreach (Match match in MonitorFailedWorkItemRegex.Matches(lines[lineIndex]))
            {
                var warningJobText = match.Groups["job"].Value;
                var jobId = RecoverMonitorJobId(warningJobText, lines, lineIndex);
                if (jobId is null)
                {
                    unresolvedCount++;
                    continue;
                }

                AddFailure(
                    failures,
                    keys,
                    BuildFailure(
                        jobId,
                        warningJobText,
                        match.Groups["wi"].Value,
                        GetOptionalGroupValue(match, "details"),
                        "monitor-warning"));
            }

            if (TryParseMonitorFailureTreeLine(
                lines[lineIndex], out var workItemName, out var jobText, out var details))
            {
                var jobId = RecoverMonitorJobId(jobText, lines, lineIndex);
                if (jobId is null)
                {
                    unresolvedCount++;
                    continue;
                }

                AddFailure(
                    failures,
                    keys,
                    BuildFailure(
                        jobId,
                        jobText,
                        workItemName,
                        details,
                        "monitor-tree"));
            }
        }

        return new MessageScan(failures, unresolvedCount, monitorLike || failures.Count > 0 || unresolvedCount > 0);
    }

    private static void AddFailure(
        List<AzdoParsedMonitorFailure> failures,
        HashSet<string> keys,
        AzdoParsedMonitorFailure failure)
    {
        var key = $"{failure.HelixJobId}\0{failure.WorkItem}";
        if (keys.Add(key))
            failures.Add(failure);
    }

    private static AzdoParsedMonitorFailure BuildFailure(
        string helixJobId,
        string jobText,
        string workItem,
        string? details,
        string sourceFormat)
    {
        var (helixJobName, leg, queue) = ParseJobLabel(jobText);
        var (state, exitCode, normalizedDetails) = ParseDetails(details);
        return new AzdoParsedMonitorFailure
        {
            HelixJobId = helixJobId,
            HelixJobName = helixJobName,
            Leg = leg,
            Queue = queue,
            WorkItem = workItem,
            State = state,
            ExitCode = exitCode,
            Details = normalizedDetails,
            SourceFormat = sourceFormat
        };
    }

    private static (string? State, int? ExitCode, string? Details) ParseDetails(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return (null, null, null);

        var normalized = details.Trim();
        var comma = normalized.IndexOf(',', StringComparison.Ordinal);
        var state = (comma >= 0 ? normalized[..comma] : normalized).Trim();
        if (state.Length == 0)
            state = null;

        int? exitCode = null;
        var exitMatch = ExitCodeRegex.Match(normalized);
        if (exitMatch.Success
            && int.TryParse(exitMatch.Groups["exit"].Value, out var parsedExitCode))
        {
            exitCode = parsedExitCode;
        }

        return (state, exitCode, normalized);
    }

    private static (string? HelixJobName, string? Leg, string? Queue) ParseJobLabel(string jobText)
    {
        var label = jobText.Trim();
        var guid = JobGuidInTextRegex.Match(label);
        if (guid.Success)
        {
            var parentheticalGuid = $"({guid.Value})";
            var parentheticalIndex = label.IndexOf(parentheticalGuid, StringComparison.OrdinalIgnoreCase);
            label = parentheticalIndex >= 0
                ? label.Remove(parentheticalIndex, parentheticalGuid.Length)
                : label.Remove(guid.Index, guid.Length);
            label = label.Trim();
        }

        if (label.Length == 0)
            return (null, null, null);

        var separatorIndex = label.LastIndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex < 0)
            return (label, label, null);

        var leg = label[..separatorIndex].Trim();
        var queue = label[(separatorIndex + 3)..].Trim();
        return (
            label,
            leg.Length == 0 ? null : leg,
            queue.Length == 0 ? null : queue);
    }

    private static string? RecoverMonitorJobId(
        string jobText, string[] lines, int entryLineIndex)
    {
        var directMatch = JobGuidInTextRegex.Match(jobText);
        if (directMatch.Success)
            return directMatch.Value;

        var consoleBlockLines = lines
            .Skip(entryLineIndex + 1)
            .TakeWhile(line => !IsMonitorFailureEntry(line))
            .SkipWhile(line => !line.Contains("Console:", StringComparison.OrdinalIgnoreCase));
        var consoleBlock = string.Join('\n', consoleBlockLines);
        var consoleJobIds = HelixJobIdRegex.Matches(consoleBlock)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return consoleJobIds.Count == 1 ? consoleJobIds[0] : null;
    }

    private static bool IsMonitorFailureEntry(string line) =>
        MonitorFailedWorkItemRegex.IsMatch(line)
        || TryParseMonitorFailureTreeLine(line, out _, out _, out _);

    private static bool TryParseMonitorFailureTreeLine(
        string line, out string workItemName, out string jobText, out string? details)
    {
        const string jobMarker = " (Job: ";
        var markerIndex = line.IndexOf(jobMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            workItemName = "";
            jobText = "";
            details = null;
            return false;
        }

        workItemName = line[..markerIndex].Trim();
        if (workItemName.StartsWith("├─", StringComparison.Ordinal)
            || workItemName.StartsWith("└─", StringComparison.Ordinal))
        {
            workItemName = workItemName[2..].Trim();
        }
        else
        {
            jobText = "";
            details = null;
            return false;
        }

        var remainder = line[(markerIndex + jobMarker.Length)..].Trim();
        var detailsSeparator = remainder.LastIndexOf(") (", StringComparison.Ordinal);
        if (detailsSeparator >= 0 && remainder.EndsWith(')'))
        {
            jobText = remainder[..(detailsSeparator + 1)].Trim();
            details = remainder[(detailsSeparator + 3)..^1].Trim();
        }
        else
        {
            jobText = remainder.EndsWith(')') ? remainder[..^1].Trim() : remainder;
            details = null;
        }

        return workItemName.Length > 0;
    }

    private static bool IsMonitorLikeMessage(string message) =>
        message.Contains("Failed work item information:", StringComparison.OrdinalIgnoreCase)
        || (message.Contains("Work item '", StringComparison.OrdinalIgnoreCase)
            && message.Contains(" in job '", StringComparison.OrdinalIgnoreCase)
            && message.Contains(" failed", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsMonitorAndHelix(string? value) =>
        value is not null
        && value.Contains("monitor", StringComparison.OrdinalIgnoreCase)
        && value.Contains("helix", StringComparison.OrdinalIgnoreCase);

    private static string? GetOptionalGroupValue(Match match, string groupName)
    {
        var group = match.Groups[groupName];
        return group.Success && group.Value.Length > 0 ? group.Value : null;
    }

    private sealed record MessageScan(
        IReadOnlyList<AzdoParsedMonitorFailure> Failures,
        int UnresolvedFailureEntryCount,
        bool IsMonitorLike);
}

internal sealed record AzdoParsedMonitorFailure
{
    public required string HelixJobId { get; init; }
    public string? HelixJobName { get; init; }
    public string? Leg { get; init; }
    public string? Queue { get; init; }
    public required string WorkItem { get; init; }
    public string? State { get; init; }
    public int? ExitCode { get; init; }
    public string? Details { get; init; }
    public required string SourceFormat { get; init; }
    public string? MonitorTaskId { get; init; }
    public string? MonitorTaskName { get; init; }
}

internal sealed record AzdoMonitorFailureScan
{
    public IReadOnlyList<AzdoMonitorJobEvidence> MonitorJobs { get; init; } = [];
}

internal sealed record AzdoMonitorJobEvidence
{
    public required string MonitorJobId { get; init; }
    public required string MonitorJobName { get; init; }
    public string? MonitorJobResult { get; init; }
    public int? MonitorJobOrder { get; init; }
    public int? MonitorJobAttempt { get; init; }
    public IReadOnlyList<AzdoMonitorTaskEvidence> Tasks { get; init; } = [];
    public IReadOnlyList<AzdoParsedMonitorFailure> Failures { get; init; } = [];
    public int UnresolvedFailureEntryCount { get; init; }
    public bool IsMonitorLike { get; init; }
}

internal sealed record AzdoMonitorTaskEvidence
{
    public required string TaskId { get; init; }
    public required string TaskName { get; init; }
    public string? TaskResult { get; init; }
    public string? TaskState { get; init; }
}
