using System.Net;
using HelixTool.Core.Acquisition;
using Microsoft.DotNet.Helix.Client;

namespace HelixTool.Core.Helix;

internal static class HelixAcquisition
{
    public static HlxAcquisitionException FromHttp(
        HttpRequestException ex,
        string operation,
        IReadOnlyDictionary<string, object?> resource)
    {
        if (ex.StatusCode.HasValue)
        {
            return new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                AcquisitionErrorFactory.KindFromStatus(ex.StatusCode.Value),
                "helix",
                operation,
                resource,
                $"Helix {operation} failed with HTTP {(int)ex.StatusCode.Value}: {ex.Message}",
                ex.StatusCode),
                ex);
        }

        return new HlxAcquisitionException(AcquisitionErrorFactory.Create(
            AcquisitionErrorKind.TransportError,
            "helix",
            operation,
            resource,
            $"Helix {operation} transport error: {ex.Message}"),
            ex);
    }

    public static HlxAcquisitionException FromRestApi(
        RestApiException ex,
        string operation,
        IReadOnlyDictionary<string, object?> resource)
    {
        var statusCode = ToStatusCode(ex.Response?.Status);
        var kind = statusCode.HasValue
            ? AcquisitionErrorFactory.KindFromStatus(statusCode.Value)
            : AcquisitionErrorKind.TransportError;
        var message = statusCode.HasValue
            ? $"Helix {operation} {AcquisitionErrorFactory.KindLabel(kind)} for {ResourceDescription(resource)} (HTTP {(int)statusCode.Value})."
            : $"Helix {operation} API error: {ex.Message}";

        if (kind == AcquisitionErrorKind.AccessDenied)
            message = "Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.";

        return new HlxAcquisitionException(AcquisitionErrorFactory.Create(
            kind,
            "helix",
            operation,
            resource,
            message,
            statusCode),
            ex);
    }

    /// <summary>
    /// Classifies <see cref="Azure.RequestFailedException"/> — the general Azure.Core pipeline
    /// exception that can surface instead of the Helix SDK's own <see cref="RestApiException"/>,
    /// including when the pipeline wraps a lower-level transport failure.
    /// </summary>
    public static HlxAcquisitionException FromRequestFailed(
        Azure.RequestFailedException ex,
        string operation,
        IReadOnlyDictionary<string, object?> resource)
    {
        // RequestFailedException.Status is 0 when the Azure.Core pipeline wrapped a transport
        // failure (e.g. a raw HttpRequestException from the handler) rather than an actual HTTP
        // response; the original status code survives on the inner exception in that case.
        HttpStatusCode? statusCode = ex.Status > 0
            ? (HttpStatusCode)ex.Status
            : (ex.InnerException as HttpRequestException)?.StatusCode;
        var kind = statusCode.HasValue
            ? AcquisitionErrorFactory.KindFromStatus(statusCode.Value)
            : AcquisitionErrorKind.TransportError;
        var message = statusCode.HasValue
            ? $"Helix {operation} {AcquisitionErrorFactory.KindLabel(kind)} for {ResourceDescription(resource)} (HTTP {(int)statusCode.Value})."
            : $"Helix {operation} transport error: {ex.Message}";

        if (kind == AcquisitionErrorKind.AccessDenied)
            message = "Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.";

        return new HlxAcquisitionException(AcquisitionErrorFactory.Create(
            kind,
            "helix",
            operation,
            resource,
            message,
            statusCode),
            ex);
    }

    public static HlxAcquisitionException Timeout(
        Exception ex,
        string operation,
        IReadOnlyDictionary<string, object?> resource)
        => new(AcquisitionErrorFactory.Create(
            AcquisitionErrorKind.Timeout,
            "helix",
            operation,
            resource,
            $"Helix {operation} timed out."),
            ex);

    public static HlxAcquisitionException Transport(
        Exception ex,
        string operation,
        IReadOnlyDictionary<string, object?> resource)
        => new(AcquisitionErrorFactory.Create(
            AcquisitionErrorKind.TransportError,
            "helix",
            operation,
            resource,
            $"Helix {operation} stream transport error: {ex.Message}"),
            ex);

    public static IReadOnlyDictionary<string, object?> Resource(params (string Name, object? Value)[] values)
    {
        var resource = new Dictionary<string, object?>();
        foreach (var (name, value) in values)
            resource[name] = value;
        return resource;
    }

    private static HttpStatusCode? ToStatusCode(int? status)
        => status.HasValue ? (HttpStatusCode)status.Value : null;

    private static string ResourceDescription(IReadOnlyDictionary<string, object?> resource)
        => string.Join(", ", AcquisitionRedaction.RedactResource(resource).Select(kvp => $"{kvp.Key}={kvp.Value}"));
}
