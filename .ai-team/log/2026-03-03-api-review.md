# 2026-03-03: API Review Session

**Requested by:** Larry Ewing

## Participants
- **Dallas** — API surface review
- **Ash** — Consumer experience review

## Summary

### Dallas: HelixTool.Core API Surface Review
Conducted a comprehensive pre-publish API surface review of `lewing.helix.core` covering all public types, naming, type design, method signatures, dependencies, abstractions, breaking change risk, and XML documentation.

**3 must-fix items (blockers):**
1. **Seal `HelixService` and `HelixException`** — unsealed public types not designed for inheritance create a breaking change trap
2. **Change `List<T>` to `IReadOnlyList<T>`** in all record types — mutable collections in "immutable" records violates the design contract
3. **Make `MatchesPattern` internal** — implementation detail leaking into public API

**6 non-blocking improvements:**
1. Rename `Failed`/`Passed` to `FailedItems`/`PassedItems` in `JobSummary`
2. Parse `IJobDetails.Created`/`Finished` to `DateTimeOffset?` in the adapter
3. Fill missing XML doc comments on `HelixIdResolver`, `HelixApiClient` ctor, and under-documented `HelixService` methods
4. Plan `IHelixService` interface for v1.1
5. Document that new `FailureCategory` values may be added
6. Track cache-package-split for consumers who want API-only without SQLite

### Ash: Consumer Experience Review
Conducted a pre-publish consumer experience review evaluating the library from the perspective of a .NET developer who finds it on NuGet, reads the README, and integrates it into their app.

**1 blocker:**
- README example uses `summary.FailedItems` / `summary.PassedItems` but actual properties are `summary.Failed` / `summary.Passed` — code won't compile

**7 friction items:**
1. Auth error message says "Run 'hlx login'" — CLI-specific hint in library exception
2. `nuget.config` requirement for `dotnet-eng` feed not mentioned in library section
3. `FailureCategory` enum defined in `HelixService.cs` instead of `Models/`
4. `IHelixTokenAccessor` mentioned in README but relationship to `HelixService` unclear
5. `DownloadFilesAsync` / `DownloadFromUrlAsync` temp file cleanup not documented
6. `HelixApiClient` constructor takes raw `string?` — no options pattern
7. File search disabled throws `InvalidOperationException` instead of `HelixException`

**7 opportunities:**
1. No `services.AddHelixClient()` extension method
2. No `GetJobInfoAsync` (metadata only)
3. `HelixException` doesn't expose `HttpStatusCode?`
4. No README error handling example
5. No link to model types or API reference in README
6. Records are well-designed — document them
7. Consider `IAsyncDisposable` wrapper for download results

## Decisions
Both reviews written to `.ai-team/decisions/inbox/` and merged into `decisions.md`.
