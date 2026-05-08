using System.Diagnostics;

namespace HelixTool.Core;

/// <summary>
/// Helpers for emitting coarse-grained <see cref="ProgressUpdate"/> events.
/// Aim is 5–10 updates over the lifetime of a long-running operation —
/// not per-byte / per-item — to keep MCP traffic low.
/// </summary>
public static class ProgressReporter
{
    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, emitting
    /// ~10 progress updates over the course of the copy when <paramref name="progress"/>
    /// is non-null. Falls back to a 1 MiB cadence when total length is unknown.
    /// </summary>
    public static async Task CopyToWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        Func<long, long?, string>? messageFactory,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var buffer = new byte[DefaultBufferSize];
        long copied = 0;

        long stepBytes = totalBytes is > 0 ? Math.Max(1, totalBytes.Value / 10) : 1L * 1024 * 1024;
        long nextEmitAt = stepBytes;
        var lastEmit = Stopwatch.StartNew();

        progress?.Report(new ProgressUpdate(
            0,
            totalBytes,
            messageFactory?.Invoke(0, totalBytes)));

        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;

            if (progress is not null && copied >= nextEmitAt && lastEmit.ElapsedMilliseconds >= 250)
            {
                progress.Report(new ProgressUpdate(
                    copied,
                    totalBytes,
                    messageFactory?.Invoke(copied, totalBytes)));
                nextEmitAt = copied + stepBytes;
                lastEmit.Restart();
            }
        }

        progress?.Report(new ProgressUpdate(
            copied,
            totalBytes ?? copied,
            messageFactory?.Invoke(copied, totalBytes ?? copied)));
    }

    /// <summary>
    /// Compute the smallest step that yields no more than ~10 updates over
    /// <paramref name="total"/> items. Always at least 1.
    /// </summary>
    public static int ItemStep(int total) => Math.Max(1, total / 10);

    /// <summary>Format bytes as a short MB string for human messages.</summary>
    public static string FormatMB(long bytes) => $"{bytes / 1024.0 / 1024.0:0.#} MB";
}
