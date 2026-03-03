namespace HelixTool.Core;

/// <summary>
/// Resolves Helix job IDs and work item names from various input formats.
/// Accepts bare GUIDs, full Helix URLs, and URLs containing both job ID and work item segments.
/// </summary>
public static class HelixIdResolver
{
    /// <summary>
    /// Resolves a Helix job ID from a bare GUID or a full Helix URL.
    /// </summary>
    /// <param name="input">A GUID string or Helix URL containing a job GUID in the path.</param>
    /// <returns>The resolved job ID (GUID string).</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="input"/> is null, empty, or not a valid GUID or Helix URL.</exception>
    public static string ResolveJobId(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        // Already a GUID
        if (Guid.TryParse(input, out _))
            return input;

        // Helix URL: extract job ID from path
        // https://helix.dot.net/api/jobs/{jobId}/...
        // https://helix.dot.net/api/2019-06-17/jobs/{jobId}/...
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i] == "jobs" && Guid.TryParse(segments[i + 1], out _))
                    return segments[i + 1];
            }
        }

        throw new ArgumentException($"Invalid job ID: must be a GUID or Helix URL containing a job GUID. Got: '{input}'", nameof(input));
    }

    /// <summary>
    /// Try to extract both job ID and work item name from a Helix URL.
    /// Returns true if a job ID was found, with <paramref name="workItem"/> set if the URL also contains a work item segment.
    /// </summary>
    /// <param name="input">A GUID string or Helix URL.</param>
    /// <param name="jobId">The extracted job ID, or <see cref="string.Empty"/> if not found.</param>
    /// <param name="workItem">The extracted work item name, or <c>null</c> if not found.</param>
    /// <returns><c>true</c> if a job ID was successfully extracted; otherwise, <c>false</c>.</returns>
    public static bool TryResolveJobAndWorkItem(string input, out string jobId, out string? workItem)
    {
        jobId = string.Empty;
        workItem = null;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        // Plain GUID — jobId only
        if (Guid.TryParse(input, out _))
        {
            jobId = input;
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Find "jobs" segment and extract GUID after it
        int jobsIndex = -1;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i] == "jobs" && Guid.TryParse(segments[i + 1], out _))
            {
                jobsIndex = i;
                jobId = segments[i + 1];
                break;
            }
        }

        if (jobsIndex < 0)
            return false;

        // Look for "workitems" segment after the job ID
        int workItemsIndex = jobsIndex + 2;
        if (workItemsIndex < segments.Length && segments[workItemsIndex] == "workitems" && workItemsIndex + 1 < segments.Length)
        {
            // Work item name is the segment after "workitems", URL-decoded
            // Skip known trailing segments (console, files, etc.)
            string[] knownTrailingSegments = ["console", "files", "details"];
            string candidate = Uri.UnescapeDataString(segments[workItemsIndex + 1]);
            if (!knownTrailingSegments.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                workItem = candidate;
            }
        }

        return true;
    }
}
