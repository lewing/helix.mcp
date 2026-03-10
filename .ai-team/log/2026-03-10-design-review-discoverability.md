# Design Review — Discoverability after recent CI guidance fixes

- **Date:** 2026-03-10
- **Facilitator:** Dallas
- **Participants:** Ripley, Lambert, Kane
- **Requested by:** Larry Ewing
- **Context:** Ash’s refresh identified discoverability as the next incremental opportunity, especially around `helix_test_results` fallback guidance and `helix_search_log` usage patterns.

## 1. Key decisions made

1. **Stay incremental; do not add a new composite investigation tool.**
   - We will improve discoverability through MCP tool descriptions, fallback/error messages, README/llmstxt guidance, and CI-guide wording.
   - Rationale: the current tool surface is already sufficient; the main gap is tool selection, not raw capability.

2. **Make `helix_ci_guide(repo)` the explicit entry point for repo-specific Helix investigation.**
   - Tool descriptions and fallback messages should point agents to `helix_ci_guide(repo)` early, especially before choosing search patterns or assuming Helix-hosted test results exist.
   - This is guidance-level coupling, not a new hard runtime dependency between tools.

3. **Clarify the contract of `helix_test_results`.**
   - The discoverability surface must say that the tool parses structured Helix-hosted test result files when present, including TRX and the existing xUnit XML fallback behavior, but that many repos still route structured results through AzDO instead.
   - Failure paths should recommend the next tool sequence, not just say that parsing failed.

4. **Clarify the contract of `helix_search_log`.**
   - The description should emphasize that it is the preferred remote-first console-log search tool and that search patterns vary by repo/test runner.
   - Guidance should steer agents away from full-log download first and toward repo-specific pattern selection.

5. **Synchronize all discoverability surfaces together.**
   - MCP descriptions, README, llmstxt/help text, and CI-guide output must tell the same story about when to use `helix_test_results`, when to pivot to AzDO structured results, and when to use `helix_search_log`.

These decisions are copied to the decisions inbox file at `.ai-team/decisions/inbox/dallas-design-review-discoverability.md`.

## 2. Agreed interfaces and contracts

### `helix_test_results`
- Keep the existing tool signature; no new parameters.
- Improve the description so the consumer understands:
  - it is for **structured test-result parsing from Helix-hosted files**,
  - it supports the existing **TRX + xUnit XML fallback behavior**,
  - it is **not universally applicable across repos**,
  - and the failure path should route the caller toward the correct next step.
- Improve the failure message contract so it recommends:
  - `azdo_test_runs` + `azdo_test_results` for structured results when Helix files are absent or the repo typically publishes to AzDO,
  - `helix_search_log` for console-log-driven investigation,
  - and `helix_ci_guide(repo)` for repo-specific patterns and workflow choice.

### `helix_search_log`
- Keep the existing tool signature; no new parameters.
- Improve the description so the consumer understands:
  - it is the **preferred remote-first search tool** for Helix console logs,
  - pattern choice is **repo/test-runner specific**,
  - and `helix_ci_guide(repo)` is the right place to get those patterns.

### `helix_ci_guide`
- Keep the existing role as the repo-specific guidance source.
- Improve wording/order so “skip `helix_test_results` for repos where it is not the right path” is easier to discover.
- No new tool required; use this as the coordination/documentation surface.

### Docs/help surfaces
- README and `Program.cs` llmstxt/help output should mirror the same routing guidance.
- The contract is consistency, not extra depth: a short investigation-path explanation is enough.

## 3. Risks and concerns raised

1. **False confidence from vague wording**
   - If descriptions say “most repos” or “may fail” without routing guidance, agents will still try the wrong tool and interpret the failure as a product bug rather than a workflow mismatch.

2. **Description bloat**
   - Tool descriptions are a primary selection surface, but they cannot become mini-manuals. We need short “use X when / if not, use Y” wording rather than long prose.

3. **Stale repo-specific guidance**
   - Repo workflows can drift. Putting stronger routing guidance into descriptions/messages increases the cost of stale CI knowledge, so tests and CI-guide upkeep matter.

4. **Behavior vs implementation leakage**
   - We must preserve the existing architecture rule: descriptions expose behavioral contracts, not storage or parsing mechanics beyond what the consumer needs to choose the tool correctly.

5. **Sync drift across surfaces**
   - Updating only MCP descriptions or only README/llmstxt will recreate the same discoverability gap in a different place.

## 4. Disagreements and resolution

- **Question:** Should we add a new one-shot investigation selector/composite tool?
  - **Ripley:** raised it as a possible option.
  - **Dallas decision:** reject for now.
  - **Resolution:** stay incremental; improve descriptions, fallback messages, CI-guide wording, and docs instead.

- **Question:** How much format detail belongs in the `helix_test_results` description?
  - **Ripley:** wanted explicit TRX + xUnit XML mention for discoverability.
  - **Kane:** cautioned against over-explaining implementation details.
  - **Resolution:** include only the amount of format detail needed for correct tool selection and expectation-setting; avoid deeper implementation language.

- **Question:** Should `helix_ci_guide(repo)` be mandatory-first or recommended-first?
  - **Kane/Ripley:** favored making it more prominent.
  - **Resolution:** recommended-first in wording, not a hard rule. Tool flows must still work without a prior CI-guide call.

## 5. Action items

### Ripley (backend/dev)
1. Tighten `helix_test_results` and `helix_search_log` MCP descriptions to make selection/fallback clearer without changing signatures.
2. Improve the `helix_test_results` failure message so it routes callers toward:
   - AzDO structured results when Helix-hosted structured results are absent/not expected,
   - `helix_search_log` when console-log investigation is the right next step,
   - `helix_ci_guide(repo)` for repo-specific guidance.
3. Review `helix_ci_guide` wording/order so “skip this tool for these repos” is clearer where appropriate.
4. Sync the short investigation-path guidance in README and `Program.cs` llmstxt/help output.

### Lambert (tester)
1. Add/strengthen tests that validate fallback guidance text and CI-guide wording, especially around:
   - `helix_test_results` error-message routing,
   - `helix_search_log` guidance clarity,
   - and repo-specific CI-guide output ordering.
2. Add focused assertions that guard against regressions in wording that would reintroduce false-confidence behavior.

### Kane (docs)
1. Update README wording to include a short failure-investigation path that aligns with the MCP descriptions.
2. Review wording across README and llmstxt/help output so all surfaces stay synchronized and concise.

## 6. Ceremony summary

The team agrees the next increment is a **discoverability pass, not a feature pass**. The architecture stays the same: improve existing descriptions, fallback messages, CI-guide wording, and docs so agents can pick the right investigation workflow quickly and reliably.
