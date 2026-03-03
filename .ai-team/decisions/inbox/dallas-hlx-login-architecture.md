### 2026-03-03: hlx login architecture
**By:** Dallas
**What:** Architecture for Phase 1 auth UX (hlx login + credential storage)
**Why:** Eliminate manual HELIX_ACCESS_TOKEN env var setup — biggest UX friction point

---

## Decision Summary

Phase 1 approved. This document defines the architecture for `hlx login`, `hlx auth status`, `hlx logout`, credential storage, and the token resolution chain. Ash's analysis was thorough; I'm making a few architectural calls where options were left open.

---

## 1. New Commands — UX Design

### `hlx login`

```
$ hlx login
To authenticate, you need an API token from helix.dot.net:

  1. Opening https://helix.dot.net/Account/Tokens in your browser...
  2. Log in with your Microsoft account if prompted
  3. Generate a new API access token
  4. Paste it below

Token: ••••••••••••••••••••

Validating... ✓ Token is valid.
✓ Token stored successfully.
```

**Behavior:**
- Attempt to open `https://helix.dot.net/Account/Tokens` in the default browser via `Process.Start` (cross-platform: `xdg-open` / `open` / `start`). If the browser open fails, print the URL and tell the user to open it manually. This is non-blocking — don't fail the command if the browser can't open.
- Prompt for token input using Spectre.Console `TextPrompt<string>` with `IsSecret = true` (mask with `•`). We already depend on Spectre.Console transitively via ConsoleAppFramework, but verify — if not, this is an acceptable new dependency for a CLI tool.
- **Validate the token** before storing: make a lightweight API call (e.g., `GET /api/2019-06-17/jobs?count=1`). If it fails with 401, tell the user the token is invalid and do NOT store it. If it fails with a network error, warn but still offer to store (the token might be valid on a different network).
- Store via `git credential approve` (see §2).
- If a token is already stored, warn and ask for confirmation before overwriting: `"A token is already stored. Replace it? [y/N]"`

**Flag:** `--no-browser` — skip the browser open step (useful for SSH sessions, headless environments).

### `hlx auth status`

```
$ hlx auth status
Token source: stored credential (git credential)
API test:     ✓ Connected
```

```
$ hlx auth status
Token source: HELIX_ACCESS_TOKEN environment variable
API test:     ✓ Connected
```

```
$ hlx auth status
⚠ No Helix token configured.
Run `hlx login` to authenticate, or set HELIX_ACCESS_TOKEN.
```

**Behavior:**
- Report which source the token came from (env var vs stored credential vs none).
- Make a test API call to validate the token is still working.
- Exit code: 0 if authenticated, 1 if not. This makes it scriptable.
- **Do NOT print the token itself.** Security 101.

### `hlx logout`

```
$ hlx logout
✓ Token removed from credential store.
```

**Behavior:**
- Remove the stored credential via `git credential reject`.
- If no stored credential exists, print "No stored token found." and exit 0 (idempotent).
- Does NOT clear the `HELIX_ACCESS_TOKEN` env var (we can't modify the parent process's environment; print a hint if the env var is set).
- Does NOT revoke the token server-side (Helix API has no revocation endpoint).

### Command grouping

These are flat commands, not subcommands of an `auth` group:
- `hlx login`
- `hlx logout`
- `hlx auth status` (or `hlx auth-status` — ConsoleAppFramework maps kebab-case to methods)

Rationale: `login`/`logout` are top-level for ergonomics (matching `gh auth login` being commonly shortened to just the common flow). `auth status` is naturally grouped since it's a diagnostic, not a primary action.

Actually — let me reconsider. ConsoleAppFramework supports command grouping via nested classes. Let's use:
- `hlx login` → top-level (most common)
- `hlx logout` → top-level (most common)
- `hlx auth status` → subcommand under `auth` group

This matches `gh` CLI conventions where `login`/`logout` are top-level shortcuts.

**UPDATE:** After reviewing ConsoleAppFramework docs — it doesn't easily support aliased top-level commands that also exist in a group. Keep it simple:
- `hlx login` — top-level command
- `hlx logout` — top-level command  
- `hlx auth status` — top-level command (single command, not a group)

All three are methods on the existing `Commands` class. No subcommand grouping needed for three commands.

---

## 2. Credential Storage — Decision: `git credential` (Option A)

**Chosen: Option A — `git credential` store.**

