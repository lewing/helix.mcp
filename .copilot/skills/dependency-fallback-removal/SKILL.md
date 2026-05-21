---
name: "dependency-fallback-removal"
description: "How to safely remove implicit constructor fallbacks for injected dependencies"
domain: "testing"
confidence: "low"
source: "earned"
---

## Context
Use this when a service currently creates a default dependency internally (for example `new HttpClient()`) and you want to require explicit injection instead. The risk is not the constructor change itself; it is missing a call site or missing coverage around the new null contract.

## Patterns
- Search the full repo for constructor call sites before removing the fallback, not just the obvious test file.
- Verify production composition roots already inject the dependency explicitly before concluding the change is safe.
- Add or retain constructor tests that cover both null-guard branches after the signature becomes strict.
- Run focused tests around the touched service first, then a full suite pass to catch indirect construction paths.

## Examples
```csharp
public MyService(IMyApi api, HttpClient httpClient)
{
    _api = api ?? throw new ArgumentNullException(nameof(api));
    _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
}
```

Validation steps:
- `rg "new MyService\\(" src`
- check DI registration/composition roots
- ensure a constructor test asserts `ArgumentNullException` for the injected dependency

## Anti-Patterns
- Removing the fallback and assuming DI wiring is correct without auditing all constructor call sites.
- Updating only one test file while leaving helper tests or secondary composition roots on the old signature.
- Keeping a fallback "for convenience" when the goal is to enforce configured policies from DI.
