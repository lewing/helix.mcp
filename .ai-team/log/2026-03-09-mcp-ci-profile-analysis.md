# 2026-03-09: MCP CI Profile Analysis

**Requested by:** Larry (lewing)
**Agent:** Ash

## Summary

Analyzed 6 CI repo profiles (runtime, aspnetcore, sdk, roslyn, efcore, vmr) from `~/src/blazor-playground/copilot-skills/plugins/dnceng-knowledge/skills/ci-repo-profiles/references/` against current MCP tool descriptions in `HelixMcpTools.cs`, `AzdoMcpTools.cs`, and `HelixService.cs`.

## Findings

- **14 recommendations:** 3 P0, 6 P1, 4 P2, 1 P3
- **Key finding:** `helix_test_results` fails for 4/6 major repos (aspnetcore, sdk, roslyn, efcore) because they don't upload test result files to Helix blob storage — Arcade reporter consumes results locally and publishes to AzDO instead
- **P0s:** Improve `helix_test_results` description to steer agents away from futile TRX searches; add repo-specific pattern guidance to `helix_search_log`; improve error messages with actionable next steps
- **Output:** `.ai-team/decisions/inbox/ash-ci-profile-analysis.md`
