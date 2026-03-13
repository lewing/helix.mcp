using System.Diagnostics;
using System.Text;
using Azure.Core;
using Azure.Identity;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Token + metadata for AzDO authentication.
/// Scheme: "Bearer" for Entra/JWT tokens, "Basic" for PATs.
/// Source: human-readable label for error messages.
/// </summary>
[DebuggerDisplay("{Scheme} credential from {Source}")]
public sealed record AzdoCredential(string Token, string Scheme, string Source)
{
    public string DisplayToken { get; init; } = Token;

    public static implicit operator string?(AzdoCredential? credential) => credential?.DisplayToken;

    public static implicit operator AzdoCredential?(string? token)
        => string.IsNullOrEmpty(token)
            ? null
            : new AzdoCredential(token, "Bearer", "Legacy string token") { DisplayToken = token };

    public override string ToString() => $"AzdoCredential {{ Scheme = {Scheme}, Source = {Source}, Token = [REDACTED] }}";
}

/// <summary>
/// Abstraction for resolving Azure DevOps authentication.
/// Chain: AZDO_TOKEN env var → AzureCliCredential → az CLI → null (anonymous for public repos).
/// </summary>
public interface IAzdoTokenAccessor
{
    Task<AzdoCredential?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves AzDO credentials via AZDO_TOKEN, AzureCliCredential, or az CLI.
/// Env-var tokens are checked on every call; fallback chain is cached for the process lifetime.
/// </summary>
public sealed class AzCliAzdoTokenAccessor : IAzdoTokenAccessor
{
    private const string AzdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly string[] s_azdoScopes = [$"{AzdoResourceId}/.default"];

    private readonly AzureCliCredential _azureCliCredential = new();
    private readonly SemaphoreSlim _resolutionLock = new(1, 1);
    private readonly SemaphoreSlim _azureIdentityLock = new(1, 1);

    private AzdoCredential? _cachedCredential;
    private bool _resolved;
    private AzdoCredential? _cachedAzureIdentityCredential;
    private bool _azureIdentityResolved;

    public async Task<AzdoCredential?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var envCredential = TryGetEnvCredential();
        if (envCredential is not null)
            return envCredential;

        if (_resolved)
            return _cachedCredential;

        await _resolutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolved)
                return _cachedCredential;

            _cachedCredential = await ResolveFallbackCredentialAsync(cancellationToken).ConfigureAwait(false);
            _resolved = true;
            return _cachedCredential;
        }
        finally
        {
            _resolutionLock.Release();
        }
    }

    private async Task<AzdoCredential?> ResolveFallbackCredentialAsync(CancellationToken cancellationToken)
    {
        var azureIdentityCredential = await TryGetAzureIdentityCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (azureIdentityCredential is not null)
            return azureIdentityCredential;

        return await TryGetAzCliTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AzdoCredential?> TryGetAzureIdentityCredentialAsync(CancellationToken cancellationToken)
    {
        if (_azureIdentityResolved)
            return _cachedAzureIdentityCredential;

        await _azureIdentityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_azureIdentityResolved)
                return _cachedAzureIdentityCredential;

            try
            {
                var token = await _azureCliCredential
                    .GetTokenAsync(new TokenRequestContext(s_azdoScopes), cancellationToken)
                    .ConfigureAwait(false);

                _cachedAzureIdentityCredential = string.IsNullOrWhiteSpace(token.Token)
                    ? null
                    : new AzdoCredential(token.Token, "Bearer", "AzureCliCredential") { DisplayToken = token.Token };
            }
            catch (CredentialUnavailableException)
            {
                _cachedAzureIdentityCredential = null;
            }
            catch (AuthenticationFailedException)
            {
                _cachedAzureIdentityCredential = null;
            }

            _azureIdentityResolved = true;
            return _cachedAzureIdentityCredential;
        }
        finally
        {
            _azureIdentityLock.Release();
        }
    }

    private static AzdoCredential? TryGetEnvCredential()
    {
        var envToken = Environment.GetEnvironmentVariable("AZDO_TOKEN");
        if (string.IsNullOrEmpty(envToken))
            return null;

        return LooksLikeJwt(envToken)
            ? new AzdoCredential(envToken, "Bearer", "AZDO_TOKEN (Bearer)") { DisplayToken = envToken }
            : new AzdoCredential(EncodePatForBasic(envToken), "Basic", "AZDO_TOKEN (PAT)") { DisplayToken = envToken };
    }

    private static string EncodePatForBasic(string token)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}"));

    private static bool LooksLikeJwt(string token)
    {
        var firstDot = token.IndexOf('.');
        if (firstDot < 0)
            return false;

        var secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot < 0)
            return false;

        return token.IndexOf('.', secondDot + 1) < 0;
    }

    private static async Task<AzdoCredential?> TryGetAzCliTokenAsync(CancellationToken cancellationToken)
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
            return string.IsNullOrEmpty(token)
                ? null
                : new AzdoCredential(token, "Bearer", "az CLI") { DisplayToken = token };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // az not installed, not in PATH, or any other failure — fall through to anonymous
            return null;
        }
    }
}
