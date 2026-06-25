# SKILL: Cutting a Release for helix.mcp

## When to use

Use this skill whenever Larry asks to cut a release (e.g., "prep vX.Y.Z release").

---

## Pre-flight checks

1. Confirm the target version (`vMAJOR.MINOR.PATCH`).
2. Identify the current `origin/main` tip SHA — the branch must be cut from there.
3. Read `.squad/agents/ripley/history.md` for any notes on the previous release process.

---

## Step 1 — Branch

```sh
git fetch origin
git checkout -b release/vX.Y.Z origin/main
```

Never branch from a local `main` that may have diverged.

---

## Step 2 — Version bump (exactly 3 occurrences)

The `publish.yml` workflow validates all three match the pushed tag. Any mismatch aborts the release.

| File | Field | Example |
|------|-------|---------|
| `src/HelixTool/HelixTool.csproj` | `<Version>` | `<Version>0.8.0</Version>` |
| `src/HelixTool/.mcp/server.json` | top-level `"version"` | `"version": "0.8.0"` |
| `src/HelixTool/.mcp/server.json` | `packages[0].version` | `"version": "0.8.0"` |

Verify after editing:
```sh
grep -oP '(?<=<Version>)[^<]+' src/HelixTool/HelixTool.csproj
jq -r '.version, .packages[0].version' src/HelixTool/.mcp/server.json
```
All three must print the new version string.

---

## Step 3 — Release notes

Write to `.squad/release-notes/vX.Y.Z.md`. Sections:
- **Headline** — one sentence describing the release theme.
- **Highlights** — top 3–5 bullets a user cares about.
- **New Features** — with PR citations (#NN).
- **Bug Fixes** — with PR citations.
- **Dependencies** — SDK bumps, security pins.
- **Infrastructure** — CI/tooling changes.
- **Full Changelog** — `https://github.com/lewing/helix.mcp/compare/vPREV...vNEXT`

---

## Step 4 — Build + test sanity check

```sh
dotnet build -c Release --no-incremental   # must be: 0 Warning(s), 0 Error(s)
dotnet test  -c Release --no-build          # must be: 0 Failed
```

**Stop and report** if any tests are red. Do not proceed with a broken build.

---

## Step 5 — Commit

```sh
git add -- src/HelixTool/HelixTool.csproj \
           src/HelixTool/.mcp/server.json \
           .squad/release-notes/vX.Y.Z.md
git commit -m "release: vX.Y.Z (version bump)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git push -u origin release/vX.Y.Z
```

Single commit. No extra files.

---

## Step 6 — Open the PR

```sh
gh pr create \
  --base main \
  --head release/vX.Y.Z \
  --title "release: vX.Y.Z" \
  --body-file .squad/release-notes/vX.Y.Z.md
gh pr edit <NUMBER> --body-file <updated-body-with-intro>
```

The PR body must include:
1. A brief intro (files changed, build/test result).
2. The full release notes inlined.
3. **"Do not auto-merge. Larry merges, then pushes the `vX.Y.Z` tag to trigger publish.yml."**
4. Co-authored-by trailer.

Open as **ready-for-review** (not draft). CCA must review before Larry merges.

---

## Step 7 — Do NOT push the tag

Ripley's job ends when the PR is open and ready-for-review. Larry merges the bump PR, then runs:

```sh
git tag vX.Y.Z <merge-commit-sha>
git push origin vX.Y.Z
```

The `publish.yml` workflow (`on: push: tags: "v*"`) triggers automatically and:
1. Validates all three version fields match the tag.
2. Packs the NuGet package.
3. Creates a GitHub Release with the `.nupkg` artifact.
4. Pushes to NuGet (`https://api.nuget.org/v3/index.json`).

---

## Versioning policy

| Change type | Bump |
|-------------|------|
| New observable behavior (strict rejection, new filter surface, SDK minor) | **minor** |
| Bug fix / security pin / dep patch only | **patch** |
| Breaking public API contract | **major** |

helix.mcp has no stable public API contract yet (pre-1.0), so "major" is reserved for truly incompatible CLI/MCP tool signature changes.

---

## After-work

1. Append release summary to `.squad/agents/ripley/history.md`:
   - PR number, branch tip SHA, files changed, build/test result, version rationale, next-steps tag command.
2. Update this SKILL.md if the process changed.
