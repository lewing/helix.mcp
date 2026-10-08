using System.Runtime.InteropServices;

namespace HelixTool.Core.Delivery;

/// <summary>
/// Advisory cross-process mutual-exclusion lock, scoped to one delivery partition root, used by
/// <see cref="FileEvidenceDeliveryStore"/> to close the TOCTOU window between a retention sweep's
/// pin snapshot and its deletes: without this, a second process can renew (pin) an entry's lease
/// via <c>TryGetDescriptor</c>/<c>Read</c>/<c>Put</c> in between this process reading a stale,
/// already-expired snapshot and acting on it, and the stale sweep would unlink a file whose receipt
/// was just handed out again. Backed by a lock file (<see cref="FileEvidenceDeliveryStore.CrossProcessLockFileName"/>)
/// inside the partition root:
/// <list type="bullet">
/// <item>On Windows, opening the file with <see cref="FileShare.None"/> is itself enforced across
/// separate processes by the OS, so the plain <see cref="FileStream"/> open/close is sufficient.</item>
/// <item>On POSIX, <see cref="FileShare.None"/> is <b>not</b> reliably honored across separate
/// .NET processes (the share-mode emulation historically only applies within one runtime's lock
/// table), so a real <c>flock(LOCK_EX)</c> is additionally taken on the open file descriptor and
/// released with <c>flock(LOCK_UN)</c> on dispose.</item>
/// </list>
/// In-process callers must still take the existing per-root <see cref="object"/> monitor first
/// (cheaper, and avoids one thread blocking another thread in the same process on a syscall); this
/// type only needs to be acquired once per logical operation, not re-entered — nesting two
/// acquisitions in the same process would deadlock on POSIX, since <c>flock</c> excludes a
/// process's own second independent file descriptor on the same file just as it would a different
/// process's.
/// </summary>
internal sealed class CrossProcessPartitionLock : IDisposable
{
    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    private const int LockExclusive = 2; // LOCK_EX
    private const int LockUnlock = 8;    // LOCK_UN

    private readonly FileStream _stream;
    private readonly bool _flocked;

    private CrossProcessPartitionLock(FileStream stream, bool flocked)
    {
        _stream = stream;
        _flocked = flocked;
    }

    /// <summary>
    /// Blocks until the cross-process lock for <paramref name="root"/> is acquired. <paramref name="root"/>
    /// must already exist (the lock file is created inside it).
    /// </summary>
    public static CrossProcessPartitionLock Acquire(string root)
    {
        var lockPath = Path.Combine(root, FileEvidenceDeliveryStore.CrossProcessLockFileName);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            // Atomic owner-only (0600) creation, matching the .bin/.meta.json hardening — a
            // separate create-then-chmod step would briefly leave the lock file at the default
            // (umask-dependent, typically 0644) mode.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        // FileShare.None: on Windows this alone excludes other processes. Retry the open itself in
        // case another process holds it exclusively right now (distinct from the flock wait below).
        FileStream? stream = null;
        var spin = new SpinWait();
        while (stream is null)
        {
            try
            {
                stream = new FileStream(lockPath, options);
            }
            catch (IOException)
            {
                spin.SpinOnce();
            }
        }

        if (OperatingSystem.IsWindows())
            return new CrossProcessPartitionLock(stream, flocked: false);

        // Best-effort tighten for a pre-existing lock file created by an older version (atomic
        // UnixCreateMode above only applies on fresh creation, not on reuse of an existing file).
        try { File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { /* best-effort */ }

        var fd = (int)stream.SafeFileHandle.DangerousGetHandle();
        // flock() blocks until the exclusive lock is available — the actual cross-process mutual
        // exclusion on POSIX, where FileShare.None alone is not honored across processes.
        if (flock(fd, LockExclusive) != 0)
        {
            stream.Dispose();
            throw new InvalidOperationException($"Unable to acquire cross-process lock on '{lockPath}' (errno {Marshal.GetLastPInvokeError()}).");
        }

        return new CrossProcessPartitionLock(stream, flocked: true);
    }

    public void Dispose()
    {
        if (_flocked)
        {
            var fd = (int)_stream.SafeFileHandle.DangerousGetHandle();
            flock(fd, LockUnlock);
        }
        _stream.Dispose();
    }
}