Rationale:
1. **Zero new dependencies.** Our users are .NET developers who have git installed. This is a safe assumption.
2. **Cross-platform by default.** git credential helpers handle macOS Keychain, Windows Credential Manager, and libsecret/gnome-keyring on Linux — all transparently.
3. **Proven pattern.** `darc authenticate` uses the same approach. `gh` CLI uses a similar credential-helper model.
4. **No crypto key management.** Options B/C/D all require us to manage encryption keys or platform-specific APIs. git credential delegates this entirely.
5. **Minimal surface area.** Two shell-outs: `git credential approve` (store) and `git credential reject` (delete). That's it.

**Why not the others:**
- **Option B** (`ProtectedData`): Windows-only. Non-starter for a cross-platform tool.
- **Option C** (Platform keychain NuGet): Adds a dependency, and we'd need to vet the package. git credential already does this.
- **Option D** (Encrypted file): We'd have to solve key management. `chmod 600` isn't encryption. Weakest option.

### Implementation Details

**Credential protocol format:**
```
protocol=https
host=helix.dot.net
username=helix-api-token
password=<the-actual-token>
```

- `username` is a fixed sentinel value (`helix-api-token`), not the user's actual username. Helix tokens are not scoped per-user in a way we can introspect.
- `protocol=https` + `host=helix.dot.net` uniquely identifies this credential so it doesn't collide with other git credentials.

**Store (git credential approve):**
```csharp
var psi = new ProcessStartInfo("git", "credential approve")
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};
var process = Process.Start(psi);
await process.StandardInput.WriteLineAsync("protocol=https");
await process.StandardInput.WriteLineAsync("host=helix.dot.net");
await process.StandardInput.WriteLineAsync("username=helix-api-token");
await process.StandardInput.WriteLineAsync($"password={token}");
await process.StandardInput.WriteLineAsync();  // blank line terminates
await process.WaitForExitAsync();
```

**Retrieve (git credential fill):**
```csharp
// Write protocol + host + username, read back password field
var psi = new ProcessStartInfo("git", "credential fill") { ... };
// Parse output for "password=..." line
```

**Delete (git credential reject):**
```csharp
// Same format as approve, but with "reject" subcommand
```

**Error handling:**
- If `git` is not on PATH, throw a clear error: `"git is not installed or not on PATH. Install git or set HELIX_ACCESS_TOKEN manually."`
- If `git credential` fails (no credential helper configured), warn the user: `"No git credential helper configured. Token will not persist. See https://git-scm.com/docs/gitcredentials"`

### New class: `GitCredentialStore`

This is a **pure infrastructure class** — it knows how to talk to `git credential` but knows nothing about Helix. Lives in `HelixTool.Core` (see §4).

```csharp
namespace HelixTool.Core;

public interface ICredentialStore
{
    Task<string?> GetTokenAsync(string host, string username, CancellationToken ct = default);
    Task StoreTokenAsync(string host, string username, string token, CancellationToken ct = default);
    Task DeleteTokenAsync(string host, string username, CancellationToken ct = default);
}

public sealed class GitCredentialStore : ICredentialStore { ... }
```

Interface allows testing with a mock store. The `host` and `username` parameters make it reusable if we ever need to store other credentials.

---

## 3. Token Resolution Chain

### CLI mode (stdio + direct commands)

Replace the current `EnvironmentHelixTokenAccessor` singleton with a new `ChainedHelixTokenAccessor`:

```
Priority 1: HELIX_ACCESS_TOKEN environment variable  (backward compat — highest wins)
Priority 2: Stored credential via git credential
Priority 3: null (no token — let the API call fail with a helpful message)
```

**Why env var wins over stored credential:** Backward compatibility. Users who have `HELIX_ACCESS_TOKEN` set today must not see behavior changes. Also, env vars are the standard mechanism for CI/CD overrides — a CI system should be able to `export HELIX_ACCESS_TOKEN=...` and have it take effect regardless of what's in the credential store.

**No interactive prompt in the chain.** The token accessor runs during DI container setup (before any command executes). Prompting for a token during DI would be terrible UX. If no token is found, the accessor returns `null`, and the API call fails with the existing `HelixException` message — which we should update to say: `"Access denied. Run 'hlx login' to authenticate, or set HELIX_ACCESS_TOKEN."` (update all 7 catch blocks in HelixService.cs).

### MCP stdio mode (`hlx mcp`)

