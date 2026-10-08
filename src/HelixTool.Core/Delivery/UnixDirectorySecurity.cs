using System.Runtime.InteropServices;

namespace HelixTool.Core.Delivery;

/// <summary>
/// P/Invoke-based Unix ownership verification and atomic owner-only directory creation, used by
/// <see cref="FileEvidenceDeliveryStore"/> to harden its private delivery directories without
/// depending on Mono.Unix (or any other POSIX interop package). The BCL has no "stat owner uid"
/// API, so this wraps the minimum libc surface needed: <c>geteuid()</c> for the current effective
/// user and <c>mkdir()</c> for atomic, mode-at-creation-time directory creation. <c>lstat</c>'s
/// struct layout differs across Linux/macOS/BSD (field order, padding, timespec resolution) in
/// ways that are not safe to hand-roll portably without per-platform structs, so owner-uid lookup
/// instead shells out to the universally-present POSIX <c>stat</c> binary (BSD/macOS: <c>stat -f
/// %u</c>, GNU/Linux: <c>stat -c %u</c>) to read just the numeric owner uid — the actual
/// uid-vs-geteuid comparison stays in managed code, and a missing/unexpected `stat` output fails
/// closed (null) rather than silently skipping the check.
/// </summary>
internal static class UnixDirectorySecurity
{
    private const int OwnerOnlyMode = 0x1C0; // 0700 octal: rwx for owner only, nothing for group/other.

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
    /// Reads the numeric Unix owner uid of <paramref name="path"/>, or null if it cannot be
    /// determined (missing `stat` binary, unexpected output, timeout, etc.) — callers must treat
    /// null as "ownership unverifiable" and fail closed, not assume the owner is acceptable.
    /// </summary>
    public static uint? TryGetOwnerUid(string path)
    {
        try
        {
            var isBsdStat = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();
            var psi = new System.Diagnostics.ProcessStartInfo("stat")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (isBsdStat)
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("%u");
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("%u");
            }
            psi.ArgumentList.Add(path);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return null;
            }
            if (proc.ExitCode != 0)
                return null;
            return uint.TryParse(stdout.Trim(), out var uid) ? uid : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort detection of extra Unix ACL entries beyond the owner-only POSIX mode bits.
    /// POSIX mode bits alone do not capture ACLs — notably macOS ACLs granted via <c>chmod +a</c>
    /// or Finder/File Sharing, which neither <c>ls -l</c> nor <see cref="File.GetUnixFileMode"/>
    /// can see: a directory can report mode 0700 and still grant another principal access through
    /// an ACL entry. Returns true (fail closed) only when an ACL is positively detected; returns
    /// false when no ACL inspection tool is available or none is present, since absence of
    /// evidence here must not block legitimate use on minimal hosts without `ls -e`/`getfacl`.
    /// </summary>
    public static bool HasExtraAcl(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // BSD `ls -lde` marks ACL-bearing entries with a trailing '+' on the mode column
                // (e.g. "drwx------+  3 user  staff  96 ..."), and (with `-e`) prints each ACL
                // grant as a numbered line below the main entry (e.g. " 0: group:everyone allow
                // list,search"). When the entry *also* carries extended attributes, macOS instead
                // shows '@' in that same column position — masking the '+' even though the ACL
                // lines are still printed — so the '+' check alone is not sufficient; the presence
                // of any numbered ACL entry line is the authoritative signal.
                var psi = new System.Diagnostics.ProcessStartInfo("ls")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-lde");
                psi.ArgumentList.Add(path);
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null)
                    return false;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                if (proc.ExitCode != 0)
                    return false;
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                    return false;
                if (lines[0].Length > 10 && lines[0][10] == '+')
                    return true;
                // Numbered ACL entry lines look like "<spaces>0: group:everyone allow ...".
                return lines.Skip(1).Any(l =>
                {
                    var trimmed = l.TrimStart();
                    var colon = trimmed.IndexOf(':');
                    return colon > 0 && trimmed[..colon].All(char.IsDigit);
                });
            }

            // Linux: best-effort via getfacl, when present. getfacl always prints the owner/group
            // /other mask entries (user::, group::, other::) even with no ACL configured; an ACL is
            // present only when it additionally prints a *named* user:<name>: or group:<name>:
            // entry.
            var getfaclPsi = new System.Diagnostics.ProcessStartInfo("getfacl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            getfaclPsi.ArgumentList.Add("--omit-header");
            getfaclPsi.ArgumentList.Add(path);
            using var getfaclProc = System.Diagnostics.Process.Start(getfaclPsi);
            if (getfaclProc is null)
                return false;
            var getfaclOutput = getfaclProc.StandardOutput.ReadToEnd();
            if (!getfaclProc.WaitForExit(5000) || getfaclProc.ExitCode != 0)
                return false;

            return getfaclOutput
                .Split('\n')
                .Any(l =>
                    (l.StartsWith("user:", StringComparison.Ordinal) && !l.StartsWith("user::", StringComparison.Ordinal)) ||
                    (l.StartsWith("group:", StringComparison.Ordinal) && !l.StartsWith("group::", StringComparison.Ordinal)));
        }
        catch
        {
            return false;
        }
    }
}
