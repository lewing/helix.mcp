namespace HelixTool.Core;

/// <summary>
/// Allocation-efficient string utilities for log processing.
/// </summary>
internal static class StringHelpers
{
    /// <summary>
    /// Return the last <paramref name="lineCount"/> lines of <paramref name="content"/>
    /// using reverse-scan — zero array allocation.
    /// Returns the original string if it has fewer lines than requested.
    /// </summary>
    public static string TailLines(string content, int lineCount)
    {
        if (lineCount <= 0)
            return content;

        var span = content.AsSpan();

        // Skip trailing newline to avoid counting a phantom empty last line
        var end = span.Length;
        if (end > 0 && span[end - 1] == '\n')
            end--;

        // Reverse-scan for the Nth-from-end newline
        var pos = end;
        for (int i = 0; i < lineCount && pos > 0; i++)
        {
            var idx = span[..pos].LastIndexOf('\n');
            if (idx < 0)
            {
                // Fewer lines than requested — return full content
                return content;
            }
            pos = idx;
        }

        // pos points at the newline before the tail block — skip it
        return content[(pos + 1)..];
    }
}
