# Decision: helix.mcp outbound traffic identifier

**Date:** 2026-05-29T20:12:39-05:00  
**Author:** Ripley  
**Status:** Proposed implementation

## Context

arcade-services request logs could not distinguish helix.mcp traffic from other Helix SDK consumers because this tool sent no product-specific identifier. AzDO traffic also only added auth headers.

## Decision

Add one shared `HelixToolUserAgent` helper in `HelixTool.Core` and apply it to every outbound HTTP surface owned by this repo:

- named `AzDO` `HttpClient`
- named `HelixDownload` `HttpClient`
- Helix SDK calls via `HelixApiOptions.AddPolicy(...)`

The identifier is `User-Agent: helix.mcp/{version}` plus `X-Helix-Mcp-Tool: helix.mcp`.

## Consequences

arcade-services can filter logs by either standard User-Agent product or the explicit tool header. The Helix SDK path is covered because `HelixApiOptions` exposes an Azure.Core pipeline policy hook.
