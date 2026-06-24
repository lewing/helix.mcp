using HelixTool.Core;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Core.AzDO;
using HelixTool.Mcp;
using HelixTool.Mcp.Tools;
using ModelContextProtocol.Server;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

HelixToolUserAgent.Initialize(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

var builder = WebApplication.CreateBuilder(args);

// Named HttpClients via IHttpClientFactory — avoids socket exhaustion, enables timeout config
builder.Services.AddHttpClient("HelixDownload", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    HelixToolUserAgent.Apply(c);
});
builder.Services.AddHttpClient("AzDO", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    HelixToolUserAgent.Apply(c);
});

// HttpContext accessor for per-request token resolution
builder.Services.AddHttpContextAccessor();

// Token accessor reads from Authorization header, falls back to env var
builder.Services.AddScoped<IHelixTokenAccessor, HttpContextHelixTokenAccessor>();

// Factories are singleton — create instances on demand
builder.Services.AddSingleton<IHelixApiClientFactory, HelixApiClientFactory>();
builder.Services.AddSingleton<ICacheStoreFactory, CacheStoreFactory>();

// CacheOptions is scoped — computed per-request from token accessor
builder.Services.AddScoped<CacheOptions>(sp =>
{
    var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
    var opts = new CacheOptions
    {
        CacheRootHash = CacheOptions.ComputeTokenHash(token)
    };
    var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
    if (int.TryParse(maxStr, out var mb))
        opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
    return opts;
});

// ICacheStore is scoped — resolved via factory for auth-context isolation
builder.Services.AddScoped<ICacheStore>(sp =>
{
    var factory = sp.GetRequiredService<ICacheStoreFactory>();
    var options = sp.GetRequiredService<CacheOptions>();
    return factory.GetOrCreate(options);
});

// IHelixApiClient is scoped — per-request with per-client token, wrapped with caching
builder.Services.AddScoped<IHelixApiClient>(sp =>
{
    var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
    var clientFactory = sp.GetRequiredService<IHelixApiClientFactory>();
    var raw = clientFactory.Create(token);
    var cache = sp.GetRequiredService<ICacheStore>();
    var options = sp.GetRequiredService<CacheOptions>();
    return new CachingHelixApiClient(raw, cache, options);
});

// HelixService is scoped — follows its scoped dependencies
builder.Services.AddScoped<HelixService>(sp =>
    new HelixService(
        sp.GetRequiredService<IHelixApiClient>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("HelixDownload")));

// AzDO services — singleton token accessor, scoped API client with caching decorator
builder.Services.AddSingleton<IAzdoTokenAccessor, AzCliAzdoTokenAccessor>();
builder.Services.AddScoped<AzdoApiClient>(sp =>
    new AzdoApiClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("AzDO"),
        sp.GetRequiredService<IAzdoTokenAccessor>(),
        sp.GetRequiredService<CacheOptions>()));
builder.Services.AddScoped<IAzdoApiClient>(sp =>
    new CachingAzdoApiClient(
        sp.GetRequiredService<AzdoApiClient>(),
        sp.GetRequiredService<ICacheStore>(),
        sp.GetRequiredService<CacheOptions>(),
        sp.GetRequiredService<IAzdoTokenAccessor>()));
builder.Services.AddScoped<AzdoService>();

builder.Services
    .AddMcpServer(options =>
    {
        var serverVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        options.ServerInfo = new() { Name = "hlx", Version = serverVersion };

        options.AddBindingErrorFilter();
        // Stage B: did-you-mean filter — runs after alias normalization, before SDK dispatch.
        // Intercepts unknown params with structured McpException + Levenshtein hints.
        // Stage A's UnmappedMemberHandling.Disallow (below) remains as defense-in-depth.
        options.AddUnknownParameterFilter(typeof(HelixMcpTools).Assembly);
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(HelixMcpTools).Assembly, new JsonSerializerOptions
    {
        // Reject unknown parameters at binding time so callers get a structured error
        // instead of silent data loss. The AddBindingErrorFilter above catches the resulting
        // ArgumentException(paramName:"arguments") and wraps it as McpException.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // Required: SDK calls MakeReadOnly() on options before schema gen; without a
        // TypeInfoResolver set, CreateJsonSchemaCore tries to assign one post-lock → InvalidOperationException.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    })
    .WithResourcesFromAssembly(typeof(HelixMcpTools).Assembly);

var app = builder.Build();
app.UseApiKeyAuthIfConfigured();
app.MapMcp();
app.Run();
