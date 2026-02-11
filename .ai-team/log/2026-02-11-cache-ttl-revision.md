# Session: 2026-02-11-cache-ttl-revision

**Requested by:** Larry Ewing

## Summary

Dallas revised the caching strategy based on Larry's concerns about indefinite expiry for completed jobs and large in-progress artifacts. Key changes:

- Console logs for running jobs are never cached (previously 60s TTL)
- Completed jobs: 4h memory sliding expiration, 7d disk retention
- Automatic disk eviction at 500MB cap + 7-day expiry (previously manual only)
- Per-data-type TTL matrix replaces blanket 60s for running jobs (15–30s depending on data type)

Decision merged from inbox: `dallas-cache-ttl-revision.md`. Propagated update to Ripley's history.
