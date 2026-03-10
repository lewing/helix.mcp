### 2026-03-10: Keep the strict HelixService HttpClient requirement
**By:** Lambert
**What:** Validated the removal of `HelixService`'s implicit `new HttpClient()` fallback and found no remaining one-argument construction sites in repo code or tests. Existing DI wiring in `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` already supplies named `HelixDownload` clients, and constructor null-guard coverage exists in `src/HelixTool.Tests/Helix/HelixServiceDITests.cs`.
**Why:** This keeps the service aligned with explicit dependency injection and avoids silently bypassing configured HTTP policies. Focused tests plus the full suite passed, so there is no current production follow-up required for the fallback removal.
