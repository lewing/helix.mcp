using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Cache;

namespace HelixTool.Core.Delivery;

/// <summary>
/// Disk-backed <see cref="IEvidenceDeliveryStore"/>. Entries live under
/// <c>{EffectiveCacheRoot}/delivery/{cachePartition}/{evidenceId}.bin</c> plus a sidecar
/// <c>.meta.json</c> descriptor, so repeated reads across requests resolve the same id without
/// re-materializing the content. This directory sits outside read-only snapshot exports: derived
/// views are always reconstructable from the backing cache key, so they add no new snapshot key.
/// <para>
/// <see cref="EvidenceDescriptor.EvidenceId"/>s are content-addressed (a SHA-256 of the bytes plus media type, not a
/// random GUID): identical payloads materialized more than once under the same partition —
/// a common case, since the same oversized timeline page or record can be requested again,
/// retried, or paged alongside a sibling continuation — reuse the same on-disk entry instead of
/// growing the delivery directory without bound.
/// </para>
/// <para>
/// <b>Leases/pins.</b> Every time a descriptor for an entry is handed out — <see cref="PutBytes"/>/
/// <see cref="PutText"/> (both a fresh write and a content-addressed dedupe hit), <see cref="Read"/>,
/// and <see cref="TryGetDescriptor"/> — its <see cref="EvidenceDescriptor.PinnedUntilUtc"/> is
/// refreshed to <c>UtcNow + <see cref="MinRetentionWindow"/></c> and persisted back to the sidecar.
/// A receipt returned in any response is therefore guaranteed readable for at least
/// <see cref="MinRetentionWindow"/> from the moment it (or a fresh lookup/read of the same id) was
/// last returned — including while a chunked <see cref="Read"/> of it is still in progress, since
/// that same call refreshes the pin before any bytes are returned. Retention (below) never deletes
/// a pinned entry; content-addressed dedupe refreshes the existing pin instead of creating a
/// competing entry, so a hot id's protection window keeps sliding forward rather than racing a
/// separate copy's expiry.
/// </para>
/// <para>
/// Retention is bounded and runs opportunistically after each <see cref="PutBytes"/>/
/// <see cref="PutText"/>, derived from the same <see cref="CacheOptions.MaxSizeBytes"/>/
/// <see cref="CacheOptions.ArtifactMaxAge"/> configuration that already governs the primary
/// SQLite-backed artifact cache, so one retention policy covers both: pinned entries (see above)
/// are first excluded entirely from consideration; of the remainder, entries older than
/// <see cref="CacheOptions.ArtifactMaxAge"/> (by last access) are deleted outright, then any still
/// remaining are evicted oldest-access-first until the partition's total bytes are back under
/// <see cref="CacheOptions.MaxSizeBytes"/>. If pinned entries alone exceed
/// <see cref="CacheOptions.MaxSizeBytes"/> (or evicting every unpinned entry still isn't enough),
/// retention stops — it never deletes a live pin — and instead leaves a
/// <see cref="RetentionStatus"/> record (see <see cref="TryGetRetentionStatus"/>) reporting the
/// soft-cap overage, so a caller/operator can see it rather than silently losing evidence a
/// response just promised was available. A delete that fails because the file is open is treated
/// as "an in-flight <c>hlx_read_evidence</c> call may be reading it" and is skipped rather than
/// retried in the same sweep (best-effort: Windows actually enforces this via its share-mode
/// locks; POSIX allows unlinking an open file, so this protection does not apply on Linux/macOS —
/// the pin is the actual cross-platform protection; the open-file skip is defense in depth).
/// </para>
/// </summary>
public sealed class FileEvidenceDeliveryStore(CacheOptions cacheOptions) : IEvidenceDeliveryStore
{
    /// <summary>
    /// Minimum guaranteed protection window after a descriptor for an entry is handed out (Put,
    /// dedupe refresh, Read, or TryGetDescriptor lookup): retention will not delete that entry
    /// before <c>(the instant it was last handed out) + MinRetentionWindow</c>, even if doing so
    /// would keep the partition under <see cref="CacheOptions.MaxSizeBytes"/>.
    /// </summary>
    public static readonly TimeSpan MinRetentionWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Per-partition-root in-process locks guarding metadata mutation (pin refresh and retention
    /// sweeps) so a concurrent <see cref="PutBytes"/>/<see cref="Read"/>/<see cref="EnforceRetention"/>
    /// in the same process can't race on the same entry's sidecar or the retention status file.
    /// Keyed by the resolved partition root path. Combined with <see cref="CrossProcessPartitionLock"/>
    /// (acquired together via <see cref="AcquirePartitionLock"/>) for the cross-process half of the
    /// same guarantee.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> PartitionLocks = new(StringComparer.Ordinal);

    /// <summary>Name of the advisory cross-process lock file created inside each partition root. Internal for test fixtures that need to open/flock it directly to prove mutual exclusion deterministically.</summary>
    internal const string CrossProcessLockFileName = ".partition.lock";

    private readonly CacheOptions _cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));

    /// <summary>
    /// Test-only hook invoked synchronously immediately after a retention sweep captures its entry
    /// snapshot (pins/sizes/last-access), while this instance's in-process AND cross-process locks
    /// for the root are both still held. Lets a regression deterministically drive a simulated
    /// concurrent renewal from another thread/handle at exactly the historical TOCTOU window,
    /// without arbitrary sleeps — the point is that such a renewal can no longer actually interleave
    /// here (the locks are held), so the hook exists purely to let a test *attempt* one and observe
    /// it block/queue correctly.
    /// </summary>
    internal Action? RetentionSnapshotCapturedForTesting;

    private static object LockFor(string root) => PartitionLocks.GetOrAdd(root, static _ => new object());

    /// <summary>
    /// Acquires both the in-process monitor and the cross-process advisory lock for
    /// <paramref name="root"/> as one unit, released together on <see cref="IDisposable.Dispose"/>
    /// in the correct (reverse) order. Must be acquired at most once per logical operation on a
    /// given thread — nested acquisition (e.g. a method that already holds it calling another
    /// method that tries to acquire it again) would deadlock on POSIX, since <c>flock</c> excludes
    /// a second independent file descriptor from the same process just as it would another
    /// process's. Callers that need to invoke retention logic while already holding the lock must
    /// call the lock-free <c>*Core</c> variant instead of re-entering through the public method.
    /// </summary>
    private static IDisposable AcquirePartitionLock(string root)
    {
        var monitor = LockFor(root);
        Monitor.Enter(monitor);
        try
        {
            var crossProcessLock = CrossProcessPartitionLock.Acquire(root);
            return new PartitionLockScope(monitor, crossProcessLock);
        }
        catch
        {
            Monitor.Exit(monitor);
            throw;
        }
    }

    private sealed class PartitionLockScope(object monitor, CrossProcessPartitionLock crossProcessLock) : IDisposable
    {
        public void Dispose()
        {
            crossProcessLock.Dispose();
            Monitor.Exit(monitor);
        }
    }

    private string RootFor(string cachePartition)
    {
        // Runtime derived files are intentionally outside any immutable snapshot export. In eval
        // mode GetEffectiveCacheRoot() returns the snapshot directory itself, so delivery uses a
        // genuinely separate location instead of nesting inside the read-only snapshot — a private,
        // per-user runtime directory rather than a shared, predictable OS-temp path with default
        // permissions (see ResolveDeliveryRuntimeRoot).
        var baseRoot = _cacheOptions.EvalMode
            ? ResolveDeliveryRuntimeRoot()
            : _cacheOptions.GetEffectiveCacheRoot();
        return Path.Combine(baseRoot, "delivery", CacheSecurity.SanitizeCacheKeySegment(cachePartition));
    }

    /// <summary>
    /// Env var letting an operator point the eval-mode private runtime delivery root at an
    /// explicit directory, bypassing the <c>XDG_RUNTIME_DIR</c>/OS-temp-fallback resolution below
    /// entirely. Primarily an escape hatch for the access-first fail-closed error in
    /// <see cref="EnsureSecurePrivateDir"/>: if the default resolved path can't be verified as a
    /// private, owner-only directory on this host (wrong owner, unexpected ACL, symlink, etc.),
    /// the thrown error names this variable as the way to redirect delivery to a path the caller
    /// controls and can fix up, rather than leaving evidence permanently inaccessible.
    /// </summary>
    public const string RuntimeDirOverrideEnvironmentVariable = "HLX_DELIVERY_RUNTIME_DIR";

    /// <summary>
    /// Resolves a private, owner-only eval-mode runtime delivery root instead of a fixed,
    /// world-guessable <c>{temp}/hlx-delivery-runtime</c> path: an explicit
    /// <see cref="RuntimeDirOverrideEnvironmentVariable"/> wins outright; otherwise, on Unix,
    /// prefers <c>XDG_RUNTIME_DIR</c> (already a per-user, OS-managed 0700 directory) and
    /// otherwise falls back to a per-user-named directory under the OS temp root (so two different
    /// OS users never contend for, or can plant a symlink into, the same path); on Windows, uses
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which is already per-user.
    /// Every directory level this method itself creates is additionally hardened via
    /// <see cref="EnsureSecurePrivateDir"/>.
    /// </summary>
    private static string ResolveDeliveryRuntimeRoot()
    {
        var overrideDir = Environment.GetEnvironmentVariable(RuntimeDirOverrideEnvironmentVariable);
        if (!string.IsNullOrEmpty(overrideDir))
            return EnsureSecurePrivateDir(overrideDir);

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var hlxDir = EnsureSecurePrivateDir(localAppData, "hlx");
            return EnsureSecurePrivateDir(hlxDir, "delivery-runtime");
        }

        var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdgRuntimeDir) && Directory.Exists(xdgRuntimeDir))
            return EnsureSecurePrivateDir(xdgRuntimeDir, "hlx-delivery-runtime");

        // No XDG_RUNTIME_DIR (common outside an interactive systemd session, e.g. CI/cron/SSH):
        // fall back to the shared OS temp dir, but scope to a directory named for this user, and
        // enforce owner-only permissions at both levels rather than relying on the temp dir's
        // default (often world-traversable) permissions.
        var userTag = CacheSecurity.SanitizeCacheKeySegment(Environment.UserName);
        var userDir = EnsureSecurePrivateDir(Path.GetTempPath(), $"hlx-delivery-{userTag}");
        return EnsureSecurePrivateDir(userDir, "hlx-delivery-runtime");
    }

    /// <summary>
    /// Ensures <paramref name="trustedBase"/>/<paramref name="segment"/> is a private, owner-only
    /// directory. This is now a thin wrapper around the thorough single-argument
    /// <see cref="EnsureSecurePrivateDir(string)"/>: <paramref name="trustedBase"/> is no longer
    /// exempt from verification — an environment-selected base (<c>XDG_RUNTIME_DIR</c>, the OS temp
    /// root) can itself be a symlink, foreign-writable, or have its ancestry replaced, and all of
    /// that must still be caught. The only exemption is a small, fixed allowlist of known OS-level
    /// alias symlinks resolved once via <see cref="TryGetKnownSystemAlias"/> (e.g. macOS's
    /// <c>/var</c> -&gt; <c>/private/var</c>) — not a blanket trust of the caller-selected base.
    /// </summary>
    private static string EnsureSecurePrivateDir(string trustedBase, string segment)
    {
        VerifyTrustedBaseDirectory(trustedBase);
        return EnsureSecurePrivateDir(Path.Combine(trustedBase, segment));
    }

    /// <summary>
    /// Validates an environment/OS-selected base directory (e.g. <c>XDG_RUNTIME_DIR</c>, the OS
    /// temp root) before any segment is created beneath it — this is no longer a blanket-trusted
    /// value. Checks its ancestry for symlink substitution (known OS aliases like macOS's
    /// <c>/var</c> still exempted), then the base itself:
    /// <list type="bullet">
    /// <item>Unix: a plain owner-only (0700) directory is always accepted. A directory that grants
    /// group/other write access is accepted only if it also carries the sticky bit (the standard
    /// shared-temp-directory pattern, e.g. <c>/tmp</c> at 1777) — the sticky bit is what actually
    /// prevents another local user from deleting/replacing an entry this process creates inside it,
    /// so a group/other-writable base *without* it is rejected outright rather than silently relied
    /// upon. Ownership must be the current user or root (a root-owned shared system temp dir);
    /// any extra ACL grant is rejected regardless of mode/sticky bit.</item>
    /// <item>Windows: the effective DACL must not grant any principal beyond current user/SYSTEM/
    /// Administrators (reuses <see cref="WindowsDirectorySecurity.VerifyDacl"/>); ownership is not
    /// required to be the current user, since <c>LocalApplicationData</c>'s parent chain is
    /// OS-managed and may be created by a system installer.</item>
    /// </list>
    /// </summary>
    private static void VerifyTrustedBaseDirectory(string trustedBase)
    {
        var full = Path.GetFullPath(trustedBase);
        foreach (var prefix in EnumerateAncestorPrefixesTopDown(full))
            VerifyAncestorSafe(prefix);

        if (!Directory.Exists(full))
        {
            throw new InvalidOperationException(
                $"Refusing to use '{full}' as a delivery base directory: it does not exist. " +
                $"Set {RuntimeDirOverrideEnvironmentVariable} to a directory that exists, and retry.");
        }

        // VerifyAncestorSafe(full) above already covered symlink + group/other-write-without-sticky
        // for this exact path (it's included in its own ancestor-prefix enumeration). On macOS, ACL
        // inspection is a cheap native syscall (no subprocess) and runs unconditionally below rather
        // than behind the cache, so it can't go stale relative to the push-invalidation event
        // dispatch; owner (and, on Linux, ACL) are gated by the same push-invalidated cache as
        // VerifyExistingSecureDir, so a hot path re-resolving this base on every operation (e.g.
        // eval-mode's RootFor()) doesn't re-launch a subprocess each time.
        if (!OperatingSystem.IsWindows() && OperatingSystem.IsMacOS() && UnixDirectorySecurity.HasExtraAcl(full))
        {
            throw new InvalidOperationException(
                $"Refusing to use '{full}' as a delivery base directory: it has additional ACL entries beyond its POSIX mode " +
                $"bits. Set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        if (VerifiedClean.ContainsKey(full))
            return;

        if (OperatingSystem.IsWindows())
        {
            if (!WindowsDirectorySecurity.VerifyDacl(full))
            {
                throw new InvalidOperationException(
                    $"Refusing to use '{full}' as a delivery base directory: its access control list grants access (explicit " +
                    $"or inherited) to a principal other than the current user, SYSTEM, or Administrators. Set " +
                    $"{RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
            }
            MarkVerifiedClean(full);
            return;
        }

        var ownerInfo = UnixDirectorySecurity.TryGetOwnerUidAndChangeTime(full);
        if (ownerInfo is null)
        {
            throw new InvalidOperationException(
                $"Refusing to use '{full}' as a delivery base directory: its owner uid could not be verified (missing `stat`?). " +
                $"Set {RuntimeDirOverrideEnvironmentVariable} to a directory on a filesystem/host where ownership can be checked, and retry.");
        }
        var effectiveUid = UnixDirectorySecurity.GetEffectiveUserId();
        if (ownerInfo.Value.Uid != 0 && ownerInfo.Value.Uid != effectiveUid)
        {
            throw new InvalidOperationException(
                $"Refusing to use '{full}' as a delivery base directory: it is owned by uid {ownerInfo.Value.Uid}, neither " +
                $"root nor the current effective user (uid {effectiveUid}). Set {RuntimeDirOverrideEnvironmentVariable} to a " +
                "different directory, and retry.");
        }

        if (!OperatingSystem.IsMacOS() && UnixDirectorySecurity.HasExtraAcl(full))
        {
            throw new InvalidOperationException(
                $"Refusing to use '{full}' as a delivery base directory: it has additional ACL entries beyond its POSIX mode " +
                $"bits. Set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        MarkVerifiedClean(full);
    }

    /// <summary>
    /// Ensures <paramref name="path"/> is a private, owner-only directory before any delivery
    /// content is written beneath it, and that it was actually created/is actually controlled by
    /// the current user rather than merely having matching permission bits. Checks, in order:
    /// (1) the entry is not a symlink/reparse point (no-follow — uses <see cref="DirectoryInfo.LinkTarget"/>,
    /// which stats without following the final component), so a predictable eval-mode temp path
    /// can't be preempted by an attacker-planted symlink that redirects writes elsewhere — except a
    /// small fixed allowlist of known OS alias symlinks (see <see cref="TryGetKnownSystemAlias"/>);
    /// (2) on Unix, the actual numeric owner uid (via <see cref="UnixDirectorySecurity.TryGetOwnerUidAndChangeTime"/>,
    /// not just permission bits) equals <see cref="UnixDirectorySecurity.GetEffectiveUserId"/>, and
    /// the mode is exactly owner-only (0700) with no extra ACL grant
    /// (<see cref="UnixDirectorySecurity.HasExtraAcl"/> — mode bits alone can't see macOS ACLs);
    /// (3) on Windows, the directory's owner SID matches the current user AND its effective DACL
    /// grants Allow access only to the current user/SYSTEM/Administrators
    /// (<see cref="WindowsDirectorySecurity.VerifyOwner"/>/<see cref="WindowsDirectorySecurity.VerifyDacl"/>).
    /// Every check fails closed: on any failure this throws rather than proceeding, with an
    /// actionable message naming <see cref="RuntimeDirOverrideEnvironmentVariable"/> as the way to
    /// redirect delivery to a different, caller-controlled directory. A directory this method
    /// itself creates is created atomically with the restrictive mode/DACL already applied (no
    /// separate create-then-restrict step), so there is no window where it exists with
    /// broader-than-intended access. The expensive (subprocess-based) parts of step (2) are skipped
    /// when a per-path identity cache proves nothing has changed since the last full verification —
    /// see <see cref="VerifyExistingSecureDir"/>.
    /// </summary>
    private static string EnsureSecurePrivateDir(string path)
    {
        var full = Path.GetFullPath(path);

        // Check every ancestor path prefix — from the filesystem root down to the full target —
        // for being a symlink before creating or trusting anything, not just the final component.
        // Directory.Exists(full) can be true purely because an *earlier* path segment is a symlink
        // into a directory that happens to contain the rest of the requested chain (e.g. a symlink
        // "link" -> "target", where "target/nested" already exists for unrelated reasons: naively
        // checking only the final "link/nested" component would see it "exists" as a plain
        // directory and never notice "link" itself is a symlink). Each prefix string is checked via
        // its own (non-following) symlink status, so an attacker-planted symlink anywhere in the
        // chain is rejected before any descendant is created — unless it matches a known, verified
        // OS-level alias (e.g. macOS's /var -> /private/var).
        foreach (var prefix in EnumerateAncestorPrefixesTopDown(full))
            VerifyAncestorSafe(prefix);

        if (Directory.Exists(full))
        {
            VerifyExistingSecureDir(full);
            return full;
        }

        if (File.Exists(full))
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{full}': a file already exists at that path. " +
                $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        // Create every missing ancestor top-down (parent before child), hardening each one this
        // method itself creates — a pre-existing ancestor found along the way (e.g. the OS-managed
        // XDG_RUNTIME_DIR/LocalAppData root, or a plain directory from an unrelated prior run) is
        // left alone beyond the symlink check above; only directories genuinely missing are ours to
        // create and fully verify.
        foreach (var component in EnumerateAncestorPrefixesTopDown(full))
        {
            if (Directory.Exists(component))
                continue;
            if (File.Exists(component))
            {
                throw new InvalidOperationException(
                    $"Refusing to use delivery runtime directory '{full}': a file already exists at '{component}'. " +
                    $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
            }
            SuppressSelfTriggeredInvalidation(component); // In case creating this invalidates an already-watched ancestor (e.g. the OS temp root).
            try
            {
                CreateSecureDir(component);
            }
            catch (Exception ex) when ((ex is InvalidOperationException or IOException) && Directory.Exists(component))
            {
                // Lost a race with another thread/process that created this exact directory
                // between our existence check and our own creation attempt (e.g. concurrent
                // operations all resolving the same shared eval-mode runtime root for the first
                // time) — benign, since VerifyExistingSecureDir below still fully (fail-closed)
                // verifies whatever now exists there, same as the normal reuse path.
            }
            VerifyExistingSecureDir(component);
        }

        return full;
    }

    /// <summary>
    /// Pure, no-filesystem-IO lexical table of known OS-level alias symlinks: the expected real
    /// target each alias path resolves to, when legitimate. Used by <see cref="TryGetKnownSystemAlias"/>
    /// as the first (cheap) half of alias recognition; the second half (actually resolving the
    /// symlink and confirming the real target is root-owned) does touch the filesystem and is not
    /// exposed here, so this table alone is safe for direct unit testing without real symlinks.
    /// Intentionally small and hardcoded — this is an allowlist of specific, well-known OS behavior
    /// (macOS historically symlinks several top-level directories into <c>/private</c>), not a
    /// general mechanism for trusting arbitrary ancestry.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownSystemAliases =
        OperatingSystem.IsMacOS()
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/var"] = "/private/var",
                ["/tmp"] = "/private/tmp",
                ["/etc"] = "/private/etc",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Lexical-only lookup: true when <paramref name="path"/> matches a known OS alias symlink path
    /// exactly, with <paramref name="expectedRealPath"/> set to its documented real target. Does
    /// <b>not</b> touch the filesystem — callers must still verify the symlink actually resolves to
    /// that target and that the target is root-owned (see call site in <see cref="RejectIfSymlink"/>)
    /// before trusting it; this half only answers "is this a path we might recognize at all."
    /// </summary>
    internal static bool TryGetKnownSystemAlias(string path, out string expectedRealPath) =>
        KnownSystemAliases.TryGetValue(path, out expectedRealPath!);

    /// <summary>
    /// Per-process cache of alias verification results (path -> verified-safe), so the one-time
    /// filesystem/subprocess cost of resolving+verifying a known alias (e.g. confirming <c>/var</c>
    /// really does resolve to a root-owned <c>/private/var</c> on this host) is paid at most once.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> VerifiedSystemAliases = new(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="path"/> is both (a) lexically a known OS alias path and (b) that
    /// alias's symlink actually resolves to its documented real target, which is itself root-owned
    /// — the full, filesystem-touching verification that <see cref="RejectIfSymlink"/> requires
    /// before exempting a symlinked ancestor from rejection. Cached per path after the first check.
    /// </summary>
    private static bool IsVerifiedSystemAlias(string path)
    {
        if (VerifiedSystemAliases.TryGetValue(path, out var cached))
            return cached;

        var verified = false;
        if (TryGetKnownSystemAlias(path, out var expectedRealPath))
        {
            var info = new DirectoryInfo(path);
            var resolved = info.LinkTarget is not null ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName : null;
            if (string.Equals(resolved, expectedRealPath, StringComparison.Ordinal))
            {
                var ownerInfo = UnixDirectorySecurity.TryGetOwnerUidAndChangeTime(expectedRealPath);
                verified = ownerInfo?.Uid == 0; // root-owned: not attacker-writable on a normally-administered host.
            }
        }

        VerifiedSystemAliases[path] = verified;
        return verified;
    }

    /// <summary>All path prefixes of <paramref name="fullPath"/> from the filesystem/drive root down to the full path itself, in top-down (parent-before-child) order.</summary>
    private static IEnumerable<string> EnumerateAncestorPrefixesTopDown(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        var segments = remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var segment in segments)
        {
            current = string.IsNullOrEmpty(current) ? segment : Path.Combine(current, segment);
            yield return current;
        }
    }

    /// <summary>
    /// Validates one ancestor path prefix before it's trusted: (1) not a symlink/reparse point,
    /// unless it's a verified known OS-level alias (see <see cref="IsVerifiedSystemAlias"/>); (2)
    /// on Unix, if it grants group/other write access, it also carries the sticky bit — otherwise
    /// another local user with write access to this ancestor could delete/replace a deeper
    /// ancestor or the final target directory itself, regardless of how restrictive that deeper
    /// directory's own permissions are (the "parent replacement" attack: owning a 0700 leaf is
    /// worthless if someone else can unlink/rename it out from under you). A no-op for a path with
    /// nothing at it yet (not yet created).
    /// </summary>
    private static void VerifyAncestorSafe(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes != (FileAttributes)(-1) && (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint))
        {
            if (!OperatingSystem.IsWindows() && IsVerifiedSystemAlias(path))
                return;

            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory: ancestor path '{path}' is a symbolic link or reparse point, not a plain directory. " +
                $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        if (OperatingSystem.IsWindows() || !Directory.Exists(path))
            return;

        var mode = File.GetUnixFileMode(path);
        var grantsGroupOrOtherWrite = (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
        var hasStickyBit = (mode & UnixFileMode.StickyBit) != 0;
        if (grantsGroupOrOtherWrite && !hasStickyBit)
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory: ancestor path '{path}' grants group/other write access ('{mode}') " +
                "without the sticky bit, so another local user could delete/replace a directory inside it (including a deeper " +
                $"ancestor or the final target) before this process notices. Remove it (or chmod 700 it), add the sticky bit " +
                $"(e.g. chmod +t) if it must remain shared, or set {RuntimeDirOverrideEnvironmentVariable} to a different " +
                "directory, and retry.");
        }
    }

    /// <summary>Atomically creates <paramref name="path"/> with the restrictive mode/DACL already applied at creation time.</summary>
    private static void CreateSecureDir(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsDirectorySecurity.CreateForCurrentUserOnly(path);
            return;
        }

        UnixDirectorySecurity.CreateOwnerOnlyDirectory(path);
        // Clamp to exactly owner-only regardless of umask: mkdir()'s requested mode can only be
        // narrowed (never widened) by umask, so this can only tighten, never loosen, permissions.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Per-path push-invalidated cache for the expensive (subprocess-based) half of
    /// <see cref="VerifyExistingSecureDir"/> — owner-uid and ACL verification. A path's entry
    /// stays valid (skipping both subprocess checks entirely on later calls) until a real OS
    /// change notification (<see cref="FileSystemWatcher"/>, backed by inotify/FSEvents/
    /// ReadDirectoryChangesW) reports that the directory's own attributes/security metadata
    /// changed — not a polled timestamp. This is what actually satisfies both "run the expensive
    /// checks once and cache the verified state" and "catch an in-place ACL/ownership change":
    /// polling any BCL-exposed timestamp (creation time, last-write time) cannot see a <c>chmod
    /// +a</c>-only change (it touches neither), and even the real <c>ctime</c> (available only via
    /// a subprocess stat call) has at best whole-second resolution on BSD/macOS, which still misses
    /// a change within the same second as the last check — a live kernel notification has neither
    /// limitation and requires zero subprocess calls on the hot path.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> VerifiedClean = new(StringComparer.Ordinal);

    /// <summary>Active watcher per verified path, keeping <see cref="VerifiedClean"/> accurate for as long as this process runs. Never explicitly disposed: the number of distinct delivery roots in one process is small and bounded (a handful of cache partitions), so this is not a meaningful resource leak.</summary>
    private static readonly ConcurrentDictionary<string, FileSystemWatcher> IdentityWatchers = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks <paramref name="path"/> as verified-clean and starts (if not already running) a
    /// <see cref="FileSystemWatcher"/> on its parent directory, filtered to that exact entry, that
    /// evicts the cache entry the instant the OS reports an attribute/security change — so the next
    /// call re-runs the full subprocess-based check instead of trusting stale state.
    /// </summary>
    /// <summary>
    /// Suppresses the next <c>Changed</c>/<c>Created</c> watcher invalidation for a path this
    /// process is about to cause itself (e.g. creating a child directory beneath an already-verified
    /// watched root) — set by <see cref="SuppressSelfTriggeredInvalidation"/> immediately before such
    /// an operation. Some platform <see cref="FileSystemWatcher"/> backends (notably macOS/FSEvents)
    /// report a directory's own change notification at coarser granularity than the requested
    /// <see cref="NotifyFilters"/>, so creating an entry *inside* a watched directory can spuriously
    /// fire a watched-directory-level event even though none of its own attributes/security/
    /// creation-time actually changed — without this, that self-inflicted event would force an
    /// unnecessary extra full re-verification pass once per initial child-creation burst. Deletions
    /// and renames are never suppressed (always treated as a potential real external change).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> SuppressedInvalidations = new(StringComparer.Ordinal);

    /// <summary>Marks the next <c>Changed</c>/<c>Created</c> watcher event for any watched ancestor of <paramref name="insidePath"/> as self-triggered (ignorable) — call immediately before creating a file/directory that lands inside an already-verified, watched path.</summary>
    private static void SuppressSelfTriggeredInvalidation(string insidePath)
    {
        foreach (var prefix in EnumerateAncestorPrefixesTopDown(Path.GetFullPath(insidePath)))
        {
            if (IdentityWatchers.ContainsKey(prefix))
                SuppressedInvalidations[prefix] = true;
        }
    }

    private static void MarkVerifiedClean(string path)
    {
        VerifiedClean[path] = true;
        IdentityWatchers.GetOrAdd(path, static p =>
        {
            var parent = Path.GetDirectoryName(p);
            var leaf = Path.GetFileName(p);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
                return CreateNoOpWatcher(); // Nothing sensible to watch — fail toward re-checking every call rather than throwing here.

            var watcher = new FileSystemWatcher(parent, leaf)
            {
                NotifyFilter = NotifyFilters.Attributes | NotifyFilters.Security | NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };
            void Invalidate(object sender, FileSystemEventArgs e)
            {
                if (SuppressedInvalidations.TryRemove(p, out _))
                    return; // Expected self-triggered event from our own child creation under this path.
                VerifiedClean.TryRemove(p, out _);
            }
            void InvalidateOnError(object sender, ErrorEventArgs e) => VerifiedClean.TryRemove(p, out _);
            watcher.Changed += Invalidate;
            watcher.Created += Invalidate;
            watcher.Deleted += (_, _) => VerifiedClean.TryRemove(p, out _);
            watcher.Renamed += (_, _) => VerifiedClean.TryRemove(p, out _);
            watcher.Error += InvalidateOnError;
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch (IOException)
            {
                // Watch limits exhausted or similar — fall back to "never cached", which is safe
                // (just loses the performance benefit for this one path, not the correctness check).
                watcher.Dispose();
                return CreateNoOpWatcher();
            }
            return watcher;
        });
    }

    /// <summary>A disabled placeholder watcher for the rare case a real watch couldn't be established — its presence in <see cref="IdentityWatchers"/> just means "don't try again," while <see cref="VerifiedClean"/> is deliberately never populated for that path, so every call falls through to the full check.</summary>
    private static FileSystemWatcher CreateNoOpWatcher() => new() { EnableRaisingEvents = false };

    /// <summary>Fail-closed verification of a directory this process either just created or is reusing from a prior run — see <see cref="EnsureSecurePrivateDir"/> for the full rationale.</summary>
    private static void VerifyExistingSecureDir(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': it is a symbolic link or reparse point, not a plain directory. " +
                $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        if (OperatingSystem.IsWindows())
        {
            if (VerifiedClean.ContainsKey(path))
                return;

            if (!WindowsDirectorySecurity.VerifyOwner(path))
            {
                throw new InvalidOperationException(
                    $"Refusing to use delivery runtime directory '{path}': its owner does not match the current user. " +
                    $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
            }
            if (!WindowsDirectorySecurity.VerifyDacl(path))
            {
                throw new InvalidOperationException(
                    $"Refusing to use delivery runtime directory '{path}': its access control list grants access (explicit or " +
                    "inherited) to a principal other than the current user, SYSTEM, or Administrators — owner match alone does " +
                    $"not prove privacy. Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
            }
            MarkVerifiedClean(path);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (mode != ownerOnly)
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': it has permissions '{mode}' instead of owner-only (0700). " +
                $"Remove it (or chmod 700 it), or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        // On macOS, ACL inspection is a direct native syscall (no subprocess — see
        // UnixDirectorySecurity.HasExtraAcl), cheap enough to run unconditionally on every call
        // rather than behind the identity cache below: that's what actually closes the
        // grant-then-recheck race a push-notification-invalidated cache cannot (event dispatch is
        // asynchronous; a live syscall is not). On Linux, ACL inspection remains subprocess-based
        // and is gated by the same cache as the owner check, further down.
        if (OperatingSystem.IsMacOS() && UnixDirectorySecurity.HasExtraAcl(path))
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': it has additional ACL entries beyond owner-only POSIX " +
                "permissions (mode bits alone can't see ACL grants on filesystems/platforms — e.g. macOS — where an ACL can " +
                $"grant access that mode bits don't show). Remove the ACL (e.g. `chmod -N` on macOS), or set " +
                $"{RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        if (VerifiedClean.ContainsKey(path))
            return; // Owner (and, on Linux, ACL) already verified, with no attribute/security change notification since.

        var ownerUid = UnixDirectorySecurity.TryGetOwnerUidAndChangeTime(path)?.Uid;
        var effectiveUid = UnixDirectorySecurity.GetEffectiveUserId();
        if (ownerUid is null)
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': its owner uid could not be verified (missing `stat`?). " +
                $"Set {RuntimeDirOverrideEnvironmentVariable} to a directory on a filesystem/host where ownership can be checked, and retry.");
        }
        if (ownerUid.Value != effectiveUid)
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': it is owned by uid {ownerUid.Value}, not the current " +
                $"effective user (uid {effectiveUid}), so it may not be controlled by this user. " +
                $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        if (!OperatingSystem.IsMacOS() && UnixDirectorySecurity.HasExtraAcl(path))
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': it has additional ACL entries beyond owner-only POSIX " +
                "permissions (mode bits alone can't see ACL grants on filesystems/platforms — e.g. macOS — where an ACL can " +
                $"grant access that mode bits don't show). Remove the ACL (e.g. `chmod -N` on macOS), or set " +
                $"{RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        MarkVerifiedClean(path);
    }

    public EvidenceDescriptor PutText(
        string content,
        string mediaType,
        string? sourceFingerprint,
        string? backingCacheKey,
        string cachePartition)
    {
        ArgumentNullException.ThrowIfNull(content);
        return PutBytes(Encoding.UTF8.GetBytes(content), mediaType, sourceFingerprint, backingCacheKey, cachePartition);
    }

    public EvidenceDescriptor PutBytes(
        ReadOnlySpan<byte> content,
        string mediaType,
        string? sourceFingerprint,
        string? backingCacheKey,
        string cachePartition)
    {
        var root = RootFor(cachePartition);
        HardenDeliveryDirectory(root);
        SuppressSelfTriggeredInvalidation(root); // About to write .bin/.meta.json/.retention-status.json inside root.

        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        // Content-addressed id (hash of bytes + media type, not a random GUID): identical payloads
        // materialized again under this partition resolve to the same id/file instead of growing
        // the delivery directory without bound.
        var evidenceId = ComputeContentAddressedId(content, mediaType);
        var dataPath = Path.Combine(root, $"{evidenceId}.bin");
        var metaPath = Path.Combine(root, $"{evidenceId}.meta.json");
        var now = DateTimeOffset.UtcNow;

        using (AcquirePartitionLock(root))
        {
            bool complete;
            EvidenceDescriptor descriptor;
            if (File.Exists(dataPath) && File.Exists(metaPath) && new FileInfo(dataPath).Length == content.Length)
            {
                // Dedupe: bytes already materialized (same content hash + length) — skip rewriting
                // them, but refresh the lease (not just last-access) so a hot id's protection
                // window keeps sliding forward instead of expiring under a separate competing copy.
                complete = true;
                File.SetLastAccessTimeUtc(dataPath, now.UtcDateTime);
                var existing = ReadDescriptor(metaPath);
                descriptor = (existing ?? BuildDescriptor(evidenceId, cachePartition, dataPath, mediaType, sha256, content.Length, complete, sourceFingerprint, backingCacheKey, now))
                    with
                { PinnedUntilUtc = now + MinRetentionWindow };
            }
            else
            {
                complete = WriteAndVerify(dataPath, content, sha256);
                descriptor = BuildDescriptor(evidenceId, cachePartition, dataPath, mediaType, sha256, content.Length, complete, sourceFingerprint, backingCacheKey, now)
                    with
                { PinnedUntilUtc = now + MinRetentionWindow };
            }

            PersistDescriptor(metaPath, descriptor);
            HardenDeliveryFile(dataPath);
            HardenDeliveryFile(metaPath);
            EnforceRetentionCore(root);
            return descriptor;
        }
    }

    private static EvidenceDescriptor BuildDescriptor(
        string evidenceId,
        string cachePartition,
        string dataPath,
        string mediaType,
        string sha256,
        long length,
        bool complete,
        string? sourceFingerprint,
        string? backingCacheKey,
        DateTimeOffset now) => new()
        {
            EvidenceId = evidenceId,
            ResourceUri = $"hlx-evidence://{cachePartition}/{evidenceId}",
            LocalPath = dataPath,
            MediaType = mediaType,
            Encoding = "binary",
            Bytes = length,
            Sha256 = sha256,
            Complete = complete,
            SourceFingerprint = sourceFingerprint,
            BackingCacheKey = backingCacheKey,
            CapturedAt = now,
            CachePartition = cachePartition
        };

    public EvidenceDescriptor? TryGetDescriptor(string evidenceId, string cachePartition)
    {
        var root = RootFor(cachePartition);
        if (!Directory.Exists(root))
            return null; // Nothing ever materialized here — avoid creating the root/lock file for a bare lookup.

        var metaPath = Path.Combine(root, $"{SanitizeId(evidenceId)}.meta.json");
        using (AcquirePartitionLock(root))
        {
            var descriptor = ReadDescriptor(metaPath);
            if (descriptor is null)
                return null;

            // A lookup is itself a hand-out: a caller may hold on to only this descriptor and read
            // the referenced file directly, so it must get the same minimum protection window as
            // Put/Read.
            var refreshed = descriptor with { PinnedUntilUtc = DateTimeOffset.UtcNow + MinRetentionWindow };
            PersistDescriptor(metaPath, refreshed);
            return refreshed;
        }
    }

    private static EvidenceDescriptor? ReadDescriptor(string metaPath)
    {
        if (!File.Exists(metaPath))
            return null;
        return JsonSerializer.Deserialize<EvidenceDescriptor>(File.ReadAllText(metaPath), DescriptorJsonOptions);
    }

    /// <summary>Writes <paramref name="descriptor"/> to <paramref name="metaPath"/> via a temp-file-plus-rename so a concurrent reader (another request's retention sweep or lookup) never observes a torn/partial write.</summary>
    private static void PersistDescriptor(string metaPath, EvidenceDescriptor descriptor)
    {
        var tmpPath = $"{metaPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(descriptor, DescriptorJsonOptions));
        HardenDeliveryFile(tmpPath);
        File.Move(tmpPath, metaPath, overwrite: true);
    }

    /// <summary>
    /// Best-effort owner-only hardening for a delivery partition directory and its immediate
    /// "delivery" parent. Unlike <see cref="EnsureSecurePrivateDir"/> (fail-closed on wrong
    /// mode/owner/ACL, used only for the predictable eval-mode runtime root), this tightens
    /// permissions in place under the already-private cache root rather than refusing reuse over a
    /// loose legacy mode, so pre-upgrade directories are not bricked. A symlinked "delivery" or
    /// partition directory is always rejected outright, though (never tightened/reused) — that is a
    /// hard security boundary regardless of upgrade compatibility, since it would otherwise let
    /// evidence be written somewhere outside this cache partition entirely.
    /// </summary>
    private static void HardenDeliveryDirectory(string root)
    {
        var deliveryDir = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException($"Delivery partition root '{root}' has no parent directory.");

        foreach (var component in new[] { deliveryDir, root })
        {
            VerifyAncestorSafe(component);
            if (!Directory.Exists(component))
            {
                SuppressSelfTriggeredInvalidation(component);
                Directory.CreateDirectory(component);
            }
            if (OperatingSystem.IsWindows())
                continue;
            try { File.SetUnixFileMode(component, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (IOException) { /* best-effort: e.g. read-only mount, or not owned by this user */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    /// <summary>Best-effort owner-only (0600) hardening for one delivery data/sidecar file.</summary>
    private static void HardenDeliveryFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }


    public EvidenceReadResult Read(
        string evidenceId,
        long offsetBytes,
        long lengthBytes,
        string encoding,
        string cachePartition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);

        var descriptor = TryGetDescriptor(evidenceId, cachePartition)
            ?? throw new EvidenceNotFoundException(evidenceId);

        var dataPath = descriptor.LocalPath;
        if (!File.Exists(dataPath))
            throw new EvidenceNotFoundException(evidenceId);

        // TryGetDescriptor above already refreshed this entry's retention pin/lease (a read is a
        // hand-out too), so it is protected from eviction for at least MinRetentionWindow from this
        // call regardless of total partition size. The last-access touch below is additional
        // best-effort protection against a concurrent same-process sweep observing a stale
        // timestamp mid-read; it is not itself the eviction-safety mechanism anymore.
        try { File.SetLastAccessTimeUtc(dataPath, DateTime.UtcNow); } catch (IOException) { /* best-effort */ }

        using var stream = File.OpenRead(dataPath);
        var totalBytes = stream.Length;
        var effectiveEncoding = ResolveEncoding(encoding, descriptor.MediaType);

        if (effectiveEncoding == "auto-binary")
        {
            // Binary auto mode offers a delivery/recovery action rather than an unsolicited whole
            // base64 dump; the caller must explicitly request encoding="base64" to read bytes.
            return new EvidenceReadResult
            {
                EvidenceId = evidenceId,
                Encoding = "auto",
                Text = null,
                Data = null,
                OffsetBytes = offsetBytes,
                ReturnedBytes = 0,
                TotalBytes = totalBytes,
                Sha256 = descriptor.Sha256,
                SourceComplete = descriptor.Complete,
                RangeComplete = false,
                Next = new EvidenceContinuation
                {
                    Arguments = new Dictionary<string, object?>
                    {
                        ["evidenceId"] = evidenceId,
                        ["offsetBytes"] = offsetBytes,
                        ["lengthBytes"] = lengthBytes,
                        ["encoding"] = "base64"
                    }
                },
                Delivery = descriptor
            };
        }

        if (offsetBytes >= totalBytes)
        {
            return BuildEmptyResult(descriptor, offsetBytes, totalBytes, effectiveEncoding);
        }

        var remaining = totalBytes - offsetBytes;
        var toRead = (int)Math.Min(lengthBytes, remaining);

        if (effectiveEncoding == "utf8")
        {
            // Advance by whole UTF-8 scalars: decode forward from offsetBytes, accumulating
            // complete scalars until at least lengthBytes have been consumed, then include the
            // complete final scalar even if it extends past the requested length. This never
            // splits a scalar and never truncates a too-small request into a replacement character.
            //
            // Cap to `remaining` (a real, file-size-bounded value) before adding the 4-byte lookahead
            // slack — adding 4 directly to a caller-supplied lengthBytes near long.MaxValue would
            // overflow to a negative number, truncating `window` to zero/garbage and silently
            // returning zero bytes with a next.offset that never advances.
            var cappedLength = Math.Min(lengthBytes, remaining);
            var window = (int)Math.Min(remaining, cappedLength + 4);
            stream.Position = offsetBytes;
            var probe = new byte[window];
            var probeRead = stream.ReadAtLeast(probe, window, throwOnEndOfStream: false);
            var consumed = 0;
            var i = 0;
            while (i < probeRead)
            {
                var seqLen = Utf8SequenceLength(probe[i]);
                if (seqLen <= 0 || i + seqLen > probeRead)
                    break; // malformed lead byte or sequence truncated at the read window's edge
                if (consumed >= lengthBytes)
                    break;
                consumed += seqLen;
                i += seqLen;
            }
            toRead = consumed > 0 ? consumed : Math.Min(probeRead, toRead);
        }

        stream.Position = offsetBytes;
        var buffer = new byte[toRead];
        var read = stream.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);
        var chunk = buffer.AsSpan(0, read).ToArray();

        var rangeComplete = (offsetBytes + read) >= totalBytes || read >= lengthBytes;
        var nextOffset = offsetBytes + read;
        var hasNext = nextOffset < totalBytes;

        return new EvidenceReadResult
        {
            EvidenceId = evidenceId,
            Encoding = effectiveEncoding,
            Text = effectiveEncoding == "utf8" ? Encoding.UTF8.GetString(chunk) : null,
            Data = effectiveEncoding == "base64" ? Convert.ToBase64String(chunk) : null,
            OffsetBytes = offsetBytes,
            ReturnedBytes = read,
            TotalBytes = totalBytes,
            Sha256 = descriptor.Sha256,
            SourceComplete = descriptor.Complete,
            RangeComplete = rangeComplete,
            Next = hasNext
                ? new EvidenceContinuation
                {
                    Arguments = new Dictionary<string, object?>
                    {
                        ["evidenceId"] = evidenceId,
                        ["offsetBytes"] = nextOffset,
                        ["lengthBytes"] = lengthBytes,
                        ["encoding"] = encoding
                    }
                }
                : null,
            Delivery = descriptor
        };
    }

    private static EvidenceReadResult BuildEmptyResult(EvidenceDescriptor descriptor, long offsetBytes, long totalBytes, string effectiveEncoding) => new()
    {
        EvidenceId = descriptor.EvidenceId,
        Encoding = effectiveEncoding,
        Text = effectiveEncoding == "utf8" ? "" : null,
        Data = effectiveEncoding == "base64" ? "" : null,
        OffsetBytes = offsetBytes,
        ReturnedBytes = 0,
        TotalBytes = totalBytes,
        Sha256 = descriptor.Sha256,
        SourceComplete = descriptor.Complete,
        RangeComplete = true,
        Next = null,
        Delivery = descriptor
    };

    private static string ResolveEncoding(string requested, string mediaType) => requested switch
    {
        "utf8" => "utf8",
        "base64" => "base64",
        _ => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
             || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            ? "utf8"
            : "auto-binary"
    };

    private static string SanitizeId(string evidenceId) => CacheSecurity.SanitizeCacheKeySegment(evidenceId);

    /// <summary>UTF-8 lead-byte sequence length (1-4), or -1 for a continuation/invalid lead byte.</summary>
    private static int Utf8SequenceLength(byte leadByte)
    {
        if ((leadByte & 0b1000_0000) == 0) return 1;
        if ((leadByte & 0b1110_0000) == 0b1100_0000) return 2;
        if ((leadByte & 0b1111_0000) == 0b1110_0000) return 3;
        if ((leadByte & 0b1111_1000) == 0b1111_0000) return 4;
        return -1;
    }

    /// <summary>Content-addressed evidence id: SHA-256 over the bytes plus the claimed media type (so identically-shaped bytes under two different declared media types never collide), hex-encoded.</summary>
    private static string ComputeContentAddressedId(ReadOnlySpan<byte> content, string mediaType)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(content);
        hasher.AppendData(Encoding.UTF8.GetBytes(mediaType ?? ""));
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Writes content to <paramref name="dataPath"/> and re-reads/re-hashes it to verify the write before declaring <c>complete=true</c>, per the exact-recovery contract.</summary>
    private static bool WriteAndVerify(string dataPath, ReadOnlySpan<byte> content, string expectedSha256)
    {
        File.WriteAllBytes(dataPath, content.ToArray());
        var verifyBytes = File.ReadAllBytes(dataPath);
        var verifySha = Convert.ToHexString(SHA256.HashData(verifyBytes)).ToLowerInvariant();
        return verifyBytes.Length == content.Length && string.Equals(verifySha, expectedSha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bounded age + total-size retention sweep for one partition's delivery directory (see the
    /// class remarks for the overall policy and the lease/pin contract). Runs synchronously and
    /// opportunistically after each <see cref="PutBytes"/>, inside the same per-root lock the
    /// <summary>
    /// Bounded age + total-size retention sweep for one partition's delivery directory, acquiring
    /// this root's locks itself. Use this entry point when calling from outside an already-locked
    /// context (e.g. test reflection); <see cref="PutBytes"/> calls <see cref="EnforceRetentionCore"/>
    /// directly since it already holds both locks (re-acquiring here would deadlock — see
    /// <see cref="AcquirePartitionLock"/>).
    /// </summary>
    private void EnforceRetention(string root)
    {
        if (!Directory.Exists(root))
            return; // Nothing materialized here yet — nothing to sweep, and no root to put a lock file in.

        using var _ = AcquirePartitionLock(root);
        EnforceRetentionCore(root);
    }

    /// <summary>
    /// Bounded age + total-size retention sweep for one partition's delivery directory (see the
    /// class remarks for the overall policy and the lease/pin contract). Callers must already hold
    /// both this root's in-process and cross-process locks (see <see cref="AcquirePartitionLock"/>)
    /// — holding the cross-process lock for the *entire* snapshot-through-delete sequence below is
    /// what actually closes the historical TOCTOU window (another process renewing/publishing an
    /// entry via <see cref="PutBytes"/>/<see cref="TryGetDescriptor"/> cannot interleave while this
    /// lock is held, because those paths take the same lock first). As additional defense in depth,
    /// each candidate's persisted pin is re-read immediately before its delete call, so even a
    /// weakened or partially-bypassed lock would still not delete an entry whose sidecar was
    /// updated after the snapshot. A failed delete (most likely an in-flight <see cref="Read"/>
    /// holding the file open) is treated as "skip this entry, it is still wanted" rather than an
    /// error. The sweep's outcome (including a soft-cap overage caused entirely by pinned entries)
    /// is always persisted via <see cref="TryGetRetentionStatus"/>.
    /// </summary>
    private void EnforceRetentionCore(string root)
    {
        if (_cacheOptions.MaxSizeBytes <= 0)
            return;

        List<RetentionEntry> entries;
        try
        {
            entries = [.. Directory.EnumerateFiles(root, "*.bin")
                .Select(dataPath =>
                {
                    var info = new FileInfo(dataPath);
                    var metaPath = Path.ChangeExtension(dataPath, null) + ".meta.json";
                    var descriptor = ReadDescriptor(metaPath);
                    return new RetentionEntry(
                        dataPath,
                        metaPath,
                        info.Exists ? info.Length : 0,
                        info.Exists ? info.LastAccessTimeUtc : DateTime.MinValue,
                        descriptor?.PinnedUntilUtc ?? default);
                })];
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        RetentionSnapshotCapturedForTesting?.Invoke();

        bool TryDelete(RetentionEntry entry)
        {
            // Re-read the persisted pin immediately before deleting, under the same lock this
            // whole sweep already holds: defense in depth against the snapshot above having gone
            // stale (e.g. a longer sweep, or any future caller that captures a snapshot outside the
            // lock) — a pin renewed since the snapshot was taken must still save the entry.
            var current = ReadDescriptor(entry.MetaPath);
            if (current is not null && current.PinnedUntilUtc > DateTimeOffset.UtcNow)
                return false;

            try
            {
                if (File.Exists(entry.DataPath)) File.Delete(entry.DataPath);
                if (File.Exists(entry.MetaPath)) File.Delete(entry.MetaPath);
                return true;
            }
            catch (IOException)
            {
                // Most likely open for an in-flight hlx_read_evidence read — leave it for a
                // later sweep rather than starving that read (best-effort: this protection
                // relies on Windows share-mode locking and is not enforced on POSIX; the pin
                // above is the actual cross-platform guarantee).
                return false;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var maxAge = _cacheOptions.ArtifactMaxAge;

        var pinned = new List<RetentionEntry>();
        var unpinned = new List<RetentionEntry>();
        foreach (var entry in entries)
        {
            if (entry.PinnedUntilUtc > now)
                pinned.Add(entry);
            else
                unpinned.Add(entry);
        }

        var survivors = new List<RetentionEntry>(pinned);
        foreach (var entry in unpinned)
        {
            if (maxAge > TimeSpan.Zero && now - entry.LastAccessUtc > maxAge && TryDelete(entry))
                continue;
            survivors.Add(entry);
        }

        var totalSize = survivors.Sum(e => e.Size);
        if (totalSize > _cacheOptions.MaxSizeBytes)
        {
            // Oldest-access-first, but never touch a pinned entry: sort the whole survivor set
            // by last access and simply skip any that are pinned, rather than only iterating
            // `unpinned` — a pinned entry refreshed just now would otherwise sort last and
            // incorrectly shield an older unpinned entry from eviction by occupying a "slot".
            foreach (var entry in survivors.Where(e => e.PinnedUntilUtc <= now).OrderBy(e => e.LastAccessUtc))
            {
                if (totalSize <= _cacheOptions.MaxSizeBytes)
                    break;
                if (TryDelete(entry))
                    totalSize -= entry.Size;
            }
        }

        var finalPinnedBytes = pinned.Sum(e => e.Size);
        var capExceeded = totalSize > _cacheOptions.MaxSizeBytes;
        var status = new RetentionStatus
        {
            LastSweepUtc = now,
            TotalBytes = totalSize,
            CapBytes = _cacheOptions.MaxSizeBytes,
            CapExceeded = capExceeded,
            PinnedBytes = finalPinnedBytes,
            PinnedEntryCount = pinned.Count,
            Message = capExceeded
                ? $"Soft cap of {_cacheOptions.MaxSizeBytes} bytes exceeded ({totalSize} bytes total) because " +
                  $"{pinned.Count} pinned/leased entries ({finalPinnedBytes} bytes) are still within their " +
                  "retention protection window and were not evicted. This is expected, access-first behavior, " +
                  "not an error: pinned evidence is never deleted to free space."
                : null
        };
        PersistRetentionStatus(root, status);
    }

    private readonly record struct RetentionEntry(
        string DataPath,
        string MetaPath,
        long Size,
        DateTime LastAccessUtc,
        DateTimeOffset PinnedUntilUtc);

    private const string RetentionStatusFileName = ".retention-status.json";

    private static void PersistRetentionStatus(string root, RetentionStatus status)
    {
        try
        {
            var path = Path.Combine(root, RetentionStatusFileName);
            var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(status, DescriptorJsonOptions));
            HardenDeliveryFile(tmpPath);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort diagnostics only; never fail a Put over a status-file write race.
        }
    }

    /// <summary>
    /// Reads the most recent retention sweep outcome for <paramref name="cachePartition"/>, or null
    /// if no sweep has run yet (e.g. retention disabled via <see cref="CacheOptions.MaxSizeBytes"/>
    /// <c>&lt;= 0</c>, or nothing has been materialized in this partition). See the class remarks
    /// and <see cref="RetentionStatus.CapExceeded"/> for the access-first soft-cap-overage report.
    /// </summary>
    public RetentionStatus? TryGetRetentionStatus(string cachePartition)
    {
        var path = Path.Combine(RootFor(cachePartition), RetentionStatusFileName);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<RetentionStatus>(File.ReadAllText(path), DescriptorJsonOptions);
    }

    private static readonly JsonSerializerOptions DescriptorJsonOptions = new() { PropertyNameCaseInsensitive = true };
}
