## API Surface Review — Implementation Complete

**From:** Ripley (Backend Dev)
**For:** Dallas (Architect), Lambert (Test Dev)

All 7 items from Dallas's API surface review have been implemented and compile cleanly.

### Changes Made
1. Sealed `HelixService` and `HelixException`
2. `List<T>` → `IReadOnlyList<T>` in all 8 record types (+ `LogMatch.Context`)
3. `MatchesPattern` made `internal` (added `InternalsVisibleTo` for `HelixTool` and `HelixTool.Mcp.Tools`)
4. Renamed `JobSummary.Failed` → `FailedItems`, `.Passed` → `PassedItems` — all consumers updated
5. Auth error message changed from CLI-specific to library-appropriate across 7 occurrences
6. XML doc comments added to `HelixIdResolver`, `HelixApiClient` ctor, `MatchesPattern`, `FindBinlogsAsync`, `GetWorkItemDetailAsync`, `GetBatchStatusAsync`
7. `FailureCategory` enum moved to `Models/FailureCategory.cs`

### Files Changed
- `HelixTool.Core/HelixService.cs` — sealed, auth messages, FailureCategory extracted, XML docs, MatchesPattern internal
- `HelixTool.Core/HelixException.cs` — sealed
- `HelixTool.Core/HelixApiClient.cs` — XML doc on constructor
- `HelixTool.Core/HelixIdResolver.cs` — XML docs
- `HelixTool.Core/HelixTool.Core.csproj` — InternalsVisibleTo for HelixTool, HelixTool.Mcp.Tools
- `HelixTool.Core/Models/` — all 8 record files updated to IReadOnlyList<T>, new FailureCategory.cs
- `HelixTool.Mcp.Tools/HelixMcpTools.cs` — FailedItems/PassedItems, Context?.ToList()
- `HelixTool/Program.cs` — FailedItems/PassedItems
- `HelixTool.Tests/` — 5 test files updated for renamed properties

### Action Needed
- **Lambert:** Run the full test suite to confirm all tests pass with the renamed properties and type changes.
