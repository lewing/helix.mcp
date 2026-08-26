using HelixTool.Core;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Core.AzDO;
using HelixTool.Mcp;
using HelixTool.Mcp.Tools;
using ModelContextProtocol.AspNetCore;
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

var mcpEvalSnapshotDir = Environment.GetEnvironmentVariable("HLX_EVAL_SNAPSHOT");
if (!string.IsNullOrEmpty(mcpEvalSnapshotDir))
{
    var resolvedSnapshot = Path.GetFullPath(mcpEvalSnapshotDir);
    var evalOptions = new CacheOptions
    {
        CacheRoot = resolvedSnapshot,
        EvalMode = true,
        CacheRootHash = null,
        AuthTokenHash = null,
    };
    // In eval mode all scoped services use the fixed eval options and offline stubs.
    builder.Services.AddScoped<CacheOptions>(_ => evalOptions);
    builder.Services.AddScoped<ICacheStore>(_ => new SqliteCacheStore(evalOptions));
    builder.Services.AddScoped<IHelixApiClient>(sp =>
        new CachingHelixApiClient(new OfflineHelixApiClient(), sp.GetRequiredService<ICacheStore>(), evalOptions));
    builder.Services.AddScoped<IAzdoApiClient>(sp =>
        new CachingAzdoApiClient(new OfflineAzdoApiClient(), sp.GetRequiredService<ICacheStore>(), evalOptions));
    // Block every direct HTTP download — OfflineHelixApiClient prevents SDK calls but
    // HelixService._httpClient is a separate code path that must also be sealed.
    builder.Services.AddScoped<HelixService>(sp =>
        new HelixService(
            sp.GetRequiredService<IHelixApiClient>(),
            new HttpClient(new EvalModeBlockingHandler())));
}
else
{
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
// HelixService in normal mode — real HelixDownload HttpClient
builder.Services.AddScoped<HelixService>(sp =>
    new HelixService(
        sp.GetRequiredService<IHelixApiClient>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("HelixDownload")));
}

// Inject IHelixApiClient so GetHelixJobsAsync can use the canonical Helix-side Job.ListAsync(source) path (#92)
builder.Services.AddScoped<AzdoService>(sp =>
new AzdoService(
    sp.GetRequiredService<IAzdoApiClient>(),
    sp.GetRequiredService<IHelixApiClient>()));

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
    .WithHttpTransport(options =>
    {
        // Explicit, not inherited: SDK 2.x flipped this default (stateful in 1.4.0).
        // Our DI is per-request scoped and we issue no server-to-client requests, so
        // sessions buy us nothing. Escape hatch if a down-level client ever needs one:
        // HttpServerSessionMode.StatefulForInitializeClients (not `Stateless = false`,
        // which selects Stateful and refuses current clients with -32022).
        options.SessionMode = HttpServerSessionMode.Stateless;
    })
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

// Test seam (ASP.NET Core minimal-API convention): top-level statements compile to an
// internal `Program` class, which WebApplicationFactory<TEntryPoint> cannot reference from
// another assembly. This partial declaration only widens that generated class's accessibility
// — it adds no members and changes no behavior — so integration tests can boot this exact
// host and assert against its real service registrations (see HttpTransportSessionModeTests).
public partial class Program;
