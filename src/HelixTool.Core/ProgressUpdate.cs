namespace HelixTool.Core;

/// <summary>
/// Transport-agnostic progress update emitted by long-running service operations.
/// The MCP tool layer translates these to <c>ProgressNotificationValue</c> for the
/// MCP SDK; the CLI layer can ignore them or render them however it likes.
/// </summary>
/// <param name="Current">Monotonically increasing value (items processed, bytes
/// transferred, percent complete, etc.). Should be non-negative.</param>
/// <param name="Total">Total expected value, when known. Use the same unit as
/// <paramref name="Current"/>. <c>null</c> when the total is unknown.</param>
/// <param name="Message">Optional human-readable status, e.g.
/// "Downloaded 42 of 128 MB" or "Searched 12 of 50 log steps".</param>
public readonly record struct ProgressUpdate(double Current, double? Total, string? Message);
