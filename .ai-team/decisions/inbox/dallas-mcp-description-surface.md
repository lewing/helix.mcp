### 2025-07-23: MCP tool descriptions should expose behavioral contracts, not implementation mechanics

**By:** Dallas
**What:** MCP `[Description]` attributes should describe *what the agent gets* (behavioral contracts), not *how the tool achieves it* (implementation details). Specifically: do NOT add phrases like "parses TRX locally", "results are cached", or "searches without downloading" to tool descriptions. These are implementation details that do not change how a consuming agent selects or invokes a tool.

**Why:**

The question is: does knowing the implementation mechanism change an LLM agent's decision-making? The answer is no for almost every case, and the few exceptions are already handled by describing *behavior*, not *mechanism*.

**Analysis by category:**

1. **Caching ("results are cached")** — An agent should never care whether results are cached. It doesn't change the call signature, the return shape, or when to call the tool. If caching affected correctness (stale data), the right fix would be a `noCache` parameter, not a description warning. Currently our cache is transparent and correct. Verdict: **implementation detail, omit.**

2. **TRX parsing ("parses TRX locally")** — The agent cares that `hlx_test_results` returns *structured test results with names, outcomes, durations, and error messages*. That's already in the description. Whether we parse XML locally or call a hypothetical Helix "parse TRX" API is irrelevant to the consumer. The description already says what you get. Verdict: **implementation detail, omit.**

3. **Remote search ("searches without downloading")** — This is the closest case to being behavior-relevant, because it implies "this is fast and doesn't leave files on disk." But the agent doesn't manage disk space or care about temp files. The behavioral contract is already correct: `hlx_search_file` says "Returns matching lines with optional context" and `hlx_search_log` says "Returns matching lines with optional context." That tells the agent exactly what it gets. Verdict: **implementation detail, omit.**

4. **Failure classification ("classifies failures locally")** — The agent cares that `hlx_status` returns `failureCategory` in the response. It doesn't care whether we computed that classification or the Helix API returned it. The current description says "failed items (with exit codes, state, duration, machine)" — this should include `failureCategory` in the description since it's a field in the response. But that's a *completeness* fix, not an "implementation detail" disclosure. Verdict: **fix the field list, don't mention local processing.**

5. **URL resolution ("accepts full Helix URLs")** — This IS behavior-relevant and IS already in tool descriptions. Every tool says "Helix job ID (GUID), Helix job URL, or full work item URL." This tells the agent what inputs are accepted. Correct and sufficient.

**The principle:** Tool descriptions are an API contract for *consumers*. They answer three questions:
- What does this tool do? (purpose)
- What inputs does it accept? (parameters)  
- What does it return? (output shape)

They do NOT answer:
- How does this tool work internally? (implementation)
- What makes this tool fast/efficient? (optimization)
- Where does processing happen? (architecture)

**The README vs. Description distinction:** The README is for *humans evaluating whether to adopt hlx*. They care about value-adds, architecture, and implementation quality. Tool descriptions are for *LLM agents selecting and invoking tools at runtime*. They care about capabilities, parameters, and return shapes. These are different audiences with different information needs.

**One exception — behavioral implications:** If an implementation detail creates a behavioral contract the agent must respect, it belongs in the description. Example: if we added a `noCache` parameter, the description should say "Bypass cache and fetch fresh data" because that changes invocation behavior. But "this is cached" with no opt-out is invisible to the consumer.

**Action items:**
- Do NOT modify any existing `[Description]` attributes to add implementation details
- DO ensure descriptions accurately list all response fields (the `failureCategory` omission in `hlx_status` is a minor gap)
- The README's "How hlx Enhances the Helix API" section is the correct home for implementation detail documentation
- This principle applies to all future MCP tools added to the project
