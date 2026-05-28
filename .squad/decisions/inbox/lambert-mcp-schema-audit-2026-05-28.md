# Lambert MCP Schema Audit — Required Params + Description Clarity

**Date:** 2026-05-28  
**Issue:** #67  
**Inputs:** Dallas policy Q2; Ash silent-failure investigation; local `tools/list` before/after dumps.

## Summary

- **Tools audited:** 25 `[McpServerTool]` methods across AzDO, Helix, and CI guidance.
- **Generated `required` arrays:** 25/25 match the C# signature shape (non-null/no-default parameters are required; nullable/default parameters are optional).
- **Parameter description coverage:** 25/25 tools had at least one user-visible described parameter before; reflection test now enforces every user-visible parameter has a non-empty `[Description]`.
- **Adequate AzDO/Helix disambiguation:** 10/25 before → 25/25 after.
- **Quick-win fixes shipped:** description-only changes; no production logic, type, or method-name changes.
- **Flagged follow-up:** 8 schema/signature expressiveness issues for Dallas/Ripley (not changed here).

## Key findings

1. The SDK does emit `required` correctly for simple required parameters: e.g. `helix_status.required = ["jobId"]`, `azdo_search_timeline.required = ["buildIdOrUrl", "pattern"]`.
2. The worst prevention gap was not missing `[Description]`; it was **weak family disambiguation**. AzDO `buildIdOrUrl` and Helix `jobId` both looked plausible to agents until descriptions explicitly said “not a Helix job ID” / “not an AzDO build ID”.
3. JSON numeric tokens sent to string ID parameters are a real SDK binding problem, not a misread. Local repro with `jobId: 1438863` and `buildIdOrUrl: 1438863` produced `System.Text.Json.JsonException: The JSON value could not be converted to System.String` before tool logic.
4. Several Helix tools have conditional requirements (`workItem` required unless `jobId` is a full work-item URL; `helix_download` requires either `url` or `jobId`+`workItem`). Plain generated schema cannot express those combos today.

## Matrix

| Tool | Required in tools/list | Description/disambiguation after pass | Action |
|---|---:|---|---|
| `azdo_artifacts` | `buildIdOrUrl` | OK — AzDO build ID/URL, JSON string, not Helix | fixed-in-PR |
| `azdo_auth_status` | none | OK — AzDO auth status | OK |
| `azdo_build` | `buildIdOrUrl` | OK — AzDO build details, not Helix | fixed-in-PR |
| `azdo_build_analysis` | `buildIdOrUrl` | OK — AzDO build ID/URL, not PR number | fixed-in-PR |
| `azdo_builds` | none | OK — AzDO org/project/list filters | fixed-in-PR |
| `azdo_changes` | `buildIdOrUrl` | OK — AzDO build changes, not Helix | fixed-in-PR |
| `azdo_helix_jobs` | `buildIdOrUrl` | OK — bridge from AzDO build to Helix job GUIDs | fixed-in-PR |
| `azdo_log` | `buildIdOrUrl`, `logId` | OK — AzDO build URL + AzDO log ID from timeline | fixed-in-PR |
| `azdo_search_log` | `buildIdOrUrl` | OK — AzDO build logs; pattern is substring, not regex | fixed-in-PR |
| `azdo_search_timeline` | `buildIdOrUrl`, `pattern` | OK — AzDO timeline search; `pattern` replaces guessed `jobNameRegex` | fixed-in-PR |
| `azdo_test_attachments` | `runId`, `resultId` | OK — Azure DevOps test run/result IDs | fixed-in-PR |
| `azdo_test_results` | `buildIdOrUrl`, `runId` | OK — AzDO build + AzDO test run | fixed-in-PR |
| `azdo_test_runs` | `buildIdOrUrl` | OK — AzDO test run summaries | fixed-in-PR |
| `azdo_timeline` | `buildIdOrUrl` | OK — AzDO timeline; log IDs route to AzDO log tools | fixed-in-PR |
| `helix_auth_status` | none | OK — Helix auth status | OK |
| `helix_batch_status` | `jobIds` | OK — array of Helix GUID strings/URLs, not AzDO build IDs | fixed-in-PR |
| `helix_ci_guide` | none | OK — guidance for choosing AzDO vs Helix tools | fixed-in-PR |
| `helix_download` | none | Description clearer, but schema cannot express `url` XOR `jobId`+`workItem` | flagged-for-followup |
| `helix_files` | `jobId` | Description clearer, but `workItem` is conditionally required unless URL includes it | flagged-for-followup |
| `helix_find_files` | `jobId` | OK — Helix job GUID/URL, not AzDO build ID | fixed-in-PR |
| `helix_logs` | `jobId` | Description clearer, but `workItem` is conditionally required unless URL includes it | flagged-for-followup |
| `helix_parse_uploaded_trx` | `jobId` | Description clearer, but `workItem` is conditionally required unless URL includes it | flagged-for-followup |
| `helix_search` | `jobId` | Description clearer, but `workItem` is conditionally required unless URL includes it | flagged-for-followup |
| `helix_status` | `jobId` | OK — Helix job GUID/URL, not AzDO build ID | fixed-in-PR |
| `helix_work_item` | `jobId` | Description clearer, but `workItem` is conditionally required unless URL includes it | flagged-for-followup |

## Flagged follow-up details

1. **Systemic string-ID numeric binding:** string ID params (`jobId`, `buildIdOrUrl`) reject JSON numbers. Decide whether to add custom converters/input wrappers or keep requiring quoted IDs.
2. **`helix_download`:** schema shows no required params because all are optional/defaulted, but runtime requires either `url` or `jobId` plus a work item (explicit or embedded in URL).
3. **`helix_logs`:** `workItem` is optional in schema but required unless `jobId` is a full work-item URL.
4. **`helix_files`:** same conditional `workItem` rule.
5. **`helix_work_item`:** same conditional `workItem` rule.
6. **`helix_search`:** same conditional `workItem` rule.
7. **`helix_parse_uploaded_trx`:** same conditional `workItem` rule.
8. **Conditional schemas generally:** consider JSON Schema `oneOf`/custom annotations only if the MCP SDK supports overriding generated schemas safely.

## Before/after excerpt

Before:

```text
helix_status required=['jobId'] jobId="Helix job ID (GUID), Helix URL, or full work item URL"
azdo_search_timeline required=['buildIdOrUrl','pattern'] buildIdOrUrl="AzDO build ID or full build URL" pattern="Text pattern to search for (case-insensitive)"
azdo_build_analysis required=['buildIdOrUrl'] buildIdOrUrl="AzDO build ID or full build URL"
```

After:

```text
helix_status required=['jobId'] jobId="Helix job ID as a JSON string (GUID), Helix job URL, or full Helix work item URL; not an AzDO build ID"
azdo_search_timeline required=['buildIdOrUrl','pattern'] buildIdOrUrl="AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID" pattern="Case-insensitive text substring to search for; not a regex"
azdo_build_analysis required=['buildIdOrUrl'] buildIdOrUrl="AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID"
```
