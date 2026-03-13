using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public string AuthPath { get; init; } = Source;
    public string? CacheIdentity { get; init; }
    public DateTimeOffset? ExpiresOnUtc { get; init; }

    /// <summary>
    /// Legacy compatibility conversion from a raw token string.
    /// Warning: this assumes a Bearer token and preserves the raw token as <see cref="DisplayToken"/>.
    /// Prefer constructing <see cref="AzdoCredential"/> explicitly to avoid accidental credential exposure.
    /// </summary>
    [Obsolete("Implicit conversion from string to AzdoCredential is legacy compatibility only. Prefer constructing AzdoCredential explicitly to avoid accidental token exposure.")]
    public static implicit operator AzdoCredential?(string? token)
        => string.IsNullOrEmpty(token)
            ? null
            : new AzdoCredential(token, "Bearer", "Legacy string token")
            {
                DisplayToken = token,
                AuthPath = "legacy token",
                CacheIdentity = BuildCacheIdentity("legacy token", token),
                ExpiresOnUtc = TryGetJwtExpiration(token)
            };

    public override string ToString() => $"AzdoCredential {{ Scheme = {Scheme}, Source = {Source}, Token = [REDACTED] }}";

    internal static string BuildCacheIdentity(string prefix, string token)
    {
        if (TryGetJwtClaims(token, out var claims))
        {
            var parts = new[]
            {
                prefix,
                GetClaim(claims, "tid"),
                GetClaim(claims, "oid"),
                GetClaim(claims, "appid"),
                GetClaim(claims, "sub")
            }
            .Where(static part => !string.IsNullOrWhiteSpace(part));

            var identity = string.Join(':', parts);
            if (!string.IsNullOrWhiteSpace(identity))
                return identity;
        }

        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..8].ToLowerInvariant();
        return $"{prefix}:{tokenHash}";
    }

    internal static DateTimeOffset? TryGetJwtExpiration(string token)
    {
        if (!TryGetJwtClaims(token, out var claims) || !claims.TryGetProperty("exp", out var expElement))
            return null;

        if (expElement.ValueKind == JsonValueKind.Number && expElement.TryGetInt64(out var expSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(expSeconds);

        if (expElement.ValueKind == JsonValueKind.String && long.TryParse(expElement.GetString(), out expSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(expSeconds);

        return null;
    }

    private static bool TryGetJwtClaims(string token, out JsonElement claims)
    {
        claims = default;
        if (!LooksLikeJwt(token))
            return false;

        var segments = token.Split('.');
        if (segments.Length != 3)
            return false;

        try
        {
            var json = DecodeBase64Url(segments[1]);
            using var document = JsonDocument.Parse(json);
            claims = document.RootElement.Clone();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetClaim(JsonElement claims, string name)
        => claims.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padded = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };

        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }

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
}

public sealed record AzdoAuthStatus
{
    public bool IsAuthenticated { get; init; }
    public string Path { get; init; } = "anonymous";
    public string Source { get; init; } = "anonymous";
    public bool? LooksExpired { get; init; }
    public DateTimeOffset? ExpiresOnUtc { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Abstraction for resolving Azure DevOps authentication.
/// Chain: AZDO_TOKEN env var (optionally forced via AZDO_TOKEN_TYPE) → AzureCliCredential → az CLI → null (anonymous for public repos).
/// </summary>
public interface IAzdoTokenAccessor
{
    Task<AzdoCredential?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    Task<AzdoAuthStatus> AuthStatusAsync(CancellationToken cancellationToken = default);
    void InvalidateCachedCredential();
}

/// <summary>
/// Resolves AzDO credentials via AZDO_TOKEN, AzureCliCredential, or az CLI.
/// Env-var tokens are checked on every call; fallback credentials are refreshed on a shorter cadence than their full token lifetime.
/// </summary>
public sealed class AzCliAzdoTokenAccessor : IAzdoTokenAccessor
{
    private const string AzdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";
    private const string AzdoTokenEnvVarName = "AZDO_TOKEN";
    private const string AzdoTokenTypeEnvVarName = "AZDO_TOKEN_TYPE";
    private static readonly string[] s_azdoScopes = [$"{AzdoResourceId}/.default"];
    private static readonly TimeSpan FallbackCredentialTtl = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan MissingCredentialTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly AzureCliCredential _azureCliCredential = new();
    private readonly SemaphoreSlim _resolutionLock = new(1, 1);

    private CachedResolution? _cachedFallbackResolution;

    public async Task<AzdoCredential?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var envResolution = TryGetEnvResolution();
        if (envResolution is not null)
            return envResolution.Credential;

        var cached = _cachedFallbackResolution;
        if (cached is not null && cached.RefreshAfterUtc > DateTimeOffset.UtcNow)
            return cached.Credential;

        await _resolutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cachedFallbackResolution;
            if (cached is not null && cached.RefreshAfterUtc > DateTimeOffset.UtcNow)
                return cached.Credential;

            var resolved = await ResolveFallbackCredentialAsync(cancellationToken).ConfigureAwait(false);
            _cachedFallbackResolution = resolved;
            return resolved.Credential;
        }
        finally
        {
            _resolutionLock.Release();
        }
    }

    public async Task<AzdoAuthStatus> AuthStatusAsync(CancellationToken cancellationToken = default)
    {
        var envResolution = TryGetEnvResolution();
        if (envResolution is not null)
            return envResolution.Status;

        var cached = _cachedFallbackResolution;
        if (cached is not null && cached.RefreshAfterUtc > DateTimeOffset.UtcNow)
            return cached.Status;

        await _resolutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cachedFallbackResolution;
            if (cached is not null && cached.RefreshAfterUtc > DateTimeOffset.UtcNow)
                return cached.Status;

            var resolved = await ResolveFallbackCredentialAsync(cancellationToken).ConfigureAwait(false);
            _cachedFallbackResolution = resolved;
            return resolved.Status;
        }
        finally
        {
            _resolutionLock.Release();
        }
    }

    public void InvalidateCachedCredential() => _cachedFallbackResolution = null;

    private async Task<CachedResolution> ResolveFallbackCredentialAsync(CancellationToken cancellationToken)
    {
        var azureIdentityCredential = await TryGetAzureIdentityCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (azureIdentityCredential is not null)
            return CreateResolution(azureIdentityCredential);

        var azCliCredential = await TryGetAzCliTokenAsync(cancellationToken).ConfigureAwait(false);
        if (azCliCredential is not null)
            return CreateResolution(azCliCredential);

        return CreateAnonymousResolution();
    }

    private async Task<AzdoCredential?> TryGetAzureIdentityCredentialAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await _azureCliCredential
                .GetTokenAsync(new TokenRequestContext(s_azdoScopes), cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(token.Token)
                ? null
                : CreateCredential(
                    wireToken: token.Token,
                    displayToken: token.Token,
                    scheme: "Bearer",
                    source: "AzureCliCredential",
                    authPath: "AzureCliCredential",
                    cacheIdentityPrefix: "AzureCliCredential",
                    expiresOnUtc: token.ExpiresOn);
        }
        catch (CredentialUnavailableException)
        {
            return null;
        }
        catch (AuthenticationFailedException)
        {
            return null;
        }
    }

    private static AzdoCredential? TryGetEnvCredential() => TryGetEnvResolution()?.Credential;

    private static CachedResolution? TryGetEnvResolution()
    {
        var envToken = Environment.GetEnvironmentVariable(AzdoTokenEnvVarName);
        if (string.IsNullOrEmpty(envToken))
            return null;

        AzdoCredential credential = GetExplicitSchemeOverride() switch
        {
            "Bearer" => CreateCredential(
                wireToken: envToken,
                displayToken: envToken,
                scheme: "Bearer",
                source: "AZDO_TOKEN (Bearer)",
                authPath: "environment variable",
                cacheIdentityPrefix: "env:AZDO_TOKEN:bearer",
                expiresOnUtc: AzdoCredential.TryGetJwtExpiration(envToken)),
            "Basic" => CreateCredential(
                wireToken: EncodePatForBasic(envToken),
                displayToken: envToken,
                scheme: "Basic",
                source: "AZDO_TOKEN (PAT)",
                authPath: "environment variable",
                cacheIdentityPrefix: "env:AZDO_TOKEN:pat",
                expiresOnUtc: null),
            _ => LooksLikeJwt(envToken)
                ? CreateCredential(
                    wireToken: envToken,
                    displayToken: envToken,
                    scheme: "Bearer",
                    source: "AZDO_TOKEN (Bearer)",
                    authPath: "environment variable",
                    cacheIdentityPrefix: "env:AZDO_TOKEN:bearer",
                    expiresOnUtc: AzdoCredential.TryGetJwtExpiration(envToken))
                : CreateCredential(
                    wireToken: EncodePatForBasic(envToken),
                    displayToken: envToken,
                    scheme: "Basic",
                    source: "AZDO_TOKEN (PAT)",
                    authPath: "environment variable",
                    cacheIdentityPrefix: "env:AZDO_TOKEN:pat",
                    expiresOnUtc: null)
        };

        return CreateResolution(credential, cacheResult: false);
    }

    private static string? GetExplicitSchemeOverride()
    {
        var tokenType = Environment.GetEnvironmentVariable(AzdoTokenTypeEnvVarName);
        if (string.Equals(tokenType, "bearer", StringComparison.OrdinalIgnoreCase))
            return "Bearer";

        if (string.Equals(tokenType, "pat", StringComparison.OrdinalIgnoreCase))
            return "Basic";

        return null;
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
                Arguments = $"account get-access-token --resource {AzdoResourceId} -o json",
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

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("accessToken", out var tokenElement))
                return null;

            var token = tokenElement.GetString()?.Trim();
            if (string.IsNullOrEmpty(token))
                return null;

            return CreateCredential(
                wireToken: token,
                displayToken: token,
                scheme: "Bearer",
                source: "az CLI",
                authPath: "az CLI",
                cacheIdentityPrefix: "az CLI",
                expiresOnUtc: TryGetAzCliExpiration(document.RootElement) ?? AzdoCredential.TryGetJwtExpiration(token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // az not installed, not in PATH, or any other failure — fall through to anonymous
            return null;
        }
    }

    private static DateTimeOffset? TryGetAzCliExpiration(JsonElement root)
    {
        if (TryParseExpiration(root, "expiresOn", out var expiresOn) ||
            TryParseExpiration(root, "expires_on", out expiresOn))
        {
            return expiresOn;
        }

        return null;
    }

    private static bool TryParseExpiration(JsonElement root, string propertyName, out DateTimeOffset expiresOn)
    {
        expiresOn = default;
        if (!root.TryGetProperty(propertyName, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixSeconds))
        {
            expiresOn = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
            return false;

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixSeconds))
        {
            expiresOn = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out expiresOn);
    }

    private static AzdoCredential CreateCredential(
        string wireToken,
        string displayToken,
        string scheme,
        string source,
        string authPath,
        string cacheIdentityPrefix,
        DateTimeOffset? expiresOnUtc)
        => new(wireToken, scheme, source)
        {
            DisplayToken = displayToken,
            AuthPath = authPath,
            CacheIdentity = AzdoCredential.BuildCacheIdentity(cacheIdentityPrefix, displayToken),
            ExpiresOnUtc = expiresOnUtc
        };

    private static CachedResolution CreateResolution(AzdoCredential credential, bool cacheResult = true)
    {
        var warnings = BuildWarnings(credential);
        return new CachedResolution(
            credential,
            new AzdoAuthStatus
            {
                IsAuthenticated = true,
                Path = credential.AuthPath,
                Source = credential.Source,
                LooksExpired = IsExpired(credential.ExpiresOnUtc),
                ExpiresOnUtc = credential.ExpiresOnUtc,
                Warnings = warnings
            },
            cacheResult ? GetRefreshAfterUtc(credential.ExpiresOnUtc) : DateTimeOffset.MaxValue);
    }

    private static CachedResolution CreateAnonymousResolution()
    {
        var warnings = new[] { "No AzDO credentials resolved. Only public Azure DevOps resources are accessible." };
        return new CachedResolution(
            Credential: null,
            Status: new AzdoAuthStatus
            {
                IsAuthenticated = false,
                Path = "anonymous",
                Source = "anonymous",
                LooksExpired = null,
                ExpiresOnUtc = null,
                Warnings = warnings
            },
            RefreshAfterUtc: DateTimeOffset.UtcNow.Add(MissingCredentialTtl));
    }

    private static IReadOnlyList<string> BuildWarnings(AzdoCredential credential)
    {
        var warnings = new List<string>();
        var looksExpired = IsExpired(credential.ExpiresOnUtc);

        if (looksExpired == true)
            warnings.Add("Resolved credential appears expired and should be refreshed.");
        else if (credential.ExpiresOnUtc.HasValue && credential.ExpiresOnUtc.Value <= DateTimeOffset.UtcNow.Add(RefreshSkew))
            warnings.Add($"Resolved credential expires soon ({credential.ExpiresOnUtc.Value:u}).");
        else if (credential.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            warnings.Add("PAT expiry cannot be determined locally.");
        else if (!credential.ExpiresOnUtc.HasValue)
            warnings.Add("Token expiry could not be determined locally.");

        return warnings;
    }

    private static bool? IsExpired(DateTimeOffset? expiresOnUtc)
        => expiresOnUtc.HasValue ? expiresOnUtc.Value <= DateTimeOffset.UtcNow : null;

    private static DateTimeOffset GetRefreshAfterUtc(DateTimeOffset? expiresOnUtc)
    {
        var now = DateTimeOffset.UtcNow;
        var refreshAfter = now.Add(FallbackCredentialTtl);
        if (!expiresOnUtc.HasValue)
            return refreshAfter;

        var earlyRefresh = expiresOnUtc.Value - RefreshSkew;
        if (earlyRefresh <= now)
            return now;

        return earlyRefresh < refreshAfter ? earlyRefresh : refreshAfter;
    }

    private sealed record CachedResolution(AzdoCredential? Credential, AzdoAuthStatus Status, DateTimeOffset RefreshAfterUtc);
}
