namespace HelixTool.Core;

/// <summary>Category of work item failure.</summary>
public enum FailureCategory
{
    Unknown,
    Timeout,
    Crash,
    AssertionFailure,
    InfrastructureError,
    BuildFailure,
    TestFailure
}
