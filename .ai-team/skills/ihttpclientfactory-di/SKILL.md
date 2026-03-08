# Skill: IHttpClientFactory with Optional Constructor Injection

**Confidence:** low
**Source:** earned

## Problem

You need to replace static or `new HttpClient()` patterns with IHttpClientFactory for proper handler lifecycle management, but the service is constructed by tests that don't use DI.

## Wrong Pattern

```csharp
// Static field — socket exhaustion, no DNS refresh
private static readonly HttpClient s_httpClient = new();

// Or: required parameter breaks all test constructors
public MyService(IHelixApiClient api, HttpClient httpClient) { ... }
```

## Right Pattern

```csharp
// Optional parameter — production injects from factory, tests get default
public MyService(IHelixApiClient api, HttpClient? httpClient = null)
{
    _api = api ?? throw new ArgumentNullException(nameof(api));
    _httpClient = httpClient ?? new HttpClient();
}
```

DI registration:
```csharp
services.AddHttpClient("MyDownload", c => c.Timeout = TimeSpan.FromMinutes(5));
services.AddSingleton<MyService>(sp =>
    new MyService(
        sp.GetRequiredService<IMyApiClient>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("MyDownload")));
```

## Why

- IHttpClientFactory manages `HttpMessageHandler` lifetime (recycles every 2 min by default)
- Avoids socket exhaustion from long-lived static HttpClient
- Named clients allow per-use-case timeout configuration
- Optional parameter preserves backward compatibility with tests
- Tests that don't exercise HTTP can pass one arg; tests that do can inject a mock handler

## Applies When

- Replacing static `HttpClient` fields in services
- Service is heavily tested with direct construction (not DI)
- Multiple HTTP use cases need different timeouts
