# Decision drop: azdo_auth_status is not sync-safe

**Date:** 2026-05-21T11:27:27-05:00  
**Author:** Ripley

## Context
Ash's MCP exception follow-up list treated `azdo_auth_status` as a possible trivial sync conversion if it only read cached/local state like `helix_auth_status`.

## Finding
- `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` delegates `azdo_auth_status` to `IAzdoTokenAccessor.AuthStatusAsync()`.
- `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs` shows `AzCliAzdoTokenAccessor.AuthStatusAsync()` awaiting `_resolutionLock.WaitAsync(...)` and, on cache miss, `ResolveFallbackCredentialAsync(...)`.
- That fallback path probes `AzureCliCredential.GetTokenAsync(...)` and then `az account get-access-token`, so the call can perform real credential I/O and subprocess work before returning status.

## Implication
- Do **not** convert `azdo_auth_status` to a synchronous MCP method in the current shape.
- If parity with `helix_auth_status` is still desired later, add a separate non-probing cached snapshot API first, then switch the tool to that surface.
