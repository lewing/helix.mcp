# Ripley PR #29 — review round 3

## Decision

Store the resolved AzDO auth cache identity on `CacheOptions` and let both `CachingAzdoApiClient` and `AzdoApiClient` update the shared auth context through `UpdateAuthContext(...)`.

## Why

The auth-hash partition can no longer be treated as write-once because long-running processes may observe Azure CLI or `az` credential changes. Keeping the last resolved identity next to the derived hash lets both layers react consistently when the principal changes, while `CacheStoreFactory` now keys stores strictly by the stable effective cache root so auth-key churn never creates duplicate `SqliteCacheStore` instances for the same database path.
