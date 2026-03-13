### 2026-03-13: Separate stable cache-root partitioning from mutable AzDO auth partitioning
**By:** Ripley
**What:** Added `CacheRootHash` for cache-store/root selection while keeping `AuthTokenHash` as the mutable AzDO key partition, and seeded AzDO auth hashes before cached AzDO reads.
**Why:** CLI and stdio hosts resolve the SQLite cache store before AzDO chooses a credential, so reusing one mutable property for both the cache root and AzDO key partition could make cache location reporting misleading and leave the first AzDO cache lookup unpartitioned. Splitting the concerns keeps cache roots stable while still isolating AzDO entries per credential.
