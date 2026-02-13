using HelixTool.Core;
using HelixTool.Mcp;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

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
        AuthTokenHash = CacheOptions.ComputeTokenHash(token)
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
builder.Services.AddScoped<HelixService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "hlx", Version = "0.1.2" };
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(HelixMcpTools).Assembly);

var app = builder.Build();
app.UseApiKeyAuthIfConfigured();
app.MapMcp();
app.Run();
