### 2026-02-15: README update for caching, HTTP multi-auth, and project structure
**By:** Kane
**What:** Added Caching section with settings table and policy docs; added HTTP per-request auth subsection under Authentication; expanded Project Structure to include Cache/, IHelixTokenAccessor, IHelixApiClientFactory, HttpContextHelixTokenAccessor, and 298 test count; added ci-analysis replacement note in Architecture.
**Why:** README was stale — caching, HTTP multi-auth, and new source files were undocumented. These are user-facing features (caching affects performance; HTTP auth matters for shared deployments). Kept documentation concise with tables and bold-label bullets to match existing README tone.
