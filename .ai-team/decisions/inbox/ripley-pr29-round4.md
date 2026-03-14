# PR #29 review round 4

- When an `AzdoCredential` arrives without `CacheIdentity`, derive the fallback identity with `AzdoCredential.BuildCacheIdentity(Source, DisplayToken)` instead of using the bare source label. This preserves principal-specific AzDO cache partitioning for PATs and JWTs that do not already carry a precomputed identity.
- Use a shared `AzdoCredential.TryFromUnixTimeSeconds` helper for both JWT `exp` parsing and az CLI `expiresOn*` parsing so out-of-range Unix timestamps fail closed consistently instead of throwing.
