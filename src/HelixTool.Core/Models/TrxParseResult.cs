namespace HelixTool.Core;

/// <summary>Summary of parsed TRX test results.</summary>
public record TrxParseResult(string FileName, int TotalTests, int Passed, int Failed, int Skipped, List<TrxTestResult> Results);
