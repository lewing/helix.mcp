### 2026-03-14: `azdo search-log --schema` is mode-sensitive
**By:** Ripley
**What:** The new CLI `--schema` support prints `LogSearchResult` when `hlx azdo search-log` is scoped to a specific `--log-id`, and prints `CrossStepSearchResult` when the command is searching across ranked build logs.
**Why:** `azdo search-log` already has two distinct JSON payload shapes depending on whether `--log-id` is present. Schema discovery needs to mirror the active wire format instead of inventing a third shape or flattening both modes into one inaccurate contract.
