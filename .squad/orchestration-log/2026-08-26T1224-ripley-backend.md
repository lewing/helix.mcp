# Ripley Backend Dev — ci-evidence-reader Command Mapping & API Gaps

**Date:** 2026-08-26T12:24:41-05:00  
**Agent:** Ripley (Backend Developer)  
**Run Type:** Synchronous backend work  
**Status:** Complete

## Summary

Mapped ci-evidence-reader command semantics to hlx MCP tool interface. Identified API gaps in current tool surface and designed fixture/replay mechanics for testing credential isolation and request-scoped caching under stateless HTTP sessions.

## Key Deliverables

1. **Command Mapping:** ci-evidence-reader CLI → helix_* tool parameter/return schema correspondence
   - 7 major commands mapped
   - 3 commands require new tool parameters or separate tools
   - Backward-compatibility matrix established

2. **API Gaps Identified:**
   - Missing: work-item-specific log retrieval with custom headers
   - Missing: batch status query with credential override
   - Schema inconsistency: `maxItems` bounds enforcement

3. **Testing Infrastructure:**
   - Fixture factory design for stateless HTTP client setup
   - Replay/record mechanics for deterministic auth + cache behavior
   - Test isolation via per-request token/partition pairs

## Validation Status

- API gap fixes staged for implementation
- Fixture design reviewed and approved for integration
- Backward-compat matrix documented for PR description

## Next Steps

- Implement identified tool additions
- Add integration tests using fixture/replay mechanics
- Verify deterministic behavior under concurrent requests
