using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelixTool.Core.Cache;

namespace HelixTool.Core.AzDO;

/// <summary>
/// HTTP-based implementation of <see cref="IAzdoApiClient"/>.
/// Uses Azure DevOps REST API v7.0 with Bearer or Basic auth.
/// </summary>
public sealed class AzdoApiClient : IAzdoApiClient
{
    private const int ErrorBodySnippetLimit = 500;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex s_secretAssignmentRegex = new(
        @"(?i)\b(?<name>token|key|password|secret)\s*=\s*(?<value>[^\s&;,]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_jwtRegex = new(
        @"\b[A-Za-z0-9_-]{3,}\.[A-Za-z0-9_-]{3,}\.[A-Za-z0-9_-]{3,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_base64Regex = new(
        @"(?<![A-Za-z0-9+/_-])(?=[A-Za-z0-9+/_-]{41,}={0,2}(?![A-Za-z0-9+/_-]))(?=[A-Za-z0-9+/_-]*[0-9+/=_-])[A-Za-z0-9+/_-]+={0,2}(?![A-Za-z0-9+/_-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly IAzdoTokenAccessor _tokenAccessor;
    private readonly CacheOptions _cacheOptions;

    public AzdoApiClient(HttpClient httpClient, IAzdoTokenAccessor tokenAccessor)
        : this(httpClient, tokenAccessor, new CacheOptions())
    {
    }

    public AzdoApiClient(HttpClient httpClient, IAzdoTokenAccessor tokenAccessor, CacheOptions cacheOptions)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
        _cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));
    }

    public async Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"build/builds/{buildId}");
        return await GetAsync<AzdoBuild>(org, project, url, ct);
    }

    public async Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default)
    {
        var queryParams = new List<string>();

        if (filter.Top is > 0)
            queryParams.Add($"$top={filter.Top}");

        if (!string.IsNullOrEmpty(filter.PrNumber))
        {
            if (!int.TryParse(filter.PrNumber, out var prNum))
                throw new ArgumentException("prNumber must be a valid integer.", nameof(filter));
            queryParams.Add($"branchName=refs/pull/{prNum}/merge");
        }
        else if (!string.IsNullOrEmpty(filter.Branch))
        {
            queryParams.Add($"branchName={Uri.EscapeDataString(filter.Branch)}");
        }

        if (filter.DefinitionId is > 0)
            queryParams.Add($"definitions={filter.DefinitionId}");

        if (!string.IsNullOrEmpty(filter.StatusFilter))
            queryParams.Add($"statusFilter={Uri.EscapeDataString(filter.StatusFilter)}");

        queryParams.Add("queryOrder=queueTimeDescending");

        var path = "build/builds?" + string.Join("&", queryParams);
        var url = BuildUrl(org, project, path);
        return await GetListAsync<AzdoBuild>(org, project, url, ct);
    }

    public async Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"build/builds/{buildId}/timeline");
        return await GetAsync<AzdoTimeline>(org, project, url, ct);
    }

    public async Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, int? startLine = null, int? endLine = null, CancellationToken ct = default)
    {
        var path = $"build/builds/{buildId}/logs/{logId}";
        var queryParts = new List<string>();
        if (startLine is not null) queryParts.Add($"startLine={startLine.Value}");
        if (endLine is not null) queryParts.Add($"endLine={endLine.Value}");
        if (queryParts.Count > 0)
            path += "?" + string.Join("&", queryParts);
        var url = BuildUrl(org, project, path);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var credential = await ApplyAuthAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        ThrowOnAuthFailure(response, org, project, credential);
        await ThrowOnUnexpectedError(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
    {
        var topParam = top is > 0 ? $"?$top={top}" : "";
        var url = BuildUrl(org, project, $"build/builds/{buildId}/changes{topParam}");
        return await GetListAsync<AzdoBuildChange>(org, project, url, ct);
    }

    public async Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
    {
        var buildUri = Uri.EscapeDataString($"vstfs:///Build/Build/{buildId}");
        var topParam = top is > 0 ? $"&$top={top}" : "";
        var url = BuildUrl(org, project, $"test/runs?buildUri={buildUri}{topParam}");
        return await GetListAsync<AzdoTestRun>(org, project, url, ct);
    }

    public async Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"test/runs/{runId}/results?$top={top}&outcomes=Failed");
        return await GetListAsync<AzdoTestResult>(org, project, url, ct);
    }

    public async Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAllOutcomesAsync(string org, string project, int runId, int top = 1000, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"test/runs/{runId}/results?$top={top}");
        return await GetListAsync<AzdoTestResult>(org, project, url, ct);
    }

    public async Task<AzdoTestResult?> GetTestResultWithSubResultsAsync(string org, string project, int runId, int resultId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"test/runs/{runId}/results/{resultId}?detailsToInclude=SubResults");
        return await GetAsync<AzdoTestResult>(org, project, url, ct);
    }

    public async Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"build/builds/{buildId}/artifacts");
        return await GetListAsync<AzdoBuildArtifact>(org, project, url, ct);
    }

    public async Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"test/runs/{runId}/results/{resultId}/attachments");
        return await GetListAsync<AzdoTestAttachment>(org, project, url, ct);
    }

    public async Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"build/builds/{buildId}/logs");
        return await GetListAsync<AzdoBuildLogEntry>(org, project, url, ct);
    }

    private static string BuildUrl(string org, string project, string path)
    {
        var separator = path.Contains('?') ? "&" : "?";
        return $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/{path}{separator}api-version=7.0";
    }

    private async Task<AzdoCredential?> ApplyAuthAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var credential = await _tokenAccessor.GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (credential is null || string.IsNullOrEmpty(credential.Token))
            return null;

        _cacheOptions.UpdateAuthContext(credential.CacheIdentity ?? AzdoCredential.BuildCacheIdentity(credential.Source, credential.DisplayToken));

        request.Headers.Authorization = credential.Scheme switch
        {
            "Basic" => new AuthenticationHeaderValue("Basic", credential.Token),
            _ => new AuthenticationHeaderValue("Bearer", credential.Token)
        };

        return credential;
    }

    private async Task<T?> GetAsync<T>(string org, string project, string url, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var credential = await ApplyAuthAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        ThrowOnAuthFailure(response, org, project, credential);
        await ThrowOnUnexpectedError(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, s_jsonOptions, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string org, string project, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var credential = await ApplyAuthAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        ThrowOnAuthFailure(response, org, project, credential);
        await ThrowOnUnexpectedError(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var wrapper = await JsonSerializer.DeserializeAsync<AzdoListResponse<T>>(stream, s_jsonOptions, ct).ConfigureAwait(false);
        return wrapper?.Value ?? [];
    }

    private void ThrowOnAuthFailure(HttpResponseMessage response, string org, string project, AzdoCredential? credential)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return;

        if (credential is not null)
            _tokenAccessor.InvalidateCachedCredential();

        var currentAuth = credential?.Source ?? "anonymous (no credentials found)";
        throw new HttpRequestException(
            $"Can't access {org}/{project} — authentication required ({(int)response.StatusCode}). Authentication failed.\n\n" +
            $"Current auth: {currentAuth}\n\n" +
            "To resolve:\n" +
            "• Run 'az login' (if your Azure identity has access to this org)\n" +
            "• Set AZDO_TOKEN to a Personal Access Token with Build(read) + Test(read) scopes\n" +
            "• Set AZDO_TOKEN to an Entra access token: az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv\n" +
            "• If AZDO_TOKEN is being misclassified, set AZDO_TOKEN_TYPE to 'pat' or 'bearer' to override detection",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static async Task ThrowOnUnexpectedError(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var snippet = body.Length > ErrorBodySnippetLimit ? body[..ErrorBodySnippetLimit] + "…" : body;
        snippet = RedactSensitiveContent(snippet);
        throw new HttpRequestException(
            $"AzDO API returned {(int)response.StatusCode}: {snippet}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static string RedactSensitiveContent(string snippet)
    {
        if (string.IsNullOrEmpty(snippet))
            return snippet;

        var redacted = s_secretAssignmentRegex.Replace(snippet, static match => $"{match.Groups["name"].Value}=[REDACTED]");
        redacted = s_jwtRegex.Replace(redacted, "[REDACTED-JWT]");
        return s_base64Regex.Replace(redacted, "[REDACTED-SECRET]");
    }
}
