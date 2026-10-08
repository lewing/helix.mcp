using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class DeliveryPrivateDirectoryRegressionTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-runtime", $"private-delivery-{Guid.NewGuid():N}"));
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Theory]
    [InlineData(UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.OtherExecute)]
    public void ExistingDirectory_WithNonprivateMode_IsRejectedWithoutSilentlyRepairingIt(UnixFileMode grant)
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(_root, "preexisting");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, OwnerDirectory | grant);
        var error = Assert.Throws<InvalidOperationException>(() => EnsurePrivate(path));
        Assert.Contains(path, error.Message);
        Assert.Contains("700", error.Message);
        Assert.Contains("retry", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OwnerDirectory | grant, File.GetUnixFileMode(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymlinkedDirectoryOrAncestor_IsRejectedBeforeCreatingAnyChild(bool ancestor)
    {
        if (OperatingSystem.IsWindows())
            return;
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, OwnerDirectory);
        if (ancestor)
        {
            Directory.CreateDirectory(Path.Combine(target, "nested"));
            File.SetUnixFileMode(Path.Combine(target, "nested"), OwnerDirectory);
        }
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        var requested = ancestor ? Path.Combine(link, "nested", "private") : link;
        var error = Assert.Throws<InvalidOperationException>(() => EnsurePrivate(requested));
        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ancestor ? Path.Combine(target, "nested") : target));
    }

    [Fact]
    public void NewlyCreatedDeliveryDirectoriesAndFiles_AreOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;
        var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = _root });
        var receipt = store.PutText("private evidence", "text/plain", null, null, "public");
        Assert.True(receipt.Complete);
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(Path.GetDirectoryName(receipt.LocalPath)!));
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(Path.GetDirectoryName(Path.GetDirectoryName(receipt.LocalPath)!)!));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(receipt.LocalPath));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(Path.ChangeExtension(receipt.LocalPath, ".meta.json")));
        Assert.Equal(OwnerFile, File.GetUnixFileMode(Path.Combine(Path.GetDirectoryName(receipt.LocalPath)!, ".retention-status.json")));
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(receipt.LocalPath)!))
            Assert.Equal(OwnerFile, File.GetUnixFileMode(path));
    }

    [Fact]
    public void PrivateDirectoryCreation_HardensEachNewAncestor()
    {
        if (OperatingSystem.IsWindows())
            return;
        var parent = Path.Combine(_root, "new-parent");
        var leaf = Path.Combine(parent, "private");
        EnsurePrivate(leaf);
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(leaf));
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(parent));
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliveryPut_RejectsSymlinkedDeliveryRootOrPartitionWithoutWritingEvidence(bool partition)
    {
        if (OperatingSystem.IsWindows())
            return;
        var target = Path.Combine(_root, "outside");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, OwnerDirectory);
        var cache = Path.Combine(_root, "cache");
        var options = new CacheOptions { CacheRoot = cache };
        Directory.CreateDirectory(options.GetEffectiveCacheRoot());
        var delivery = Path.Combine(options.GetEffectiveCacheRoot(), "delivery");
        if (partition)
        {
            Directory.CreateDirectory(delivery);
            File.SetUnixFileMode(delivery, OwnerDirectory);
        }
        Directory.CreateSymbolicLink(partition ? Path.Combine(delivery, "public") : delivery, target);
        var store = new FileEvidenceDeliveryStore(options);
        var error = Assert.Throws<InvalidOperationException>(() =>
            store.PutText("must never leak", "text/plain", null, null, "public"));
        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnvironmentSelectedBase_RejectsArbitraryAliasAndLinkedPrivateChild(bool childIsLink)
    {
        if (OperatingSystem.IsWindows())
            return;
        var target = Path.Combine(_root, "system-base");
        Directory.CreateDirectory(target);
        var alias = Path.Combine(_root, "system-alias");
        Directory.CreateSymbolicLink(alias, target);
        if (childIsLink)
        {
            var outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            File.SetUnixFileMode(outside, OwnerDirectory);
            Directory.CreateSymbolicLink(Path.Combine(target, "private"), outside);
            Assert.Throws<InvalidOperationException>(() => EnsurePrivate(alias, "private"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => EnsurePrivate(alias, "private"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
    }

    [RootUnixFact]
    public void ExistingDirectory_WithWrongOwner_IsRejectedWhenOwnershipCanBeChanged()
    {
        if (OperatingSystem.IsWindows() || GetEffectiveUserId() != 0)
            return;
        var path = Path.Combine(_root, "wrong-owner");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, OwnerDirectory);
        Assert.Equal(0, ChangeOwner(path, 65534, uint.MaxValue));
        var error = Assert.Throws<InvalidOperationException>(() => EnsurePrivate(path));
        Assert.Contains("owner", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, error.Message);
    }

    [MacAclFact]
    public void ExistingMacDirectory_WithAclAndExtendedAttributes_IsRejected()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var path = Path.Combine(_root, "acl");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, OwnerDirectory);
        Run("chmod", "+a", "everyone allow read,search", path);
        Run("xattr", "-w", "com.helix.tests.acl-marker", "present", path);
        var error = Assert.Throws<InvalidOperationException>(() => EnsurePrivate(path));
        Assert.Contains("ACL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, error.Message);
    }

    [WindowsAclFact]
    public void ExistingWindowsDirectory_WithEveryoneReadAcl_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(_root, "acl");
        Directory.CreateDirectory(path);
        Run("icacls", path, "/grant", "*S-1-1-0:(OI)(CI)R");
        var error = Assert.Throws<InvalidOperationException>(() => EnsurePrivate(path));
        Assert.Contains(path, error.Message);
        Assert.Contains("ACL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsAclFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NewlyCreatedWindowsDirectory_HasCurrentUserOwnerAndRestrictedDacl()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(_root, "private-windows");
        EnsurePrivate(path);
        var security = new DirectoryInfo(path).GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(identity.User, rule.IdentityReference));
    }

    [WindowsAclTheory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ReusedWindowsDirectory_RejectsBroadExplicitAndInheritedAccess(bool inherited)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var parent = Path.Combine(_root, "windows-parent");
        var leaf = Path.Combine(parent, "private");
        EnsurePrivate(parent);
        if (inherited)
        {
            AddEveryoneReadGrant(parent);
            Directory.CreateDirectory(leaf);
        }
        else
        {
            EnsurePrivate(leaf);
            AddEveryoneReadGrant(leaf);
        }
        var security = new DirectoryInfo(leaf).GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        Assert.Contains(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(everyone) && rule.IsInherited == inherited);
        var error = Assert.Throws<InvalidOperationException>(() => EnsurePrivate(leaf));
        Assert.Contains("ACL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(leaf, error.Message);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddEveryoneReadGrant(string path)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void EnsurePrivate(params string[] pathArguments)
    {
        var method = typeof(FileEvidenceDeliveryStore).GetMethod("EnsureSecurePrivateDir",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            types: Enumerable.Repeat(typeof(string), pathArguments.Length).ToArray(), modifiers: null);
        Assert.NotNull(method);
        try
        {
            method.Invoke(null, pathArguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static void Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{executable} failed: {output} {error}");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint owner, uint group);

    private sealed class RootUnixFactAttribute : FactAttribute
    {
        public RootUnixFactAttribute()
        {
            if (OperatingSystem.IsWindows() || GetEffectiveUserId() != 0)
                Skip = "Changing a fixture's Unix ownership requires a root Unix test host.";
        }
    }

    private sealed class MacAclFactAttribute : FactAttribute
    {
        public MacAclFactAttribute()
        {
            if (!OperatingSystem.IsMacOS())
                Skip = "Requires macOS ACL and extended-attribute APIs.";
        }
    }

    private sealed class WindowsAclFactAttribute : FactAttribute
    {
        public WindowsAclFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Requires Windows owner SID and DACL APIs.";
        }
    }

    private sealed class WindowsAclTheoryAttribute : TheoryAttribute
    {
        public WindowsAclTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Requires Windows owner SID and inherited/explicit DACL APIs.";
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
