using System.Text.Json;
using HelixTool.Core.Acquisition;
using Xunit;

namespace HelixTool.Tests;

internal static class AcquisitionAssertions
{
    public static AcquisitionError Error(
        Exception exception,
        AcquisitionErrorKind kind,
        string provider,
        string operation,
        int? httpStatus = null,
        int? retryAfterSeconds = null)
    {
        var acquisition = Assert.IsType<HlxAcquisitionException>(exception);
        var error = acquisition.Error;
        Assert.Equal(kind, error.Kind);
        Assert.Equal(provider, error.Provider);
        Assert.Equal(operation, error.Operation);
        Assert.Equal(httpStatus, error.HttpStatus);
        Assert.Equal(retryAfterSeconds, error.RetryAfterSeconds);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error;
    }

    public static HlxAcquisitionException Exception(
        AcquisitionErrorKind kind,
        string provider,
        string operation,
        IReadOnlyDictionary<string, object?>? resource = null,
        int? httpStatus = null,
        int? retryAfterSeconds = null)
        => new(new AcquisitionError
        {
            Kind = kind,
            Provider = provider,
            Operation = operation,
            Resource = resource ?? new Dictionary<string, object?>(),
            HttpStatus = httpStatus,
            RetryAfterSeconds = retryAfterSeconds,
            Message = $"{provider} {operation} acquisition failed"
        });

    public static void Resource(AcquisitionError error, string key, object? expected)
    {
        Assert.True(error.Resource.TryGetValue(key, out var actual), $"Expected resource key '{key}'.");
        Assert.Equal(expected, actual);
    }

    public static string WireKind(AcquisitionErrorKind kind)
    {
        var json = JsonSerializer.Serialize(kind);
        return JsonSerializer.Deserialize<string>(json)!;
    }
}
