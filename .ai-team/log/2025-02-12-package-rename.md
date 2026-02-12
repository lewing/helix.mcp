# Session: 2025-02-12-package-rename

**Requested by:** Larry Ewing

## Changes

- **Ripley:** Renamed PackageId from `hlx` to `lewing.helix.mcp` in HelixTool.csproj and `.mcp/server.json`, following baronfel's `baronfel.binlog.mcp` naming convention. Updated server.json to 2025-10-17 schema format. Added PackageTags, PackageReadmeFile, PublishRepositoryUrl. ToolCommandName (`hlx`) unchanged.
- **Kane:** Updated all README.md references from `dnx hlx` to `dnx lewing.helix.mcp` across Installation, MCP Configuration, and Authentication sections.

## Verification

- Build: 0 errors, 0 warnings
- Tests: 126/126 passed
