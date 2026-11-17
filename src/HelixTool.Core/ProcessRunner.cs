using System.Diagnostics;

namespace HelixTool.Core;

/// <summary>Result of running an external process: exit code and captured standard output.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput);

/// <summary>
/// Seam for running an external process without a shell. Lets credential lookups be
/// tested without touching real OS state.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="arguments"/> (no shell) and capture stdout.
    /// Throws <see cref="InvalidOperationException"/> if the process cannot be started and
    /// <see cref="TimeoutException"/> if it does not exit within <paramref name="timeout"/>. On timeout or
    /// caller cancellation the process tree is killed; caller cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Default <see cref="IProcessRunner"/> backed by <see cref="Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        // Redirect stdin so the child can never consume the stdio MCP transport.
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.", ex);
        }

        try
        {
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested)
                    throw;
                throw new TimeoutException($"'{fileName}' timed out after {timeout.TotalSeconds}s.");
            }

            await stderrTask;
            return new ProcessRunResult(process.ExitCode, await stdoutTask);
        }
        finally
        {
            process.Dispose();
        }
    }
}
