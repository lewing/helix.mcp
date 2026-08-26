namespace HelixTool.Core;

/// <summary>
/// HTTP message handler that blocks all network requests in eval/snapshot mode.
/// Registered through DI when <c>HLX_EVAL_SNAPSHOT</c> is set so that no direct HTTP
/// download can reach the network, regardless of which caller holds the HttpClient.
/// </summary>
public sealed class EvalModeBlockingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            $"Network access is blocked in eval/snapshot mode. " +
            $"Direct HTTP requests (target: {request.RequestUri?.Host ?? "<unknown>"}) are not permitted. " +
            $"Only data already present in the snapshot cache can be served.");
}
