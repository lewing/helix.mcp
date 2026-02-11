namespace HelixTool.Core;

public static class HelixIdResolver
{
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
    /// Returns true if both were found, false if only jobId or neither.
    /// </summary>
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
