using System.Diagnostics;
using System.Text;

namespace HelixTool.Core;

/// <summary>
/// Stores and retrieves credentials using <c>git credential</c> CLI,
/// which delegates to the OS keychain (macOS Keychain, Windows Credential Manager, libsecret).
/// </summary>
public sealed class GitCredentialStore : ICredentialStore
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    public async Task<string?> GetTokenAsync(string host, string username, CancellationToken ct = default)
    {
        ValidateArgs(host, username);

        var input = BuildInput(host, username);
        var (exitCode, stdout, _) = await RunGitCredentialAsync("fill", input, ct);

        if (exitCode != 0)
            return null;

        // Parse "password=<value>" from output
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("password=", StringComparison.Ordinal))
                return line["password=".Length..];
        }

        return null;
    }

    public async Task StoreTokenAsync(string host, string username, string token, CancellationToken ct = default)
    {
        ValidateArgs(host, username);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var input = BuildInput(host, username, token);
        var (exitCode, _, stderr) = await RunGitCredentialAsync("approve", input, ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"git credential approve failed (exit {exitCode}): {stderr}");
    }

    public async Task DeleteTokenAsync(string host, string username, CancellationToken ct = default)
    {
        ValidateArgs(host, username);

        var input = BuildInput(host, username);
        // reject may "fail" if no credential exists — that's fine, we treat this as idempotent
        await RunGitCredentialAsync("reject", input, ct);
    }

    private static string BuildInput(string host, string username, string? password = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("protocol=https");
        sb.AppendLine($"host={host}");
        sb.AppendLine($"username={username}");
        if (password is not null)
            sb.AppendLine($"password={password}");
        sb.AppendLine(); // blank line terminates input
        return sb.ToString();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCredentialAsync(
        string subcommand, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", $"credential {subcommand}")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "git is not installed or not on PATH. Install git or set HELIX_ACCESS_TOKEN manually.", ex);
        }

        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProcessTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — kill the process
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"git credential {subcommand} timed out after {ProcessTimeout.TotalSeconds}s.");
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void ValidateArgs(string host, string username)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(username);
    }
}
