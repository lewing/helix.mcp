namespace HelixTool.Core;

/// <summary>
/// Exception type for Helix API errors (decision D6).
/// Wraps <see cref="HttpRequestException"/> (network/HTTP errors) and
/// <see cref="TaskCanceledException"/> (timeouts) with human-readable messages.
/// Hosts (CLI and MCP) catch this type to present user-friendly error output.
/// </summary>
public class HelixException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="HelixException"/> with a human-readable message
    /// and an optional inner exception from the Helix SDK or HTTP stack.
    /// </summary>
    /// <param name="message">A descriptive error message (e.g., "Job 'abc' not found.").</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public HelixException(string message, Exception? inner = null)
        : base(message, inner) { }
}
