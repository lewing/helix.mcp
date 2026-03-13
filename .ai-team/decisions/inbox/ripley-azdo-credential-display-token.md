# Ripley decision: AzdoCredential keeps a display token separate from wire token

## Context
`IAzdoTokenAccessor` now returns structured auth metadata so AzDO requests can switch between Bearer and Basic auth. That change would have broken a large body of existing string-shaped tests and mocks immediately, especially around `AZDO_TOKEN` PAT handling where the wire value must be base64-encoded.

## Decision
Keep `AzdoCredential.Token` as the actual header payload sent on the wire, but add `AzdoCredential.DisplayToken` for compatibility-oriented string conversions and test assertions.

## Why
- PAT auth needs the accessor to pre-encode `:{pat}` for the Basic header, so `Token` can no longer double as the human-readable value.
- Existing callers/tests still compare returned credentials as strings; `DisplayToken` lets them keep seeing the original env/CLI token text without changing request behavior.
- Error messages still use `Source`, not either token field, so no secret values are surfaced.

## Files
- `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs`
- `src/HelixTool.Core/AzDO/AzdoApiClient.cs`
