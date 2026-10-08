using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HelixTool.Core.Delivery;

/// <summary>Result of a bounded, trusted-executable subprocess invocation. See <see cref="UnixDirectorySecurity.RunTrusted"/>.</summary>
internal readonly record struct TrustedProcessResult(string Stdout, int ExitCode);

/// <summary>
/// P/Invoke-based Unix ownership verification and atomic owner-only directory creation, used by
/// <see cref="FileEvidenceDeliveryStore"/> to harden its private delivery directories without
/// depending on Mono.Unix (or any other POSIX interop package). The BCL has no "stat owner uid"
/// API, so this wraps the minimum libc surface needed: <c>geteuid()</c> for the current effective
/// user and <c>mkdir()</c> for atomic, mode-at-creation-time directory creation. <c>lstat</c>'s
/// struct layout differs across Linux/macOS/BSD (field order, padding, timespec resolution) in
/// ways that are not safe to hand-roll portably without per-platform structs, so owner-uid/inode
/// lookup instead shells out to a small, fixed set of trusted absolute-path POSIX executables
/// (never resolved via <c>PATH</c>, so a substituted/malicious same-named binary earlier on PATH
/// cannot be invoked instead): <c>/usr/bin/stat</c>, <c>/bin/ls</c>, <c>/usr/bin/getfacl</c>.
/// </summary>
internal static class UnixDirectorySecurity
{
    private const int OwnerOnlyMode = 0x1C0; // 0700 octal: rwx for owner only, nothing for group/other.

    /// <summary>Absolute path to the trusted <c>stat</c> binary — never resolved via PATH.</summary>
    public const string StatExecutable = "/usr/bin/stat";

    /// <summary>Absolute path to the trusted <c>ls</c> binary — never resolved via PATH.</summary>
    public const string LsExecutable = "/bin/ls";

    /// <summary>Absolute path to the trusted <c>getfacl</c> binary (Linux) — never resolved via PATH.</summary>
    public const string GetfaclExecutable = "/usr/bin/getfacl";

    /// <summary>Bound on captured child stdout to guard against a flooding/misbehaving child exhausting memory.</summary>
    private const int MaxCapturedOutputBytes = 1 * 1024 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Count of real (non-cached) owner-uid verification subprocess invocations — incremented only
    /// by <see cref="TryGetOwnerUidAndChangeTime"/> actually running <c>stat</c>, not by callers that skip it via
    /// <see cref="FileEvidenceDeliveryStore"/>'s per-root identity cache. Internal, for regression
    /// tests asserting the cache actually suppresses repeated subprocess launches.
    /// </summary>
    internal static long OwnershipCheckCount;

    /// <summary>Count of real (non-cached) ACL-detection <b>subprocess</b> invocations (Linux <c>getfacl</c> only — macOS never spawns a process for this, see <see cref="NativeAclCheckCount"/>).</summary>
    internal static long AclCheckCount;

    /// <summary>Count of macOS native (no-subprocess) ACL probe calls — <see cref="HasExtraAcl"/> on macOS runs this synchronously on every call (not gated by any cache), so this should increase roughly once per verification call regardless of how many times the subprocess-based owner-uid check was skipped via caching.</summary>
    internal static long NativeAclCheckCount;

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int mkdir(string path, int mode);

    /// <summary>The current process's effective Unix user id, via <c>geteuid()</c>.</summary>
    public static uint GetEffectiveUserId() => geteuid();

