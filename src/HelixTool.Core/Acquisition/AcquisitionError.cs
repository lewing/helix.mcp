using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>Exception carrying a structured acquisition error.</summary>
public sealed class HlxAcquisitionException : Exception
{
    public HlxAcquisitionException(AcquisitionError error, Exception? inner = null)
        : base(error.Message, inner)
    {
        Error = error;
    }

    public AcquisitionError Error { get; }
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
            Resource = resource,
            HttpStatus = httpStatus.HasValue ? (int)httpStatus.Value : null,
            RetryAfterSeconds = retryAfterSeconds,
            Message = message,
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
            _ => kind.ToString()
        };
}
