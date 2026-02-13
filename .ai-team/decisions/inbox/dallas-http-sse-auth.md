### 2026-02-12: HTTP/SSE multi-client auth architecture for HelixTool.Mcp

**By:** Dallas
**Requested by:** Larry Ewing

**What:** Architectural design for supporting per-client Helix auth tokens in the HTTP/SSE MCP transport (`HelixTool.Mcp`), where the server is a long-running process serving multiple concurrent clients, each with potentially different Helix tokens.

**Why:** The current singleton `HelixApiClient` with a baked-in `HELIX_ACCESS_TOKEN` env var works for stdio (one process per client) but fails for HTTP/SSE where the server is shared. This decision documents the concrete architecture for per-session token isolation using the MCP C# SDK's built-in facilities.

---

## 1. MCP SDK Capabilities (verified against `modelcontextprotocol/csharp-sdk` main)

The SDK provides three critical mechanisms for this scenario:

### 1a. `HttpServerTransportOptions.ConfigureSessionOptions`
```csharp
Func<HttpContext, McpServerOptions, CancellationToken, Task>? ConfigureSessionOptions
```
Called once per new MCP session. Receives the `HttpContext` (including `Authorization` header) and the `McpServerOptions` for that session. This is the hook for extracting per-client tokens.

### 1b. `McpServerOptions.ScopeRequests = true` (default)
Each tool invocation creates a new `IServiceScope`. Services registered as `AddScoped<T>()` get per-request instances. This means scoped services resolve from a child scope created by the SDK per request.

### 1c. `IHttpContextAccessor` availability
When `PerSessionExecutionContext = false` (the default), tool handlers run on the HTTP request's `ExecutionContext`, so `IHttpContextAccessor` works. When `PerSessionExecutionContext = true`, it does NOT work (SDK docs explicitly warn about this).

### 1d. `ClaimsPrincipal` flow
The SDK reads `context.User` from `HttpContext` and flows it into `JsonRpcMessage.Context.User`. It also enforces session-user binding — subsequent requests to an existing session must have the same user identity claim.

## 2. Recommended Architecture

### Token Flow: HTTP Authorization Header → AsyncLocal → Scoped HelixApiClient

**Option chosen: `IHttpContextAccessor` + scoped `IHelixApiClient` factory pattern.**

This is preferred over `ConfigureSessionOptions` + `AsyncLocal<string>` because:
- It uses standard ASP.NET Core patterns (no custom session state management)
- It works with the SDK's `ScopeRequests = true` default (scoped DI per request)
- Token is available in every tool invocation without custom plumbing
- `IHttpContextAccessor` is the documented, supported approach in the SDK

### Approach

1. Client sends Helix token in HTTP `Authorization` header: `Authorization: token <helix-token>`
2. ASP.NET Core middleware (or `ConfigureSessionOptions`) extracts the token
3. Scoped `IHelixApiClient` is resolved per request, receiving the token from `IHttpContextAccessor`

## 3. Concrete Changes

### File: `src/HelixTool.Core/IHelixApiClientFactory.cs` (NEW)

```csharp
namespace HelixTool.Core;

/// <summary>
/// Factory for creating <see cref="IHelixApiClient"/> instances with a specific auth token.
/// Used in HTTP/SSE MCP transport where each client has its own token.
/// </summary>
public interface IHelixApiClientFactory
{
    IHelixApiClient Create(string? accessToken);
}

public sealed class HelixApiClientFactory : IHelixApiClientFactory
{
    public IHelixApiClient Create(string? accessToken) => new HelixApiClient(accessToken);
}
```

### File: `src/HelixTool.Core/IHelixTokenAccessor.cs` (NEW)

```csharp
namespace HelixTool.Core;

/// <summary>
/// Abstraction for resolving the current Helix access token.
/// Stdio: returns env var. HTTP: returns token from HttpContext.
/// </summary>
public interface IHelixTokenAccessor
{
    string? GetAccessToken();
}

/// <summary>Fixed token from environment variable — used by stdio transport and CLI.</summary>
public sealed class EnvironmentHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly string? _token;
    public EnvironmentHelixTokenAccessor(string? token) => _token = token;
    public string? GetAccessToken() => _token;
}
```

