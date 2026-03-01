using System.Text.Json.Serialization;

namespace HelixTool.Core;

// --- Status tool ---

public sealed class StatusJobInfo
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("queueId")] public string QueueId { get; init; } = "";
    [JsonPropertyName("creator")] public string Creator { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("created")] public string? Created { get; init; }
    [JsonPropertyName("finished")] public string? Finished { get; init; }
    [JsonPropertyName("helixUrl")] public string HelixUrl { get; init; } = "";
}

public sealed class StatusWorkItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("exitCode")] public int ExitCode { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("machineName")] public string? MachineName { get; init; }
    [JsonPropertyName("duration")] public string? Duration { get; init; }
    [JsonPropertyName("consoleLogUrl")] public string ConsoleLogUrl { get; init; } = "";
    [JsonPropertyName("failureCategory")] public string? FailureCategory { get; init; }
}

public sealed class StatusResult
{
    [JsonPropertyName("job")] public StatusJobInfo Job { get; init; } = new();
    [JsonPropertyName("totalWorkItems")] public int TotalWorkItems { get; init; }
    [JsonPropertyName("failedCount")] public int FailedCount { get; init; }
    [JsonPropertyName("passedCount")] public int PassedCount { get; init; }
    [JsonPropertyName("failed")] public List<StatusWorkItem>? Failed { get; init; }
    [JsonPropertyName("passed")] public List<StatusWorkItem>? Passed { get; init; }
}

// --- Files tool ---

public sealed class FileInfo_
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("uri")] public string Uri { get; init; } = "";
}

public sealed class FilesResult
{
    [JsonPropertyName("binlogs")] public List<FileInfo_> Binlogs { get; init; } = [];
    [JsonPropertyName("testResults")] public List<FileInfo_> TestResults { get; init; } = [];
    [JsonPropertyName("other")] public List<FileInfo_> Other { get; init; } = [];
}

// --- FindFiles tool ---

public sealed class FindFilesWorkItem
{
    [JsonPropertyName("workItem")] public string WorkItem { get; init; } = "";
    [JsonPropertyName("files")] public List<FileInfo_> Files { get; init; } = [];
}

public sealed class FindFilesResult
{
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("scannedItems")] public int ScannedItems { get; init; }
    [JsonPropertyName("found")] public int Found { get; init; }
    [JsonPropertyName("results")] public List<FindFilesWorkItem> Results { get; init; } = [];
}

// --- Download tool ---

public sealed class DownloadResult
{
    [JsonPropertyName("downloadedFiles")] public List<string> DownloadedFiles { get; init; } = [];
}

// --- DownloadUrl tool ---

public sealed class DownloadUrlResult
{
    [JsonPropertyName("downloadedFile")] public string DownloadedFile { get; init; } = "";
}

// --- WorkItem tool ---

public sealed class WorkItemToolResult
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("exitCode")] public int ExitCode { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("machineName")] public string? MachineName { get; init; }
    [JsonPropertyName("duration")] public string? Duration { get; init; }
    [JsonPropertyName("consoleLogUrl")] public string ConsoleLogUrl { get; init; } = "";
    [JsonPropertyName("failureCategory")] public string? FailureCategory { get; init; }
    [JsonPropertyName("files")] public List<FileInfo_> Files { get; init; } = [];
}

// --- SearchLog tool ---

public sealed class SearchMatch
{
    [JsonPropertyName("lineNumber")] public int LineNumber { get; init; }
    [JsonPropertyName("line")] public string Line { get; init; } = "";
    [JsonPropertyName("context")] public List<string>? Context { get; init; }
}

public sealed class SearchLogResult
{
    [JsonPropertyName("workItem")] public string WorkItem { get; init; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("totalLines")] public int TotalLines { get; init; }
    [JsonPropertyName("matchCount")] public int MatchCount { get; init; }
    [JsonPropertyName("matches")] public List<SearchMatch> Matches { get; init; } = [];
}

// --- SearchFile tool ---

public sealed class SearchFileResult
{
    [JsonPropertyName("fileName")] public string FileName { get; init; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("totalLines")] public int TotalLines { get; init; }
    [JsonPropertyName("matchCount")] public int MatchCount { get; init; }
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("matches")] public List<SearchMatch> Matches { get; init; } = [];
}

// --- TestResults tool ---

public sealed class TestResultEntry
{
    [JsonPropertyName("testName")] public string TestName { get; init; } = "";
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
    [JsonPropertyName("duration")] public string? Duration { get; init; }
    [JsonPropertyName("computerName")] public string? ComputerName { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    [JsonPropertyName("stackTrace")] public string? StackTrace { get; init; }
}

public sealed class TestResultFile
{
    [JsonPropertyName("fileName")] public string FileName { get; init; } = "";
    [JsonPropertyName("totalTests")] public int TotalTests { get; init; }
    [JsonPropertyName("passed")] public int Passed { get; init; }
    [JsonPropertyName("failed")] public int Failed { get; init; }
    [JsonPropertyName("skipped")] public int Skipped { get; init; }
    [JsonPropertyName("results")] public List<TestResultEntry> Results { get; init; } = [];
}

public sealed class TestResultsToolResult
{
    [JsonPropertyName("workItem")] public string WorkItem { get; init; } = "";
    [JsonPropertyName("fileCount")] public int FileCount { get; init; }
    [JsonPropertyName("files")] public List<TestResultFile> Files { get; init; } = [];
}

// --- BatchStatus tool ---

public sealed class BatchJobEntry
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("failedCount")] public int FailedCount { get; init; }
    [JsonPropertyName("passedCount")] public int PassedCount { get; init; }
    [JsonPropertyName("totalCount")] public int TotalCount { get; init; }
}

public sealed class BatchStatusResult
{
    [JsonPropertyName("jobs")] public List<BatchJobEntry> Jobs { get; init; } = [];
    [JsonPropertyName("totalFailed")] public int TotalFailed { get; init; }
    [JsonPropertyName("totalPassed")] public int TotalPassed { get; init; }
    [JsonPropertyName("jobCount")] public int JobCount { get; init; }
    [JsonPropertyName("failureBreakdown")] public Dictionary<string, int>? FailureBreakdown { get; init; }
}
