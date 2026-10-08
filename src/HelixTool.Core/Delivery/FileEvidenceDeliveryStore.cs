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
    /// Keyed by the resolved partition root path. This is in-process only: cross-process
    /// concurrency still relies on the open-file-skip/atomic-rename best effort described above.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> PartitionLocks = new(StringComparer.Ordinal);

    private readonly CacheOptions _cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));

    private static object LockFor(string root) => PartitionLocks.GetOrAdd(root, static _ => new object());

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
    /// directory, checking/creating only that one new segment relative to <paramref name="trustedBase"/>
    /// — <paramref name="trustedBase"/> itself (an OS-provided per-user directory such as
    /// <c>XDG_RUNTIME_DIR</c>, <see cref="Environment.SpecialFolder.LocalApplicationData"/>, the OS
    /// temp root, or a previously-verified result of this same method) is trusted as given and never
    /// inspected. This is deliberately lighter than the full ancestor-to-root walk in the single-
    /// argument <see cref="EnsureSecurePrivateDir(string)"/> overload: several of those OS-provided
    /// bases legitimately contain symlinks above the level we care about (e.g. macOS's
    /// <c>/var</c> -&gt; <c>/private/var</c>, which <see cref="Path.GetTempPath"/> resolves under) —
    /// walking all the way up from such a base to the filesystem root would reject those as
    /// "attacker-planted" symlinks, which they are not. Only <paramref name="segment"/> itself is
    /// ours to create/verify.
    /// </summary>
    private static string EnsureSecurePrivateDir(string trustedBase, string segment)
    {
        var path = Path.Combine(trustedBase, segment);
        if (Directory.Exists(path))
        {
            VerifyExistingSecureDir(path);
            return path;
        }

        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': a file already exists at that path. " +
                $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }

        CreateSecureDir(path);
        VerifyExistingSecureDir(path);
        return path;
    }

    /// <summary>
    /// Ensures <paramref name="path"/> is a private, owner-only directory before any delivery
    /// content is written beneath it, and that it was actually created/is actually controlled by
    /// the current user rather than merely having matching permission bits. Checks, in order:
    /// (1) the entry is not a symlink/reparse point (no-follow — uses <see cref="DirectoryInfo.LinkTarget"/>,
    /// which stats without following the final component), so a predictable eval-mode temp path
    /// can't be preempted by an attacker-planted symlink that redirects writes elsewhere;
    /// (2) on Unix, the actual numeric owner uid (via <see cref="UnixDirectorySecurity.TryGetOwnerUid"/>,
    /// not just permission bits) equals <see cref="UnixDirectorySecurity.GetEffectiveUserId"/>, and
    /// the mode is exactly owner-only (0700) with no extra ACL grant
    /// (<see cref="UnixDirectorySecurity.HasExtraAcl"/> — mode bits alone can't see macOS ACLs);
    /// (3) on Windows, the directory's owner SID matches the current user
    /// (<see cref="WindowsDirectorySecurity.VerifyOwner"/>) — a DACL alone doesn't prove who
    /// created it. Every check fails closed: on any failure this throws rather than proceeding,
    /// with an actionable message naming <see cref="RuntimeDirOverrideEnvironmentVariable"/> as the
    /// way to redirect delivery to a different, caller-controlled directory. A directory this
    /// method itself creates is created atomically with the restrictive mode/DACL already applied
    /// (no separate create-then-restrict step), so there is no window where it exists with
    /// broader-than-intended access.
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
        // chain is rejected before any descendant is created.
        foreach (var prefix in EnumerateAncestorPrefixesTopDown(full))
            RejectIfSymlink(prefix);

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
            CreateSecureDir(component);
            VerifyExistingSecureDir(component);
        }

        return full;
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

    /// <summary>Throws if <paramref name="path"/> itself (its own path entry, not a resolved target) is a symlink/reparse point. A no-op if nothing exists at that exact path.</summary>
    private static void RejectIfSymlink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes != (FileAttributes)(-1) && (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory: ancestor path '{path}' is a symbolic link or reparse point, not a plain directory. " +
                $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
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
            if (!WindowsDirectorySecurity.VerifyOwner(path))
            {
                throw new InvalidOperationException(
                    $"Refusing to use delivery runtime directory '{path}': its owner does not match the current user. " +
                    $"Remove it, or set {RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
            }
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

        var ownerUid = UnixDirectorySecurity.TryGetOwnerUid(path);
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

        if (UnixDirectorySecurity.HasExtraAcl(path))
        {
            throw new InvalidOperationException(
                $"Refusing to use delivery runtime directory '{path}': it has additional ACL entries beyond owner-only POSIX " +
                "permissions (mode bits alone can't see ACL grants on filesystems/platforms — e.g. macOS — where an ACL can " +
                $"grant access that mode bits don't show). Remove the ACL (e.g. `chmod -N` on macOS), or set " +
                $"{RuntimeDirOverrideEnvironmentVariable} to a different directory, and retry.");
        }
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

        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        // Content-addressed id (hash of bytes + media type, not a random GUID): identical payloads
        // materialized again under this partition resolve to the same id/file instead of growing
        // the delivery directory without bound.
        var evidenceId = ComputeContentAddressedId(content, mediaType);
        var dataPath = Path.Combine(root, $"{evidenceId}.bin");
        var metaPath = Path.Combine(root, $"{evidenceId}.meta.json");
        var now = DateTimeOffset.UtcNow;

        lock (LockFor(root))
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
            EnforceRetention(root);
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
        var metaPath = Path.Combine(root, $"{SanitizeId(evidenceId)}.meta.json");
        lock (LockFor(root))
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
            RejectIfSymlink(component);
            if (!Directory.Exists(component))
                Directory.CreateDirectory(component);
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
    /// caller already holds (re-entrant). A failed delete (most likely an in-flight
    /// <see cref="Read"/> holding the file open) is treated as "skip this entry, it is still
    /// wanted" rather than an error. Entries still inside their <see cref="EvidenceDescriptor.PinnedUntilUtc"/>
    /// protection window are excluded from every eviction pass below — never a delete candidate,
    /// regardless of age or the size cap — and the sweep's outcome (including a soft-cap overage
    /// caused entirely by pinned entries) is always persisted via <see cref="TryGetRetentionStatus"/>.
    /// </summary>
    private void EnforceRetention(string root)
    {
        if (_cacheOptions.MaxSizeBytes <= 0)
            return;

        lock (LockFor(root))
        {
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

            bool TryDelete(RetentionEntry entry)
            {
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