### File: `src/HelixTool.Mcp/HttpContextHelixTokenAccessor.cs` (NEW)

```csharp
using HelixTool.Core;
using Microsoft.AspNetCore.Http;

namespace HelixTool.Mcp;

/// <summary>
/// Extracts Helix token from the HTTP Authorization header.
/// Expected format: "token {value}" (matching Helix SDK convention).
/// Falls back to HELIX_ACCESS_TOKEN env var if no header present.
/// </summary>
public sealed class HttpContextHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string? _fallbackToken;

    public HttpContextHelixTokenAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        _fallbackToken = Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN");
    }

    public string? GetAccessToken()
    {
        var authHeader = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            // "token xxx" or "Bearer xxx"
            var spaceIdx = authHeader.IndexOf(' ');
            if (spaceIdx > 0)
                return authHeader[(spaceIdx + 1)..];
            return authHeader;
        }
        return _fallbackToken;
    }
}
```

### File: `src/HelixTool.Mcp/Program.cs` (MODIFIED)

```csharp
using HelixTool.Core;
using HelixTool.Mcp;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// HttpContext accessor for per-request token resolution
builder.Services.AddHttpContextAccessor();

// Token accessor reads from Authorization header, falls back to env var
builder.Services.AddScoped<IHelixTokenAccessor, HttpContextHelixTokenAccessor>();

// Factory is singleton — creates HelixApiClient instances on demand
builder.Services.AddSingleton<IHelixApiClientFactory, HelixApiClientFactory>();

// IHelixApiClient is SCOPED — one per request, using the per-request token
builder.Services.AddScoped<IHelixApiClient>(sp =>
{
    var tokenAccessor = sp.GetRequiredService<IHelixTokenAccessor>();
    var factory = sp.GetRequiredService<IHelixApiClientFactory>();
    var token = tokenAccessor.GetAccessToken();
    var client = factory.Create(token);

    // Wrap with caching if enabled
    var cacheOptions = new CacheOptions
    {
        AuthTokenHash = CacheOptions.ComputeTokenHash(token)
    };
    var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
    if (int.TryParse(maxStr, out var mb))
        cacheOptions = cacheOptions with { MaxSizeBytes = (long)mb * 1024 * 1024 };

    if (cacheOptions.MaxSizeBytes <= 0) return client;

    var cacheStore = sp.GetRequiredService<ICacheStoreFactory>().GetOrCreate(cacheOptions);
    return new CachingHelixApiClient(client, cacheStore, cacheOptions);
});

// HelixService is SCOPED (depends on scoped IHelixApiClient)
builder.Services.AddScoped<HelixService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "hlx", Version = "1.0.0" };
        // ScopeRequests = true is the default — each tool call gets a new scope
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(HelixMcpTools).Assembly);

var app = builder.Build();
app.MapMcp();
app.Run();
```

### File: `src/HelixTool.Core/Cache/ICacheStoreFactory.cs` (NEW)

```csharp
namespace HelixTool.Core;

/// <summary>
/// Factory for obtaining cache stores by auth context.
/// Needed for HTTP mode where multiple auth contexts coexist in one process.
/// Thread-safe: concurrent requests with the same token share the same cache store.
/// </summary>
public interface ICacheStoreFactory
{
    ICacheStore GetOrCreate(CacheOptions options);
}
```

The cache store factory is needed because `SqliteCacheStore` holds an open SQLite connection and should be reused across requests with the same token hash, not recreated per scope. A `ConcurrentDictionary<string, ICacheStore>` keyed by `AuthTokenHash ?? "public"` is the implementation strategy.

### File: `src/HelixTool/Program.cs` (CLI — MINIMAL CHANGES)

The CLI/stdio path stays largely unchanged. Register `IHelixTokenAccessor` as `EnvironmentHelixTokenAccessor` and keep `IHelixApiClient` as singleton:

```csharp
services.AddSingleton<IHelixTokenAccessor>(_ =>
    new EnvironmentHelixTokenAccessor(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")));
services.AddSingleton<IHelixApiClient>(sp => ...); // unchanged — singleton is correct for stdio
```

## 4. DI Lifetime Summary

| Service | CLI/stdio | HTTP/SSE | Reason |
|---------|-----------|----------|--------|
| `IHelixTokenAccessor` | Singleton (`EnvironmentHelixTokenAccessor`) | Scoped (`HttpContextHelixTokenAccessor`) | CLI: one token per process. HTTP: one token per request context. |
| `IHelixApiClient` | Singleton | **Scoped** | CLI: one client per process. HTTP: per-request with per-client token. |
| `HelixService` | Singleton | **Scoped** | Depends on `IHelixApiClient`. |
| `ICacheStore` | Singleton | **Singleton** (via factory) | Cache stores are thread-safe, shared across requests with same token hash. |
| `ICacheStoreFactory` | Not needed | Singleton | Maps token hash → shared cache store instance. |
| `CacheOptions` | Singleton | Computed per scope | CLI: one config. HTTP: per-request based on token. |

## 5. Cache Interaction Analysis

### Existing design is correct
- Cache isolation by token hash (`CacheOptions.AuthTokenHash` → separate SQLite DBs) already works.
- Two clients with different tokens get independent cache stores/DBs — no cross-contamination.
- Two clients with the SAME token share the same cache store — this is correct and desired (cache hit efficiency).

### Concurrent access safety
- `SqliteCacheStore` uses SQLite WAL mode + `busy_timeout=5000` — already designed for cross-process concurrency.
- Within a single process, concurrent requests hitting the same `SqliteCacheStore` instance are safe because SQLite WAL handles concurrent reads, and writes are serialized by SQLite's internal locking.
- **One concern:** `SqliteCacheStore` currently uses a single `SqliteConnection` field. For concurrent in-process access, this should be changed to connection-per-operation (open/close from a connection pool) or protected with a lock. This is a pre-existing issue that becomes acute under HTTP load.

### Action item
- **R-HTTP-CACHE-1:** `SqliteCacheStore` should use connection pooling (`Data Source={path};Cache=Shared`) or `SemaphoreSlim` for write serialization. Current single-connection design is fine for stdio (sequential access) but risks corruption under concurrent HTTP requests.

## 6. Security Considerations

### Token handling in long-running process
1. **No logging:** Tokens must never appear in logs. The current code doesn't log tokens. The `IHelixTokenAccessor` pattern keeps tokens out of DI descriptions and diagnostics.
2. **Memory:** Tokens live only in `HelixApiClient._api` (Helix SDK `HelixApi` instance) and in the scoped `IHelixTokenAccessor`. Scoped objects are disposed at the end of the request scope. The `HelixApiClient` itself is scoped and gets GC'd after the request.
3. **Header extraction:** The `HttpContextHelixTokenAccessor` reads from `Authorization` header — standard HTTP pattern, protected by TLS in transit.
4. **Session binding:** The MCP SDK enforces that subsequent requests to an existing session come from the same `ClaimsPrincipal` (via `HasSameUserId` check). This prevents session hijacking where client B reuses client A's MCP session ID but with a different identity.
5. **Cache isolation:** Different tokens → different SQLite DBs → complete data separation. A client can only read cache entries from their own auth context.

### What NOT to do
- Don't store tokens in `AsyncLocal<string>` that might leak across requests via thread pool reuse. `IHttpContextAccessor` is the correct abstraction — it uses `AsyncLocal` internally but is scoped to the HTTP request pipeline.
- Don't pass tokens as MCP tool arguments (exposes tokens in tool call logs/traces).
- Don't cache tokens in a static dictionary keyed by session ID (memory leak, no expiry).

## 7. Backward Compatibility

