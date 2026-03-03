### 2026-03-03: Helix authentication UX analysis
**By:** Ash
**What:** Feasibility analysis for improving hlx authentication experience
**Why:** Larry asked about Entra auth — the answer is "not directly" but there are UX improvements we can make

---

## 1. Current State

### How auth works today

hlx uses a two-transport auth model via `IHelixTokenAccessor`:

| Transport | Implementation | How token is provided |
|-----------|---------------|----------------------|
| CLI (stdio) | `EnvironmentHelixTokenAccessor` | `HELIX_ACCESS_TOKEN` env var, set by user before invoking `hlx` |
| HTTP MCP | `HttpContextHelixTokenAccessor` | `Authorization: Bearer <token>` or `Authorization: token <token>` header per request; falls back to env var |

The token is passed to `HelixApiClient` → `HelixApiTokenCredential` → `HelixApiTokenAuthenticationPolicy`, which sends it to the Helix API as `Authorization: token <TOKEN>`.

### UX pain points

1. **Manual token generation:** Users must navigate to helix.dot.net → log in with Microsoft SSO → go to Profile → generate an API access token → copy it.
2. **Manual env var setup:** CLI users must `export HELIX_ACCESS_TOKEN=<token>` in every new shell session. No persistence.
3. **No `hlx login` flow:** Unlike `az login`, `gh auth login`, or `darc authenticate`, there is no interactive auth command.
4. **Token management is invisible:** No way to check if a token is configured, valid, or expired. Users only discover auth problems when API calls fail with "Access denied."
5. **Repeated error messages:** All 7 catch blocks in `HelixService.cs` repeat the same guidance string: _"Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page."_

---

## 2. What's NOT Possible: Direct Entra Auth to Helix API

**The Helix API does not accept Entra (Azure AD) JWT tokens.** This is a server-side limitation, not a client-side one.

**Evidence:**
- The Helix SDK's auth model uses `HelixApiTokenCredential`, which wraps a plain opaque string — not a JWT.
- `HelixApiTokenAuthenticationPolicy` sends `Authorization: token <TOKEN>`, a custom scheme. The Helix API server validates these opaque tokens against its own token store, not against Entra/AAD.
- While helix.dot.net's _web UI_ uses Microsoft SSO for login, the API access tokens generated from the Profile page are opaque strings decoupled from the user's Entra session. They are not JWTs, cannot be refreshed via Entra, and have no OAuth scopes.
- Contrast with Maestro/PCS, which explicitly uses `AddMicrosoftIdentityWebApi` and accepts Entra JWT Bearer tokens. Helix has no equivalent.

**Bottom line:** Until the Helix service team adds Entra API auth support server-side, hlx cannot use Entra tokens to call the Helix API. This is not something we can solve on the client side.

---

## 3. What IS Possible: Practical UX Improvements

### 3a. `hlx login` — Interactive Token Setup

**Concept:** A new CLI command that guides users through token acquisition:

```
$ hlx login
No Helix token found.

To authenticate, you need an API token from helix.dot.net:

  1. Opening https://helix.dot.net/Account/Tokens in your browser...
  2. Log in with your Microsoft account if prompted
  3. Generate a new API access token
  4. Paste it here

Token: ••••••••••••••••••••

✓ Token stored successfully.
  Stored in: system keychain (helix.dot.net)
  Test: Attempting API call... ✓ Authenticated as lewing@microsoft.com
```

**Implementation sketch:**
- Open browser via `Process.Start` (cross-platform: `xdg-open` on Linux, `open` on macOS, `start` on Windows)
- Read token from stdin with echo disabled (`Console.ReadLine` after clearing echo, or use Spectre.Console `TextPrompt` with `IsSecret = true`)
- Validate token by making a lightweight API call (e.g., list a known public job)
- Store token securely (see 3b)

**New `IHelixTokenAccessor` implementation:** `StoredHelixTokenAccessor` that reads from secure storage, falling back to env var.

### 3b. Secure Token Storage

**Option A: `git credential` (recommended)**
Leverage the user's existing git credential manager, which already handles cross-platform secure storage:
```
$ echo "protocol=https\nhost=helix.dot.net\nusername=helix-api\n" | git credential fill
```
- **Pro:** Works everywhere git works. Users already have credential managers configured (macOS Keychain, Windows Credential Manager, libsecret on Linux). Zero new dependencies.
- **Pro:** Precedent in the .NET ecosystem — `darc` uses a similar pattern.
- **Con:** Requires git to be installed (safe assumption for this user base).

**Option B: Platform keychain directly**
Use `System.Security.Cryptography.ProtectedData` (Windows), macOS Keychain via P/Invoke, or libsecret on Linux.
- **Pro:** No git dependency.
- **Con:** Three platform-specific implementations to maintain. Significant effort.

