# Session: 2026-02-11-p0-foundation

**Requested by:** Larry Ewing
**Date:** 2026-02-11

## Summary

Dallas facilitated a Design Review ceremony for P0 foundation work covering US-12 (DI/testability) and US-13 (error handling).

## Prior Work

Ash completed requirements extraction from session 72e659c1 — 30 user stories (US-1 through US-30) formalized in `.ai-team/requirements.md`. Included ci-analysis skill review identifying 12 additional stories (US-19 through US-30). P0 items confirmed: US-12 (DI/testability) and US-13 (error handling).

## Decisions (D1–D10)

- **D1:** `IHelixApiClient` interface — 6 methods mirroring actual SDK usage, all with `CancellationToken`
- **D2:** `HelixApiClient` default implementation — wraps `new HelixApi()`, single instantiation point
- **D3:** `HelixService` constructor-injects `IHelixApiClient` — no default constructor
- **D4:** CLI constructs `HelixService` manually with `new HelixApiClient()`
- **D5:** MCP tools become instance methods with constructor injection via DI container
- **D6:** `HelixException` — single exception type, catches `HttpRequestException` and `TaskCanceledException` only
- **D7:** Input validation — `ArgumentException.ThrowIfNullOrWhiteSpace` guards, `ResolveJobId` throws on invalid input
- **D8:** `CancellationToken cancellationToken = default` on all async methods, threaded through all awaits
- **D9:** `IHelixApiClient` is the only mock boundary; `HelixIdResolver` and `MatchesPattern` tested directly
- **D10:** `JsonSerializerOptions` hoisted to static field in `HelixMcpTools`

## Risks Identified

1. MCP SDK v0.8.0-preview.1 may not support instance tool methods — verify first
2. Helix SDK return types may be concrete — may need DTOs
3. ConsoleAppFramework DI support unknown

## Spawned Work

- **Ripley:** Implementing D1–D10 (interface, wrapper, DI refactoring, error handling, validation, CancellationToken)
- **Lambert:** Writing unit tests with NSubstitute against `IHelixApiClient` mock boundary

Ripley and Lambert spawned in parallel.
