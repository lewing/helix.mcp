using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HelixTool.Core.AzDO;

/// <summary>
/// HTTP-based implementation of <see cref="IAzdoApiClient"/>.
/// Uses Azure DevOps REST API v7.0 with Bearer token auth.
/// </summary>
public sealed class AzdoApiClient : IAzdoApiClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly IAzdoTokenAccessor _tokenAccessor;

    public AzdoApiClient(HttpClient httpClient, IAzdoTokenAccessor tokenAccessor)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
    }

    public async Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"build/builds/{buildId}");
        return await GetAsync<AzdoBuild>(url, ct);
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
            queryParams.Add($"branchName={Uri.EscapeDataString(filter.Branch)}");

        if (filter.DefinitionId is > 0)
            queryParams.Add($"definitions={filter.DefinitionId}");

        if (!string.IsNullOrEmpty(filter.StatusFilter))
            queryParams.Add($"statusFilter={Uri.EscapeDataString(filter.StatusFilter)}");

        queryParams.Add("queryOrder=queueTimeDescending");

        var path = "build/builds?" + string.Join("&", queryParams);
        var url = BuildUrl(org, project, path);
        return await GetListAsync<AzdoBuild>(url, ct);
    }

    public async Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"build/builds/{buildId}/timeline");
        return await GetAsync<AzdoTimeline>(url, ct);
    }

    public async Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"build/builds/{buildId}/logs/{logId}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await ThrowOnAuthFailure(response);
        await ThrowOnUnexpectedError(response);

        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
    {
        var topParam = top is > 0 ? $"?$top={top}" : "";
        var url = BuildUrl(org, project, $"build/builds/{buildId}/changes{topParam}");
        return await GetListAsync<AzdoBuildChange>(url, ct);
    }

    public async Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
    {
        var buildUri = Uri.EscapeDataString($"vstfs:///Build/Build/{buildId}");
        var topParam = top is > 0 ? $"&$top={top}" : "";
        var url = BuildUrl(org, project, $"test/runs?buildUri={buildUri}{topParam}");
        return await GetListAsync<AzdoTestRun>(url, ct);
    }

    public async Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, CancellationToken ct = default)
    {
        var url = BuildUrl(org, project, $"test/runs/{runId}/results?$top={top}&outcomes=Failed");
        return await GetListAsync<AzdoTestResult>(url, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string BuildUrl(string org, string project, string path)
    {
        // path may already contain query params (e.g. "test/runs?buildUri=...")
        var separator = path.Contains('?') ? "&" : "?";
        return $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/{path}{separator}api-version=7.0";
    }

    private async Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _tokenAccessor.GetAccessTokenAsync(ct);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await ThrowOnAuthFailure(response);
        await ThrowOnUnexpectedError(response);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, s_jsonOptions, ct);
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        await ThrowOnAuthFailure(response);
        await ThrowOnUnexpectedError(response);

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var wrapper = await JsonSerializer.DeserializeAsync<AzdoListResponse<T>>(stream, s_jsonOptions, ct);
        return wrapper?.Value ?? [];
    }

    private static async Task ThrowOnAuthFailure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException(
                $"Authentication failed ({(int)response.StatusCode}). Set AZDO_TOKEN env var or run 'az login'.",
                inner: null,
                statusCode: response.StatusCode);
        }
    }

    private static async Task ThrowOnUnexpectedError(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var snippet = body.Length > 500 ? body[..500] + "…" : body;
            throw new HttpRequestException(
                $"AzDO API returned {(int)response.StatusCode}: {snippet}",
                inner: null,
                statusCode: response.StatusCode);
        }
    }
}
