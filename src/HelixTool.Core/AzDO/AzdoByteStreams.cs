using System.Net;
using System.Text.Json;
using HelixTool.Core.Acquisition;

namespace HelixTool.Core.AzDO;

public sealed class AzdoByteStream(Stream content, string contentType, long? length = null,
    HttpResponseMessage? response = null) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public string ContentType { get; } = contentType;
    public long? Length { get; } = length;
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        response?.Dispose();
    }
}

public sealed partial class AzdoApiClient
{
    public async Task<IReadOnlyList<AzdoTestAttachment>> GetRunTestAttachmentsAsync(
        string org, string project, int runId, int top = 50, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        var results = new List<AzdoTestAttachment>();
        var requested = top > 0 ? top : 50;
        while (results.Count < requested)
        {
            var count = Math.Min(requested - results.Count, MaxTestAttachmentsPerRequest);
            var url = BuildUrl(org, project, $"test/runs/{runId}/attachments?$top={count}&$skip={results.Count}");
            var page = await GetListAsync<AzdoTestAttachment>(org, project, url, "list_run_test_attachments",
                Resource(org, project, ("runId", runId)), ct, requestedLimit: count);
            results.AddRange(page);
            if (page.Count < count) break;
        }
        return results;
    }

    public Task<AzdoByteStream> OpenArtifactAsync(string org, string project, int buildId,
        AzdoBuildArtifact artifact, string? memberPath = null, CancellationToken ct = default)
    {
        var type = artifact.Resource?.Type;
        if (!string.Equals(type, "PipelineArtifact", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported artifact resource type '{type}'.");
        var url = artifact.Resource?.DownloadUrl
            ?? throw new InvalidDataException("Artifact metadata has no download endpoint.");
        if (memberPath is not null)
        {
            var path = AzdoDownloadPaths.NormalizeMember(memberPath);
            var formatKey = string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase) ? "$format" : "format";
            url = WithQueryParameter(url, formatKey, "file");
            url = WithQueryParameter(url, "subPath", "/" + path);
        }
        return OpenBytesAsync(org, project, url,
            Resource(org, project, ("buildId", buildId), ("artifactId", artifact.Id)), ct, requestedFile: memberPath is not null);
    }

    public Task<AzdoByteStream> OpenTestAttachmentAsync(string org, string project, int runId,
        int? resultId, int attachmentId, CancellationToken ct = default)
    {
        var scope = resultId is int id ? $"results/{id}/" : "";
        return OpenBytesAsync(org, project,
            BuildUrl(org, project, $"test/runs/{runId}/{scope}attachments/{attachmentId}"),
            Resource(org, project, ("runId", runId), ("resultId", resultId), ("attachmentId", attachmentId)), ct, requestedFile: true);
    }

    private async Task<AzdoByteStream> OpenBytesAsync(string org, string project, string url,
        IReadOnlyDictionary<string, object?> resource, CancellationToken ct, bool requestedFile)
    {
        const string operation = "download_bytes";
        var uri = new Uri(url, UriKind.Absolute);
        var origin = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(org)}/");
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsApprovedByteHost(uri))
                throw new InvalidDataException("Unsafe artifact download host or redirect.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (uri.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase) &&
                !uri.AbsolutePath.StartsWith(origin.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Artifact download changed the expected Azure organization.");
            if (uri.Scheme == origin.Scheme && uri.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals($"{org}.visualstudio.com", StringComparison.OrdinalIgnoreCase))
                await ApplyAuthAsync(request, ct);
            var response = await SendAsync(request, operation, resource, ct);
            try
            {
                if (IsRedirect(response.StatusCode))
                {
                    if (response.Headers.Location is null)
                        throw new InvalidDataException("Artifact redirect has no Location.");
                    var next = new Uri(uri, response.Headers.Location);
                    if (!IsApprovedByteHost(next))
                        throw new InvalidDataException("Unsafe artifact download redirect.");
                    uri = next;
                    response.Dispose();
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
                    throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(AcquisitionErrorKind.AccessDenied,
                        "azdo", operation, resource, "AzDO returned a sign-in response instead of the requested file."));
                await ThrowOnUnexpectedError(response, operation, resource, ct);
                if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not null)
                    throw new InvalidDataException("Provider returned a partial body without a validated Range request; restart the exact request without Range.");
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                if (!requestedFile && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
                    throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                        mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ? AcquisitionErrorKind.AccessDenied : AcquisitionErrorKind.InvalidResponse,
                        "azdo", operation, resource, "AzDO returned a sign-in or metadata document instead of binary evidence."));
                var body = await response.Content.ReadAsStreamAsync(ct);
                var prefix = new byte[512];
                var count = await body.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct);
                var text = System.Text.Encoding.UTF8.GetString(prefix, 0, count).TrimStart();
                if (!requestedFile && (text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)))
                    throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(AcquisitionErrorKind.AccessDenied,
                        "azdo", operation, resource, "Provider returned a sign-in HTML body instead of evidence bytes."));
                return new AzdoByteStream(new PrefixStream(body, prefix.AsMemory(0, count)), mediaType,
                    response.Content.Headers.ContentLength, response);
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Artifact redirect chain exceeded five hops.");
    }

    private sealed class PrefixStream(Stream inner, ReadOnlyMemory<byte> prefix) : Stream
    {
        private int _offset;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= prefix.Length) return inner.ReadAsync(buffer, cancellationToken);
            var count = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.Slice(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static bool IsApprovedByteHost(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || IPAddress.TryParse(uri.Host, out _))
            return false;
        var host = uri.IdnHost;
        return host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".visualstudio.com", ".vsblob.dev.azure.com", ".blob.core.windows.net" }
                .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed partial class CachingAzdoApiClient
{
    public async Task<IReadOnlyList<AzdoBuildArtifact>> RefreshBuildArtifactsAsync(string org, string project,
        int buildId, CancellationToken ct = default)
    {
        if (_options.EvalMode) return await GetBuildArtifactsAsync(org, project, buildId, ct);
        await EnsureAuthTokenHashAsync(ct);
        var artifacts = await _inner.RefreshBuildArtifactsAsync(org, project, buildId, ct);
        if (_enabled)
            await _cache.SetMetadataAsync(BuildCacheKey(org, project, $"artifacts:{buildId}"),
                JsonSerializer.Serialize(artifacts), InProgressTtl, ct);
        return artifacts;
    }

    public async Task<IReadOnlyList<AzdoTestAttachment>> GetRunTestAttachmentsAsync(
        string org, string project, int runId, int top = 50, CancellationToken ct = default)
    {
        await EnsureAuthTokenHashAsync(ct);
        var key = BuildCacheKey(org, project, $"testrunattachments:v1:{runId}:all");
        if (_enabled)
        {
            var json = await _cache.GetMetadataAsync(key, ct);
            if (json is not null)
                return JsonSerializer.Deserialize<List<AzdoTestAttachment>>(json)!.Take(top).ToArray();
            if (_options.EvalMode) await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);
        }
        var result = await _inner.GetRunTestAttachmentsAsync(org, project, runId, int.MaxValue, ct);
        if (_enabled) await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), TestTtl, ct);
        return result.Take(top).ToArray();
    }

    public Task<AzdoByteStream> OpenArtifactAsync(string org, string project, int buildId,
        AzdoBuildArtifact artifact, string? memberPath = null, CancellationToken ct = default)
        => _inner.OpenArtifactAsync(org, project, buildId, artifact, memberPath, ct);

    public Task<AzdoByteStream> OpenTestAttachmentAsync(string org, string project, int runId,
        int? resultId, int attachmentId, CancellationToken ct = default)
        => _inner.OpenTestAttachmentAsync(org, project, runId, resultId, attachmentId, ct);
}