### Zero breaking changes to `IHelixApiClient` interface
The interface is unchanged. The only difference is DI lifetime (singleton vs scoped).

### `HelixMcpTools` is unchanged
It depends on `HelixService` via constructor injection. Whether `HelixService` is singleton or scoped is invisible to the tool class.

### `HelixService` is unchanged
It depends on `IHelixApiClient` via constructor injection. Scoping is handled by DI, not by `HelixService` code.

### CLI binary (`hlx`) is unchanged
Stdio mode continues to use singleton lifetime. The `IHelixTokenAccessor` abstraction can be added opportunistically but isn't strictly required — the CLI can continue to read the env var directly at registration time.

## 8. Implementation Priority & Ordering

This is a **P2** feature. Rationale:
- No user has requested remote MCP yet
- All current consumers (Copilot CLI, ci-analysis skill, Claude Desktop, VS Code) use stdio
- The `HelixTool.Mcp` HTTP project exists but is not actively deployed
- The architecture should be designed now (this document) but implemented when HTTP/SSE demand materializes

When implemented, the ordering should be:

1. **R-HTTP-1:** Add `IHelixTokenAccessor` interface + `EnvironmentHelixTokenAccessor` to Core
2. **R-HTTP-2:** Add `IHelixApiClientFactory` to Core
3. **R-HTTP-3:** Add `ICacheStoreFactory` to Core, refactor `SqliteCacheStore` for concurrent access
4. **R-HTTP-4:** Add `HttpContextHelixTokenAccessor` to HelixTool.Mcp
5. **R-HTTP-5:** Rewire `HelixTool.Mcp/Program.cs` with scoped DI registrations
6. **R-HTTP-6:** (Optional) Rewire `HelixTool/Program.cs` CLI to use `IHelixTokenAccessor` for consistency
7. **L-HTTP-1:** Unit tests for `HttpContextHelixTokenAccessor` with mocked `IHttpContextAccessor`
8. **L-HTTP-2:** Integration test verifying two concurrent MCP sessions with different tokens get isolated results
9. **L-HTTP-3:** Test `ICacheStoreFactory` thread safety (concurrent `GetOrCreate` with same key)
10. **L-HTTP-4:** Test `SqliteCacheStore` under concurrent access

## 9. Alternatives Considered and Rejected

| Option | Verdict | Reason |
|--------|---------|--------|
| **A: `ConfigureSessionOptions` + `AsyncLocal<string>`** | Viable but fragile | Requires `PerSessionExecutionContext = true`, which breaks `IHttpContextAccessor` and ties us to SDK-specific behavior. Standard ASP.NET Core scoping is more robust. |
| **B: Token in MCP tool arguments** | Reject | Exposes secrets in tool call logs. Violates security best practices. Noisy API surface. |
| **C: Server-side config maps client identities to tokens** | Reject for now | Requires user management, identity federation. Overkill. Could layer on top later using ASP.NET Core auth middleware. |
| **D: Singleton `HelixApiClient` + `ConcurrentDictionary<token, HelixApi>`** | Reject | Unbounded memory growth. No clear eviction. Mixes transport concerns into the API client. |
| **E: Do nothing — require separate HTTP server process per token** | Reject for HTTP | Defeats the purpose of HTTP/SSE transport. Users would need N server processes for N tokens. |

## 10. Open Questions for Implementation

1. **SDK version:** We're on `0.8.0-preview.1`. The `ConfigureSessionOptions`, `ScopeRequests`, and `PerSessionExecutionContext` properties exist in the current codebase on `main`. Verify these are available in our pinned version, or upgrade.
2. **Helix SDK client lifecycle:** `HelixApi` (from `Microsoft.DotNet.Helix.Client`) — does it hold `HttpClient` instances that should be long-lived? If so, the scoped pattern should use `IHttpClientFactory` internally rather than creating new `HttpClient` per scope. Investigate `HelixApi` internals.
3. **Token format:** Helix uses `Authorization: token {value}`, not `Bearer`. The `HttpContextHelixTokenAccessor` should accept both formats for flexibility.