Same `ChainedHelixTokenAccessor` as CLI mode. The MCP server launched via `hlx mcp` is a child process — it inherits env vars and has access to the same git credential store.

### HTTP MCP mode (`HelixTool.Mcp`)

Keep `HttpContextHelixTokenAccessor` as-is. The HTTP transport gets its token from the per-request Authorization header, with env var fallback. Adding git credential as a third fallback is reasonable:

```
Priority 1: Authorization header (Bearer or token scheme)
Priority 2: HELIX_ACCESS_TOKEN environment variable
Priority 3: Stored credential via git credential
```

This means the HTTP MCP server can function without any explicit token configuration if the user has run `hlx login`.

### New class: `ChainedHelixTokenAccessor`

```csharp
namespace HelixTool.Core;

public sealed class ChainedHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly string? _envToken;
    private readonly ICredentialStore _store;
    private string? _cachedToken;
    private bool _resolved;

    public ChainedHelixTokenAccessor(ICredentialStore store)
    {
        _envToken = Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN");
        _store = store;
    }

    public string? GetAccessToken()
    {
        if (!string.IsNullOrEmpty(_envToken))
            return _envToken;

        if (!_resolved)
        {
            // Synchronous wrapper — git credential is fast (<100ms)
            _cachedToken = _store.GetTokenAsync("helix.dot.net", "helix-api-token")
                .GetAwaiter().GetResult();
            _resolved = true;
        }
        return _cachedToken;
    }
}
```

**Note on sync-over-async:** `IHelixTokenAccessor.GetAccessToken()` is synchronous (returns `string?`). The git credential shell-out is async. We use `.GetAwaiter().GetResult()` here, which is acceptable because: (a) it runs once during startup, (b) `git credential fill` completes in <100ms, and (c) changing the interface to async would be a large cross-cutting change that's not justified for Phase 1.

The `_resolved` flag ensures we shell out to git at most once per process lifetime. This is important for MCP stdio mode where the process is long-lived.

### `auth status` token source reporting

`ChainedHelixTokenAccessor` should expose which source provided the token:

```csharp
public enum TokenSource { None, EnvironmentVariable, StoredCredential }
public TokenSource Source { get; private set; }
```

This is used by `hlx auth status` to report where the token came from.

---

## 4. Code Location

### New files in `HelixTool.Core`

| File | What |
|------|------|
| `ICredentialStore.cs` | Interface: `GetTokenAsync`, `StoreTokenAsync`, `DeleteTokenAsync` |
| `GitCredentialStore.cs` | Implementation using `git credential` CLI |
| `ChainedHelixTokenAccessor.cs` | New `IHelixTokenAccessor` impl with env var → stored credential chain |

**Why Core, not CLI?** The credential store and chained accessor are needed by both `HelixTool` (CLI) and `HelixTool.Mcp` (HTTP MCP server fallback). Keeping them in Core avoids duplicating the git credential logic.

### New code in `HelixTool` (CLI-specific)

| Location | What |
|----------|------|
| `Program.cs` — `Commands` class | Three new command methods: `Login()`, `Logout()`, `AuthStatus()` |
| `Program.cs` — DI setup | Replace `EnvironmentHelixTokenAccessor` with `ChainedHelixTokenAccessor` |

The login/logout/auth-status commands are CLI-only — they don't make sense in MCP context. They go in the existing `Commands` class.

### Modified files

| File | Change |
|------|--------|
| `IHelixTokenAccessor.cs` | No interface change. Add `ChainedHelixTokenAccessor` class (or separate file). |
| `Program.cs` (CLI) | DI: swap `EnvironmentHelixTokenAccessor` → `ChainedHelixTokenAccessor`. Add commands. |
| `Program.cs` (MCP HTTP) | DI: add `ICredentialStore` registration. Update `HttpContextHelixTokenAccessor` to accept it as third fallback. |
| `HttpContextHelixTokenAccessor.cs` | Add `ICredentialStore` parameter for third-level fallback. |
| `HelixService.cs` | Update all 7 error messages from `"Set the HELIX_ACCESS_TOKEN..."` to `"Run 'hlx login' to authenticate, or set HELIX_ACCESS_TOKEN."` |

### No new NuGet packages required

- `Process.Start` for git — already available in `System.Diagnostics`
- `Spectre.Console` for secret prompt — check if already a dependency. If not, it's a reasonable add for a CLI tool (small, well-maintained). **Alternative:** Use `Console.ReadLine()` with manual echo suppression if we want zero new deps.

