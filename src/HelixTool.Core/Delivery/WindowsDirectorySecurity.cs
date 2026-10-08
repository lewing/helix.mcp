using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HelixTool.Core.Delivery;

/// <summary>
/// Windows-only directory ACL helpers used by <see cref="FileEvidenceDeliveryStore"/> to create
/// its private delivery directories with a DACL granting only the current user, and to verify the
/// owner SID on reuse of a pre-existing directory. All members are guarded by
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
    /// as a private delivery root even if its DACL happens to look restrictive.
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
}