    /// <summary>
    /// Atomically creates <paramref name="path"/> as an owner-only (0700) directory: the mode is
    /// applied at <c>mkdir()</c> syscall time (subject only to umask, which can only narrow it
    /// further, never widen it), not via a separate create-then-chmod step, so there is no window
    /// where the directory briefly exists with broader-than-requested permissions. Throws if the
    /// directory cannot be created (including "already exists" — callers must check existence
    /// first if they want to tolerate reuse).
    /// </summary>
    public static void CreateOwnerOnlyDirectory(string path)
    {
        if (mkdir(path, OwnerOnlyMode) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"Unable to create private directory '{path}' with owner-only (0700) mode (errno {err}).");
        }
    }

    /// <summary>
    /// Reads the numeric Unix owner uid of <paramref name="path"/> AND its metadata-change time
    /// (<c>ctime</c>, epoch seconds) in one <see cref="StatExecutable"/> invocation, or null if
    /// unverifiable. <c>ctime</c> — unlike <see cref="DirectoryInfo.CreationTimeUtc"/>/
    /// <see cref="DirectoryInfo.LastWriteTimeUtc"/>, neither of which the BCL backs with the real
    /// inode change time on any platform here — advances whenever the directory's own metadata
    /// changes, including an in-place <c>chmod</c>/ACL grant that touches neither its birth time nor
    /// its content mtime. This is what lets <see cref="FileEvidenceDeliveryStore"/>'s per-root
    /// verification cache correctly invalidate on an in-place ACL change, not just on
    /// delete-and-recreate.
    /// </summary>
    public static (uint Uid, long ChangeTimeEpochSeconds)? TryGetOwnerUidAndChangeTime(string path)
    {
        Interlocked.Increment(ref OwnershipCheckCount);
        if (!File.Exists(StatExecutable))
            return null;
        try
        {
            var isBsdStat = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();
            var args = isBsdStat ? new[] { "-f", "%u %c", path } : new[] { "-c", "%u %Z", path };
            var result = RunTrusted(StatExecutable, args, DefaultTimeout);
            if (result.ExitCode != 0)
                return null;
            var parts = result.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !uint.TryParse(parts[0], out var uid) || !long.TryParse(parts[1], out var ctime))
                return null;
            return (uid, ctime);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Detection of extra Unix ACL entries beyond the owner-only POSIX mode bits. On macOS this is
    /// a direct, synchronous native call — <c>acl_get_link_np</c> (no-follow) + <c>acl_get_entry</c>
    /// from <c>libSystem</c> — not a subprocess: POSIX mode bits alone do not capture ACLs (notably
    /// macOS ACLs granted via <c>chmod +a</c> or Finder/File Sharing, which neither <c>ls -l</c> nor
    /// <see cref="File.GetUnixFileMode"/> can see), and a subprocess-based check (previously
    /// <c>ls -lde</c>) is inherently asynchronous relative to an in-process change — it can only be
    /// cached with a cache-invalidation delay, which cannot close a same-instant grant-then-recheck
    /// race. The native call has no such gap: it reads live kernel ACL state on every call, cheaply
    /// enough to not need caching at all. Constants (<c>ACL_TYPE_EXTENDED = 0x100</c>,
    /// <c>ACL_FIRST_ENTRY = 0</c>) and signatures verified against the authoritative Apple OSS
    /// Distributions header (apple-oss-distributions/Libc, include/sys/acl.h) and confirmed
    /// empirically against a real <c>chmod +a</c> grant/clear cycle — not guessed from memory (an
    /// earlier attempt with an incorrect <c>ACL_TYPE_EXTENDED</c> value failed closed with EINVAL,
    /// which is exactly the fail-safe behavior wanted even for a wrong constant). Any unexpected
    /// errno (other than <c>ENOENT</c>, "no ACL present") fails CLOSED (returns true): mode bits
    /// cannot prove privacy on macOS, so an unverifiable state must not be silently accepted.
    /// <para>
    /// On Linux, ACL inspection remains subprocess-based via the trusted absolute
    /// <see cref="GetfaclExecutable"/> — POSIX ACL support is frequently absent entirely there, and
    /// owner-only mode bits are already a complete and sufficient privacy proof when no ACL
    /// subsystem is present, so a missing <c>getfacl</c> binary does not fail closed; only a
    /// genuinely detected ACL, a timeout, or a flooded/garbled response does.
    /// </para>
    /// </summary>
    public static bool HasExtraAcl(string path)
    {
        if (OperatingSystem.IsMacOS())
            return HasExtraAclMacOsNative(path);
        Interlocked.Increment(ref AclCheckCount);
        return HasExtraAclLinux(path);
    }

    // ACL_TYPE_EXTENDED and ACL_FIRST_ENTRY verified against
    // https://github.com/apple-oss-distributions/Libc/blob/main/include/sys/acl.h and confirmed
    // empirically (acl_get_file/acl_get_link_np return NULL/errno ENOENT with no ACL present, a
    // valid non-null handle with errno 0 immediately after `chmod +a`, and acl_get_entry(FIRST)
    // returns 0 — success, an entry exists — against that handle).
    private const int AclTypeExtended = 0x100;
    private const int AclFirstEntry = 0;
    private const int ENOENT = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr acl_get_link_np(string path, int type);

    [DllImport("libc", SetLastError = true)]
    private static extern int acl_get_entry(IntPtr acl, int entryId, out IntPtr entry);

    [DllImport("libc", SetLastError = true)]
    private static extern int acl_free(IntPtr obj);

    private static bool HasExtraAclMacOsNative(string path)
    {
        Interlocked.Increment(ref NativeAclCheckCount);
        var acl = acl_get_link_np(path, AclTypeExtended);
        if (acl == IntPtr.Zero)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno != ENOENT)
                return true; // Some other failure — fail closed, unverifiable.

            // ENOENT is ambiguous here: acl_get_link_np returns NULL/ENOENT both for "this entry
            // genuinely has no ACL" AND for "nothing exists at this path at all" — distinguish by
            // actually checking existence, so a caller passing a bad/already-deleted path fails
            // closed instead of being silently told "no ACL" (which would be true of any path).
            return !Directory.Exists(path) && !File.Exists(path);
        }

        try
        {
            var rc = acl_get_entry(acl, AclFirstEntry, out _);
            if (rc == 0)
                return true; // At least one entry present.
            var entryErrno = Marshal.GetLastPInvokeError();
            // ENOENT from acl_get_entry legitimately means "no entries in this ACL" (safe, no
            // extra access granted). Any other failure is unverifiable and fails closed, rather
            // than silently treating an unexpected error the same as "confirmed empty".
            return entryErrno != ENOENT;
        }
        finally
        {
            acl_free(acl);
        }
    }

    private static bool HasExtraAclLinux(string path)
    {
        // Linux: best-effort via getfacl, when present. A missing/absent ACL subsystem is not a
        // privacy failure here (unlike macOS) — owner-only mode bits are a complete proof on a
        // filesystem with no ACL support — so a missing binary returns false, not fail-closed.
        if (!File.Exists(GetfaclExecutable))
            return false;
        try
        {
            // getfacl always prints the owner/group/other mask entries (user::, group::, other::)
            // even with no ACL configured; an ACL is present only when it additionally prints a
            // *named* user:<name>: or group:<name>: entry.
            var result = RunTrusted(GetfaclExecutable, ["--omit-header", path], DefaultTimeout);
            if (result.ExitCode != 0)
                return false;
            return result.Stdout
                .Split('\n')
                .Any(l =>
                    (l.StartsWith("user:", StringComparison.Ordinal) && !l.StartsWith("user::", StringComparison.Ordinal)) ||
                    (l.StartsWith("group:", StringComparison.Ordinal) && !l.StartsWith("group::", StringComparison.Ordinal)));
        }
        catch (TimeoutException)
        {
            return true; // a timeout IS suspicious enough to fail closed even on Linux.
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Runs <paramref name="exePath"/> with bounded output capture and an enforced wall-clock
    /// <paramref name="timeout"/> covering the *entire* invocation (not just the final
    /// <c>WaitForExit</c>): stdout/stderr are drained concurrently via async reads started
    /// immediately after launch (so a child that fills its stderr pipe without being read cannot
    /// deadlock the parent), and the timeout is a real deadline — if the process hasn't exited by
    /// then, the process (and its tree) is killed and reaped before returning. Throws
    /// <see cref="TimeoutException"/> on deadline exceeded, or <see cref="InvalidOperationException"/>
    /// if the process cannot be started or stdout exceeds <see cref="MaxCapturedOutputBytes"/>
    /// (flood guard). Does not validate <paramref name="exePath"/> against any allowlist — callers
    /// are responsible for only ever passing a trusted absolute path in production; this method is
    /// intentionally generic so it can be exercised directly against adversarial fixtures (hung,
    /// non-zero exit, flooding) in tests.
    /// </summary>
    internal static TrustedProcessResult RunTrusted(string exePath, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        if (!process.Start())
            throw new InvalidOperationException($"Unable to start trusted process '{exePath}'.");

        var stdoutBuilder = new StringBuilder();
        var stdoutOverflow = false;
        var stdoutDone = ReadBoundedAsync(process.StandardOutput, stdoutBuilder, () => stdoutOverflow = true);
        // Stderr is drained concurrently purely to prevent the child from blocking on a full pipe;
        // its content is intentionally discarded (diagnostics only, not part of the trust decision).
        var stderrDone = DrainAsync(process.StandardError);

        // A single deadline covers BOTH the direct child's exit AND both stream drains completing
        // (not "wait for exit, then separately allow a fixed extra grace period for the streams"):
        // a direct child can exit promptly while leaving an orphaned/detached descendant process
        // holding its inherited stdout/stderr pipe handles open — WaitForExit alone would then
        // report success immediately, but ReadAsync on the pipes could still hang indefinitely
        // waiting for that orphan to close them. Racing everything against one deadline means that
        // case is correctly treated as a timeout too, not silently allowed extra time beyond what
        // the caller asked for.
        using var cts = new CancellationTokenSource();
        Task exitTask;
        try
        {
            exitTask = process.WaitForExitAsync(cts.Token);
        }
        catch (ObjectDisposedException)
        {
            exitTask = Task.CompletedTask;
        }
        var allDone = Task.WhenAll(exitTask, stdoutDone, stderrDone);

        if (!allDone.Wait(timeout))
        {
            cts.Cancel();
            TryKillAndReap(process);
            // Force any still-pending reads (e.g. blocked on an orphaned descendant's open pipe) to
            // unblock with an exception now, rather than leaking the async read tasks forever.
            TryCloseStream(process.StandardOutput);
            TryCloseStream(process.StandardError);
            throw new TimeoutException(
                $"Trusted process '{exePath}' (or an orphaned descendant still holding its output pipes open) did not complete within {timeout}.");
        }

        if (stdoutOverflow)
            throw new InvalidOperationException($"Trusted process '{exePath}' produced more than {MaxCapturedOutputBytes} bytes of output.");

        return new TrustedProcessResult(stdoutBuilder.ToString(), process.ExitCode);
    }

    private static void TryCloseStream(StreamReader reader)
    {
        try { reader.Close(); } catch { /* best-effort */ }
    }

    private static async Task ReadBoundedAsync(StreamReader reader, StringBuilder into, Action onOverflow)
    {
        var buffer = new char[4096];
        var total = 0;
        var overflowed = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (!overflowed && total > MaxCapturedOutputBytes)
            {
                overflowed = true;
                onOverflow();
            }
            if (!overflowed)
                into.Append(buffer, 0, read);
            // After overflow, keep draining (discarding) rather than stopping: a child that keeps
            // writing past the cap would otherwise block on a full pipe waiting for a reader that
            // gave up, turning a flood into a hang — masking the real failure as a timeout instead
            // of the intended "output exceeded the cap" result.
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
        {
            // Discard — draining only to keep the child's stderr pipe from filling and blocking it.
        }
    }

    private static void TryKillAndReap(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        try { process.WaitForExit(2000); } catch { /* best-effort reap */ }
    }
}
