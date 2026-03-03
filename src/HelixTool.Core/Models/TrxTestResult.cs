namespace HelixTool.Core;

/// <summary>Parsed test result from a TRX file.</summary>
public record TrxTestResult(string TestName, string Outcome, string? Duration, string? ComputerName, string? ErrorMessage, string? StackTrace);
