using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HelixTool.Core.Acquisition;

/// <summary>Stable classification for provider evidence-acquisition failures.</summary>
[JsonConverter(typeof(AcquisitionErrorKindJsonConverter))]
public enum AcquisitionErrorKind
{
    NotFound,
    AccessDenied,
    RateLimited,
    Timeout,
    TransportError,
    InvalidResponse,
    NotInSnapshot,
}

/// <summary>Machine-readable acquisition failure details shared by CLI and MCP surfaces.</summary>
public sealed record AcquisitionError
{
    [JsonPropertyName("kind")]
    public required AcquisitionErrorKind Kind { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("resource")]
    public IReadOnlyDictionary<string, object?> Resource { get; init; } =
        new Dictionary<string, object?>();

    [JsonPropertyName("httpStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HttpStatus { get; init; }

    [JsonPropertyName("retryAfterSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterSeconds { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("replayed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Replayed { get; init; }

    [JsonPropertyName("recordedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RecordedAt { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>Exception carrying a structured acquisition error.</summary>
public sealed class HlxAcquisitionException : Exception
{
    public HlxAcquisitionException(AcquisitionError error, Exception? inner = null)
        : base(AcquisitionRedaction.RedactSensitiveUrls(error.Message), inner)
    {
        Error = Sanitize(error);
    }

    public AcquisitionError Error { get; }

    private static AcquisitionError Sanitize(AcquisitionError error)
        => error with
        {
            Resource = AcquisitionRedaction.RedactResource(error.Resource),
            Message = AcquisitionRedaction.RedactSensitiveUrls(error.Message)
        };
}

/// <summary>Stable JSON envelope for CLI and MCP structured acquisition failures.</summary>
public sealed record AcquisitionErrorEnvelope(
    [property: JsonPropertyName("error")] AcquisitionError Error);

/// <summary>Stable JSON envelope for CLI hard failures.</summary>
public sealed record AcquisitionErrorCliEnvelope(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] AcquisitionError Error);

public static class AcquisitionJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        Converters = { new AcquisitionErrorKindJsonConverter() }
    };
}

public sealed class AcquisitionErrorKindJsonConverter : JsonConverter<AcquisitionErrorKind>
{
    public override AcquisitionErrorKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "not_found" => AcquisitionErrorKind.NotFound,
            "access_denied" => AcquisitionErrorKind.AccessDenied,
            "rate_limited" => AcquisitionErrorKind.RateLimited,
            "timeout" => AcquisitionErrorKind.Timeout,
            "transport_error" => AcquisitionErrorKind.TransportError,
            "invalid_response" => AcquisitionErrorKind.InvalidResponse,
            "not_in_snapshot" => AcquisitionErrorKind.NotInSnapshot,
            _ => throw new JsonException($"Unknown acquisition error kind '{value}'.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        AcquisitionErrorKind value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            AcquisitionErrorKind.NotFound => "not_found",
            AcquisitionErrorKind.AccessDenied => "access_denied",
            AcquisitionErrorKind.RateLimited => "rate_limited",
            AcquisitionErrorKind.Timeout => "timeout",
            AcquisitionErrorKind.TransportError => "transport_error",
            AcquisitionErrorKind.InvalidResponse => "invalid_response",
            AcquisitionErrorKind.NotInSnapshot => "not_in_snapshot",
            _ => throw new JsonException($"Unknown acquisition error kind '{value}'.")
        });
    }
}

internal static class AcquisitionErrorFactory
{
    public static AcquisitionError Create(
        AcquisitionErrorKind kind,
        string provider,
        string operation,
        IReadOnlyDictionary<string, object?> resource,
        string message,
        HttpStatusCode? httpStatus = null,
        int? retryAfterSeconds = null)
        => new()
        {
            Kind = kind,
            Provider = provider,
            Operation = operation,
            Resource = AcquisitionRedaction.RedactResource(resource),
            HttpStatus = httpStatus.HasValue ? (int)httpStatus.Value : null,
            RetryAfterSeconds = retryAfterSeconds,
            Message = AcquisitionRedaction.RedactSensitiveUrls(message),
        };

    public static AcquisitionErrorKind KindFromStatus(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AcquisitionErrorKind.AccessDenied,
            HttpStatusCode.NotFound or HttpStatusCode.NoContent => AcquisitionErrorKind.NotFound,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => AcquisitionErrorKind.Timeout,
            (HttpStatusCode)429 => AcquisitionErrorKind.RateLimited,
            _ => AcquisitionErrorKind.TransportError,
        };

    public static string KindLabel(AcquisitionErrorKind kind)
        => kind switch
        {
            AcquisitionErrorKind.NotFound => "not found",
            AcquisitionErrorKind.AccessDenied => "access denied",
            AcquisitionErrorKind.RateLimited => "rate limited",
            AcquisitionErrorKind.Timeout => "timed out",
            AcquisitionErrorKind.TransportError => "transport error",
            AcquisitionErrorKind.InvalidResponse => "invalid response",
            AcquisitionErrorKind.NotInSnapshot => "not in snapshot",
            _ => kind.ToString()
        };
}

internal static class AcquisitionRedaction
{
    private static readonly Regex s_urlRegex = new(
        "https?://[^\\s\"'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, object?> RedactResource(IReadOnlyDictionary<string, object?> resource)
    {
        var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in resource)
            redacted[key] = RedactResourceValue(value);
        return redacted;
    }

    public static object? RedactResourceValue(object? value)
        => value is string text ? RedactSensitiveUrls(text) : value;

    public static string RedactSensitiveUrls(string value)
        => s_urlRegex.Replace(value, static match =>
        {
            var text = match.Value;
            var suffixStart = text.Length;
            while (suffixStart > 0 && IsTrailingPunctuation(text[suffixStart - 1]))
                suffixStart--;

            var candidate = text[..suffixStart];
            var suffix = text[suffixStart..];
            return TryRedactUrl(candidate, out var redacted)
                ? string.Concat(redacted, suffix)
                : text;
        });

    private static bool TryRedactUrl(string value, out string redacted)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, uri.AbsolutePath)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            redacted = builder.Uri.GetLeftPart(UriPartial.Path);
            return true;
        }

        redacted = value;
        return false;
    }

    private static bool IsTrailingPunctuation(char value)
        => value is '.' or ',' or ';' or ':' or ')' or ']' or '}';
}
