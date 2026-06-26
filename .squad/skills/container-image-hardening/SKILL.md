# SKILL: Container Image Hardening

**Context:** Pre-publish hardening steps for a new Dockerfile + GitHub Actions workflow. Apply before the first real image publish so the baseline is clean.

---

## Recipe 1 — Non-root user

### Why

Running as root with a world-writable home (`chmod 0777`) allows any co-tenant process to replace files in the home directory (e.g., SQLite cache). A dedicated non-root user restricts writes to that UID.

### Pattern

```dockerfile
# In the runtime stage, before WORKDIR/COPY:

# -r = system user (no password aging, no login shell — appropriate for service accounts)
# -u 1000 = stable UID; conventional first non-system user, predictable for bind-mount semantics
# -d = home dir path
# -m = create home dir owned by hlx:hlx (no extra chown/chmod needed)
RUN useradd -r -u 1000 -d /home/hlx -m hlx

ENV HOME=/home/hlx

WORKDIR /app
COPY --from=build /publish .

# Any RUN steps that write to system paths (e.g., /usr/local/bin) must come BEFORE USER
RUN ln -s /app/HelixTool /usr/local/bin/hlx

# Switch to non-root before runtime.
# docker run --rm -i (stdio MCP via gh-aw) is unaffected — stdin/stdout are
# file descriptors and do not require host-side UID matching.
USER hlx

ENTRYPOINT ["hlx"]
```

### Key rules

1. `RUN useradd` must come before `USER hlx` (obviously).
2. Any `RUN` that writes to system paths (symlinks into `/usr/local/bin`, `apt-get`, etc.) must also come before `USER hlx`.
3. `useradd -m` creates the home directory with correct ownership — do **not** add `chmod 0777` afterward.
4. For stdio MCP containers: `docker run --rm -i` passes stdin via file descriptor, not via TTY — non-root UID does not interfere.

---

## Recipe 2 — Digest-pin base images

### Why

Mutable tags like `:10.0` can be updated at any time. A rebuild days later may pull a different image, producing a non-reproducible build. Digest pins lock the exact image layer set.

### How to get digests (no Docker daemon needed)

```bash
curl -s -I \
  -H "Accept: application/vnd.docker.distribution.manifest.list.v2+json" \
  "https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0" \
  | grep -i "docker-content-digest"

curl -s -I \
  -H "Accept: application/vnd.docker.distribution.manifest.list.v2+json" \
  "https://mcr.microsoft.com/v2/dotnet/runtime/manifests/10.0" \
  | grep -i "docker-content-digest"
```

(Replace registry and image path as needed. Works for any OCI-compliant registry.)

If `docker buildx` is available:
```bash
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0 \
  --format '{{json .Manifest}}' | jq -r '.digest // .manifests[0].digest'
```

### Dockerfile pattern

```dockerfile
# Tag kept for human debuggability; the digest is what actually gets pulled.
# Future bumps via Dependabot will update both tag and digest together.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:<actual-digest> AS build
...
FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:<actual-digest>
```

### Notes

- Keep the human-readable tag in the line — it documents *intent* and makes Dependabot PRs readable.
- If you previously used `ARG DOTNET_VERSION=10.0` with `FROM image:${DOTNET_VERSION}`, drop the ARG once you add digest pinning. The ARG can no longer be used to override the version at build time (the digest is tag-specific), and a non-functional ARG is misleading.
- Dependabot handles `mcr.microsoft.com` images if configured with `package-ecosystem: docker`.

---

## Recipe 3 — Verify GitHub Actions SHA pin comments

### Why

SHA pins prevent supply-chain attacks but are opaque without the version comment. Stale or wrong comments confuse future auditors and make Dependabot PRs harder to review.

### Verification procedure

GitHub Actions uses annotated tags. The SHA in the YAML must be the **commit** SHA that the tag points to, not the tag *object* SHA. Two-step resolution:

```bash
# Step 1: get tag object SHA
TAG_OBJ=$(gh api repos/<owner>/<repo>/git/refs/tags/v1.2.3 --jq '.object.sha')
echo "tag obj: $TAG_OBJ"

# Step 2: if it's a tag object (not a commit), resolve to commit SHA
gh api repos/<owner>/<repo>/git/tags/$TAG_OBJ --jq '.object.sha + " " + .object.type'
# If type == "commit", the SHA from step 2 is what should appear in the YAML.
# If type == "tag", recurse (rare — double-nested annotated tags).
```

If the commit SHA from step 2 matches the YAML → comment is correct, leave it.  
If not → update the comment to the correct tag, or remove it.

### Decision: keep vs. remove comments

| Situation | Recommendation |
|-----------|----------------|
| All comments verified correct | Keep — correct comments help auditors |
| Some wrong, can't easily fix | Remove all trailing comments; rely on Dependabot |
| Dependabot is configured | Either works — Dependabot will fix comments on next bump |

---

## Recipe 4 — Smoke tests

Run these after `docker buildx build --load -t <test-tag> .`:

```bash
# 1. Binary on PATH, exits quickly
docker run --rm <test-tag> --help | head -5

# 2. Stdio MCP: JSON-RPC initialize round-trip
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}' \
  | docker run --rm -i <test-tag> | head -5
# Expected: JSON response containing "result" and "serverInfo"

# 3. Non-root confirmation
docker run --rm <test-tag> id
# Expected: uid=1000(hlx) gid=65534(nogroup) ...  (NOT uid=0(root))
```

### If Docker is not available locally

The CI workflow triggered by pushing to the branch is an acceptable substitute for functional verification. Document this in the PR comment so reviewers know local smoke tests were skipped.

---

## Cross-fork push workflow (reusable)

```bash
# Add fork as remote (idempotent — 2>/dev/null suppresses "already exists")
git remote add <fork-alias> https://github.com/<fork-owner>/<repo>.git 2>/dev/null || true
git fetch <fork-alias> <branch>

# Check out tracking the fork
git checkout -B <branch> <fork-alias>/<branch>

# Verify push access BEFORE making changes
git push --dry-run <fork-alias> <branch> 2>&1 | head -10
# "Everything up-to-date" or "To https://..." = proceed
# "Permission denied" / "403" = STOP; fork owner has not enabled "Allow edits from maintainers"

# After editing + committing:
git push <fork-alias> <branch>
```

**Fallback if access denied:** Merge the upstream PR as-is; open a follow-up PR with the hardening changes from the upstream repo. Document both options in the PR comment.

---

*First applied: PR #77 (PureWeen/helix.mcp), 2026-06-25*
