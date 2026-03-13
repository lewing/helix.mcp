### 2026-03-13: Use stable auth-source identity for AzDO cache partitioning and refreshable fallback auth
**By:** Ripley
**What:** AzDO fallback auth now caches AzureCliCredential and `az` CLI tokens with expiry-aware refresh windows, clears that cached fallback after 401/403, and derives cache partition hashes from stable auth-source identity metadata (`auth path` + stable JWT claims) instead of hashing raw tokens.
**Why:** Raw bearer tokens rotate frequently, so hashing token bytes would churn cache partitions and still leave long-running MCP servers stuck on stale fallback credentials. Using stable source identity keeps cache keys refresh-safe while preventing one authenticated AzDO context from reusing another context's cached private responses.
