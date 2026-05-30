using System.Net.Http.Headers;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.DotNet.Helix.Client;

namespace HelixTool.Core;

public static class HelixToolUserAgent
{
    public const string ToolName = "helix.mcp";
    public const string ToolHeaderName = "X-Helix-Mcp-Tool";
    public const string ToolHeaderValue = ToolName;

    public static string ProductIdentifier { get; private set; } = $"{ToolName}/0.0.0";

    public static void Initialize(string version)
    {
        ProductIdentifier = $"{ToolName}/{version}";
    }

    public static void Apply(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!client.DefaultRequestHeaders.UserAgent.Any(IsToolProduct))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductIdentifier);

        if (!client.DefaultRequestHeaders.Contains(ToolHeaderName))
            client.DefaultRequestHeaders.Add(ToolHeaderName, ToolHeaderValue);
    }

    public static void Apply(HelixApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(new HeaderPolicy(), HttpPipelinePosition.PerCall);
    }

    private static bool IsToolProduct(ProductInfoHeaderValue value)
        => string.Equals(value.Product?.Name, ToolName, StringComparison.OrdinalIgnoreCase);

    private static string AppendProductIdentifier(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
            return ProductIdentifier;

        return existing.Contains(ToolName + "/", StringComparison.OrdinalIgnoreCase)
            ? existing
            : existing + " " + ProductIdentifier;
    }

    private sealed class HeaderPolicy : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Headers.TryGetValue("User-Agent", out var existingUserAgent))
                message.Request.Headers.SetValue("User-Agent", AppendProductIdentifier(existingUserAgent));
            else
                message.Request.Headers.SetValue("User-Agent", ProductIdentifier);

            message.Request.Headers.SetValue(ToolHeaderName, ToolHeaderValue);
        }
    }
}