**Option C: Encrypted file in `~/.hlx/`**
Store token in an encrypted file with user-only permissions (`chmod 600`).
- **Pro:** Simple, portable.
- **Con:** Less secure than keychain. Encryption key management is its own problem.

**Recommendation:** Option A (`git credential`). It's battle-tested, cross-platform, zero new dependencies for our user base, and follows the pattern that `darc` and `gh` already use.

### 3c. Token Resolution Order

New resolution order for `IHelixTokenAccessor` in CLI mode:

1. `--token` CLI flag (explicit, one-time use)
2. `HELIX_ACCESS_TOKEN` env var (backward compatible)
3. Secure storage via `git credential` (new — the `hlx login` destination)
4. Prompt user: _"No token found. Run `hlx login` to authenticate."_

For HTTP MCP transport, keep the current `HttpContextHelixTokenAccessor` behavior (Authorization header → env var fallback). Add secure storage as a third fallback.

### 3d. `hlx auth status` — Token Health Check

```
$ hlx auth status
Token source: system keychain (helix.dot.net)
API test: ✓ Connected
```

Or when unconfigured:
```
$ hlx auth status
⚠ No Helix token configured.
Run `hlx login` to authenticate, or set HELIX_ACCESS_TOKEN.
```

### 3e. `hlx logout` — Token Removal

```
$ hlx logout
✓ Token removed from system keychain.
```

Clears the stored credential. Does not revoke the token on the server (Helix API doesn't have a revocation endpoint).

---

## 4. Future Possibility: If Helix Adopts Entra API Auth

If the Helix service team ever adds Entra JWT Bearer token support (as Maestro/PCS did), the client-side implementation would look like:

### What Helix would need to change (server-side)
- Add `Microsoft.Identity.Web` or equivalent middleware
- Accept `Authorization: Bearer <JWT>` alongside existing `Authorization: token <opaque>`
- Register an app in Entra with appropriate API scopes

### What hlx would change (client-side)
1. **New `IHelixTokenAccessor` implementation:** `EntraHelixTokenAccessor` using `Azure.Identity`
2. **Token acquisition strategies** (mirroring Darc's `AppCredentialResolver`):
   - `InteractiveBrowserCredential` — `hlx login --entra` opens browser for SSO
   - `AzureCliCredential` — use existing `az login` session
   - `ManagedIdentityCredential` — for CI/CD environments
   - `DefaultAzureCredential` — automatic chain of all the above
3. **Token refresh:** Entra JWTs expire (typically 1 hour). `Azure.Identity` handles refresh automatically via `TokenCredential.GetTokenAsync`.
4. **Updated `HelixApiClient`:** Accept either `TokenCredential` (Entra) or `string` (opaque token) in constructor, using the appropriate auth policy.

### Architecture compatibility
The existing `IHelixTokenAccessor` interface returns `string?`, which works for both opaque tokens and JWTs (a JWT is just a longer string). No interface change needed — the Entra accessor would return the JWT string, and `HelixApiTokenCredential` would pass it through. The Helix server would need to accept it in the `token` scheme or hlx would need a new auth policy for `Bearer` scheme.

Alternatively, if Helix adopted standard Bearer auth, `HelixApiClient` could accept `Azure.Identity.TokenCredential` directly, bypassing the custom auth policy entirely. This is the cleaner path and what Maestro does.

---

## 5. Recommended Next Steps

### Phase 1: `hlx login` + `git credential` storage (HIGH VALUE, LOW EFFORT)

| Item | Effort | Impact |
|------|--------|--------|
| `hlx login` command with browser open + token paste | Small | Eliminates biggest UX pain point |
| `git credential`-based secure storage | Small | Eliminates env var requirement |
| `StoredHelixTokenAccessor` with resolution chain | Small | Clean architecture extension |
| `hlx auth status` | Trivial | Confidence builder for users |
| `hlx logout` | Trivial | Cleanup story |

**Estimated total effort:** 1-2 days for a focused implementation.

### Phase 2: Polish (MEDIUM VALUE)

- Token validation on `hlx login` (test API call before storing)
- Better error messages when token is expired or invalid
- `--token` CLI flag for one-off use without storage

### Phase 3: Entra auth (FUTURE — blocked on Helix server)

- Only pursue if/when Helix service announces Entra API auth support
- Monitor Helix service roadmap or ask the Helix team directly
- Implementation would be ~1 week using `Azure.Identity` SDK

### Decision needed from Dallas

This analysis identifies a clear UX improvement path that doesn't require any server-side changes. Recommend Dallas approve Phase 1 scope so Ripley can implement.

**Key architecture question:** Should `StoredHelixTokenAccessor` live in `HelixTool.Core` (available to both CLI and MCP) or in `HelixTool` (CLI only)? The `git credential` dependency suggests CLI-only, but the resolution chain pattern is useful for MCP too.
