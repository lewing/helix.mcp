using System.Diagnostics;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Abstraction for resolving an Azure DevOps access token.
/// Chain: AZDO_TOKEN env var → az CLI → null (anonymous for public repos).
/// </summary>
public interface IAzdoTokenAccessor
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves AzDO tokens via AZDO_TOKEN env var or Azure CLI.
/// Does NOT use Azure.Identity — MSAL fails on WSL due to libsecret/D-Bus issues.
/// </summary>
public sealed class AzCliAzdoTokenAccessor : IAzdoTokenAccessor
{
    // Azure DevOps resource ID for az CLI token requests
    private const string AzdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    private string? _cachedToken;
    private bool _resolved;

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var envToken = Environment.GetEnvironmentVariable("AZDO_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
            return envToken;

        if (_resolved)
            return _cachedToken;

        _cachedToken = await TryGetAzCliTokenAsync(cancellationToken).ConfigureAwait(false);
        _resolved = true;
        return _cachedToken;
    }

    private static async Task<string?> TryGetAzCliTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"account get-access-token --resource {AzdoResourceId} --query accessToken -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
                return null;

            var token = output.Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            // az not installed, not in PATH, or any other failure — fall through to anonymous
            return null;
        }
    }
}