---

## 5. Work Items for Ripley

Ordered by dependency. Each item is independently testable.

### WI-1: `ICredentialStore` + `GitCredentialStore`
**Files:** `src/HelixTool.Core/ICredentialStore.cs`, `src/HelixTool.Core/GitCredentialStore.cs`
**What:** Interface and git credential implementation. Three operations: get, store, delete. Input validation (non-empty host/username). Proper process lifecycle management (dispose, timeout).
**Tests (Lambert):** Mock process execution. Verify protocol format. Error handling for missing git. Timeout handling.
**Estimate:** Small

### WI-2: `ChainedHelixTokenAccessor`
**Files:** `src/HelixTool.Core/ChainedHelixTokenAccessor.cs`
**What:** New `IHelixTokenAccessor` implementation. Env var → stored credential → null. Expose `TokenSource` enum for `auth status`. Cache the resolved token (resolve once per process).
**Depends on:** WI-1
**Tests (Lambert):** Env var wins over stored. Stored returned when no env var. Null when neither. TokenSource set correctly.
**Estimate:** Small

### WI-3: DI wiring update
**Files:** `src/HelixTool/Program.cs` (both CLI and MCP DI blocks)
**What:** Register `ICredentialStore` → `GitCredentialStore`. Replace `EnvironmentHelixTokenAccessor` with `ChainedHelixTokenAccessor` in CLI and MCP stdio paths. Add `ICredentialStore` fallback to HTTP MCP accessor.
**Depends on:** WI-2
**Tests (Lambert):** Integration test verifying DI resolves correctly. Token from env var still works (regression guard).
**Estimate:** Small

### WI-4: `hlx login` command
**Files:** `src/HelixTool/Program.cs` (Commands class)
**What:** Browser open (best-effort, cross-platform). Token prompt (secret input). Token validation via test API call. Store via `ICredentialStore`. Overwrite confirmation if existing token. `--no-browser` flag.
**Depends on:** WI-1, WI-3
**Tests (Lambert):** Mock `ICredentialStore` to verify store is called. Verify browser open attempt. Verify validation call.
**Estimate:** Medium

### WI-5: `hlx logout` command
**Files:** `src/HelixTool/Program.cs` (Commands class)
**What:** Delete stored credential. Warn if env var is also set. Idempotent (no error if nothing stored).
**Depends on:** WI-1, WI-3
**Tests (Lambert):** Verify delete is called. Idempotent behavior.
**Estimate:** Trivial

### WI-6: `hlx auth status` command
**Files:** `src/HelixTool/Program.cs` (Commands class)
**What:** Report token source (env var / stored / none). Test API call. Exit code 0/1.
**Depends on:** WI-2, WI-3
**Tests (Lambert):** All three source states. Exit code behavior.
**Estimate:** Small

### WI-7: Error message update
**Files:** `src/HelixTool.Core/HelixService.cs`
**What:** Update all 7 catch blocks from `"Set the HELIX_ACCESS_TOKEN environment variable..."` to `"Access denied. Run 'hlx login' to authenticate, or set HELIX_ACCESS_TOKEN."`. Extract the message to a constant to avoid repetition.
**Depends on:** None (can be done in parallel)
**Tests (Lambert):** Update existing `HelixAuthTests.cs` assertions to match new message.
**Estimate:** Trivial

---

## 6. What This Does NOT Include (Phase 2+)

- `--token` CLI flag for one-off use — Phase 2
- Token expiration detection/proactive warning — Phase 2
- Entra/Azure AD auth — blocked on Helix server, Phase 3
- MCP tool for token management — not needed, CLI is the right UX
- Automatic token refresh — not possible with opaque tokens

---

## 7. Open Questions (Non-blocking)

1. **Spectre.Console dependency:** Is it already pulled in transitively? If not, is `Console.ReadLine()` with manual echo disable acceptable? → Ripley should check during WI-4 and decide. Either approach is fine architecturally.

2. **Token validation endpoint:** Which Helix API call is cheapest for validation? Ash suggested listing jobs with `count=1`. Ripley should verify this works without any specific job context.

3. **HttpContextHelixTokenAccessor fallback:** Adding git credential as a third fallback for the HTTP MCP server is nice-to-have. If it complicates things (the HTTP server might run in a container without git), Ripley can defer this to Phase 2. The critical path is CLI only.
