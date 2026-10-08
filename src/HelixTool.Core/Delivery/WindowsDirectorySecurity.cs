using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HelixTool.Core.Delivery;

/// <summary>
/// Windows-only directory ACL helpers used by <see cref="FileEvidenceDeliveryStore"/> to create
/// its private delivery directories with a DACL granting only the current user, and to verify both
/// the owner SID and the full effective DACL (explicit and inherited Allow ACEs) on reuse of a
/// pre-existing directory. All members are guarded by
/// <see cref="OperatingSystem.IsWindows"/> at the call site; calling into
/// <see cref="System.Security.AccessControl"/> APIs on a non-Windows OS throws
/// <see cref="PlatformNotSupportedException"/>, so this type must never be touched off-Windows —
/// marked <see cref="SupportedOSPlatformAttribute"/>("windows") so the platform-compatibility
/// analyzer enforces that callers guard it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDirectorySecurity
{
    /// <summary>
    /// Creates <paramref name="path"/> with inheritance disabled and a single access rule granting
    /// the current user full control — no other principal (including Administrators/SYSTEM beyond
    /// their implicit override rights) is granted explicit access, and the owner is set to the
    /// current user so a later <see cref="VerifyOwner"/> call has a stable identity to check.
    /// </summary>
    public static void CreateForCurrentUserOnly(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new InvalidOperationException(
            "Unable to resolve the current Windows user SID to secure the delivery directory.");

        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        // Drop inherited rules from the parent so only the rule added below applies.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var dirInfo = new DirectoryInfo(path);
        dirInfo.Create(security);
    }

    /// <summary>
    /// True when the owner SID of the existing directory at <paramref name="path"/> matches the
    /// current user — a pre-existing directory created by a different principal is not trustworthy
    /// as a private delivery root even if its DACL happens to look restrictive. Callers should also
    /// call <see cref="VerifyDacl"/>: owner match alone does not prove no one else has access (the
    /// owner can still grant broad ACEs, and inherited ACEs apply regardless of owner).
    /// </summary>
    public static bool VerifyOwner(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        if (currentUser is null)
            return false;

        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        return owner is not null && owner.Equals(currentUser);
    }

    /// <summary>
    /// Well-known SIDs allowed to hold an Allow ACE on a private delivery directory: the current
    /// user, <c>NT AUTHORITY\SYSTEM</c>, and <c>BUILTIN\Administrators</c>. Any other principal
    /// (notably <c>Everyone</c>, <c>Authenticated Users</c>, or a plain <c>Users</c> group) with any
    /// Allow access — explicit or inherited — makes the directory untrustworthy as a private root,
    /// regardless of who owns it.
    /// </summary>
    private static bool IsAllowedPrincipal(SecurityIdentifier sid, SecurityIdentifier currentUser) =>
        sid.Equals(currentUser) ||
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    /// <summary>
    /// Validates the *effective* DACL of an existing directory, not just its owner: every Allow ACE
    /// — explicit or inherited — must name only the current user, SYSTEM, or Administrators (see
    /// <see cref="IsAllowedPrincipal"/>). A newly-created-by-us directory always passes (it was
    /// created with exactly one such rule and inheritance disabled), but a *reused* pre-existing
    /// directory could have been created with a broader DACL (e.g. an inherited Everyone-read ACE
    /// from its parent) that <see cref="VerifyOwner"/> alone would not catch. Deny ACEs are ignored
    /// (they only narrow access, never widen it). Fails closed (returns false) if the DACL cannot
    /// be read or translated.
    /// </summary>
    public static bool VerifyDacl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        if (currentUser is null)
            return false;

        try
        {
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                    continue; // Deny rules only narrow access; irrelevant to this check.
                if (rule.IdentityReference is not SecurityIdentifier sid)
                    return false; // Unresolvable principal — fail closed rather than guess.
                if (!IsAllowedPrincipal(sid, currentUser))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
