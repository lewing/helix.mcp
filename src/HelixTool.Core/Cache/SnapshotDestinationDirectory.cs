using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HelixTool.Core.Cache;

/// <summary>
/// Retains the destination parent selected before export callbacks run and rejects cooperative
/// replacement or retargeting. The parent namespace is otherwise a trusted export precondition.
/// </summary>
internal sealed class SnapshotDestinationDirectory : IDisposable
{
    private const int LinuxGenericOpenDirectory = 0x10000;
    private const int LinuxGenericOpenNoFollow = 0x20000;
    private const int LinuxArmPpcOpenDirectory = 0x4000;
    private const int LinuxArmPpcOpenNoFollow = 0x8000;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int MacOpenDirectory = 0x100000;
    private const int MacOpenNoFollow = 0x100;
    private const int MacOpenCloseOnExec = 0x1000000;
    private const uint MacRenameExclusive = 0x00000004;
    private const uint LinuxRenameNoReplace = 1;
    private const int AtCurrentWorkingDirectory = -100;

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private const int ErrorExists = 17;
    private const int LinuxErrorNotEmpty = 39;
    private const int MacErrorNotEmpty = 66;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private readonly SafeFileHandle _parentHandle;
    private readonly SnapshotDirectoryIdentity _parentIdentity;

    private SnapshotDestinationDirectory(string physicalPath)
    {
        PhysicalPath = SnapshotExporter.CanonicalizeExistingPath(
            physicalPath,
            requireDirectory: true);
        _parentHandle = OpenDirectory(PhysicalPath);
        try
        {
            _parentIdentity = GetIdentity(_parentHandle);
            EnsureParentStillNamed();
        }
        catch
        {
            _parentHandle.Dispose();
            throw;
        }
    }

    private string PhysicalPath { get; }

    public static SnapshotDestinationDirectory Open(string physicalPath)
    {
        if (!OperatingSystem.IsWindows() &&
            !OperatingSystem.IsLinux() &&
            !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Snapshot export supports Windows, Linux, and macOS.");
        }

        return new SnapshotDestinationDirectory(physicalPath);
    }

    public SnapshotTemporaryDirectory CreateTemporaryDirectory(string destinationLeaf)
    {
        ValidateLeaf(destinationLeaf);
        EnsureParentStillNamed();

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var name = $"{destinationLeaf}.tmp.{Guid.NewGuid():N}";
            var path = Path.Combine(PhysicalPath, name);
            if (SnapshotExporter.PathEntryExists(path))
                continue;

            Directory.CreateDirectory(path);
            SafeFileHandle? handle = null;
            try
            {
                handle = OpenDirectory(path);
                return new SnapshotTemporaryDirectory(
                    name,
                    handle,
                    GetIdentity(handle),
                    this);
            }
            catch (Exception creationFailure)
            {
                handle?.Dispose();
                try
                {
                    DeleteTreeWithoutFollowingLinks(path);
                }
                catch (Exception cleanupFailure)
                {
                    creationFailure.Data["SnapshotCleanupFailure"] = cleanupFailure;
                }

                throw;
            }
        }

        throw new InvalidOperationException(
            "Unable to allocate a unique temporary snapshot directory.");
    }

    public void Publish(
        SnapshotTemporaryDirectory temporaryDirectory,
        string currentParentPath,
        string destinationLeaf,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLeaf(destinationLeaf);

        var temporary = RequireTemporaryDirectory(temporaryDirectory);
        var parentPath = GetCurrentPhysicalPath(currentParentPath);
        var sourcePath = GetOwnedTemporaryPath(temporary);
        var destinationPath = Path.Combine(parentPath, destinationLeaf);
        if (SnapshotExporter.PathEntryExists(destinationPath))
            throw DestinationExists(destinationPath);

        cancellationToken.ThrowIfCancellationRequested();
        PublishNoReplace(sourcePath, destinationPath);
        temporary.CurrentName = destinationLeaf;
    }

    public string GetCurrentPhysicalPath(string currentParentPath)
    {
        string currentPath;
        try
        {
            currentPath = SnapshotExporter.CanonicalizeExistingPath(
                currentParentPath,
                requireDirectory: true);
        }
        catch (InvalidOperationException ex)
        {
            throw ParentChanged(ex);
        }

        if (!SnapshotExporter.PathsAreEqualForPositiveProof(currentPath, PhysicalPath))
            throw ParentChanged();

        SafeFileHandle currentHandle;
        try
        {
            currentHandle = OpenDirectory(currentPath);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or
                UnauthorizedAccessException or NotSupportedException)
        {
            throw ParentChanged(ex);
        }

        using (currentHandle)
        {
            if (GetIdentity(currentHandle) != _parentIdentity)
                throw ParentChanged();
        }

        return PhysicalPath;
    }

    public void Cleanup(
        SnapshotTemporaryDirectory temporaryDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = RequireTemporaryDirectory(temporaryDirectory);
        if (temporary.Removed)
            return;

        DeleteTreeWithoutFollowingLinks(GetOwnedTemporaryPath(temporary));
        temporary.Removed = true;
    }

    public void Dispose() => _parentHandle.Dispose();

    internal string GetOwnedTemporaryPath(SnapshotTemporaryDirectory temporary)
    {
        if (temporary.Removed)
            throw new InvalidOperationException("The temporary snapshot directory was removed.");

        EnsureParentStillNamed();
        if (GetIdentity(temporary.Handle) != temporary.Identity)
        {
            throw new InvalidOperationException(
                "The retained temporary snapshot directory identity changed.");
        }

        var path = Path.Combine(PhysicalPath, temporary.CurrentName);
        if (!Directory.Exists(path) || IsLink(new DirectoryInfo(path)))
        {
            throw new InvalidOperationException(
                "The owned temporary snapshot directory is no longer present at its retained name.");
        }

        using var namedHandle = OpenDirectory(path);
        if (GetIdentity(namedHandle) != temporary.Identity)
        {
            throw new InvalidOperationException(
                "The owned temporary snapshot directory is no longer present at its retained name.");
        }

        return path;
    }

    internal static string[] GetRelativePathComponents(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("A staging path must be non-empty and relative.");

        var separators = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? new[] { Path.DirectorySeparatorChar }
            : new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var components = relativePath.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 ||
            components.Any(component =>
                component is "." or ".." ||
                component.Contains('\0') ||
                (OperatingSystem.IsWindows() && component.Contains(':'))))
        {
            throw new InvalidOperationException("A staging path contains an unsafe component.");
        }

        return components;
    }

    internal static bool IsLink(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
        entry.LinkTarget != null;

    private void EnsureParentStillNamed()
    {
        SafeFileHandle namedHandle;
        try
        {
            namedHandle = OpenDirectory(PhysicalPath);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or
                UnauthorizedAccessException or NotSupportedException)
        {
            throw ParentChanged(ex);
        }

        using (namedHandle)
        {
            if (GetIdentity(namedHandle) != _parentIdentity)
                throw ParentChanged();
        }
    }

    private SnapshotTemporaryDirectory RequireTemporaryDirectory(
        SnapshotTemporaryDirectory temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(temporaryDirectory);
        if (!ReferenceEquals(temporaryDirectory.Owner, this))
        {
            throw new ArgumentException(
                "Temporary directory belongs to another destination.",
                nameof(temporaryDirectory));
        }

        return temporaryDirectory;
    }

    private static void PublishNoReplace(string sourcePath, string destinationPath)
    {
        // The paths are siblings, so these are same-filesystem atomic renames. MoveFileEx with
        // no flags, RENAME_NOREPLACE, and RENAME_EXCL all refuse to replace an existing entry.
        int result;
        if (OperatingSystem.IsWindows())
        {
            if (MoveFileExW(
                    ToExtendedPath(sourcePath),
                    ToExtendedPath(destinationPath),
                    flags: 0))
            {
                return;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileExists or ErrorAlreadyExists)
                throw DestinationExists(destinationPath);
            throw NativeFailure("Unable to atomically publish the snapshot", error);
        }

        if (OperatingSystem.IsMacOS())
        {
            result = renamex_np(sourcePath, destinationPath, MacRenameExclusive);
        }
        else
        {
            result = renameat2(
                AtCurrentWorkingDirectory,
                sourcePath,
                AtCurrentWorkingDirectory,
                destinationPath,
                LinuxRenameNoReplace);
        }

        if (result == 0)
            return;

        var errno = Marshal.GetLastPInvokeError();
        if (errno is ErrorExists or LinuxErrorNotEmpty or MacErrorNotEmpty)
            throw DestinationExists(destinationPath);
        throw NativeFailure("Unable to atomically publish the snapshot", errno);
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFileW(
                ToExtendedPath(path),
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw NativeFailure($"Unable to open snapshot directory '{path}'", error);
            }

            var information = GetWindowsInformation(handle);
            if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
                (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                handle.Dispose();
                throw new InvalidOperationException(
                    $"Snapshot path is not a direct directory: {path}");
            }

            return handle;
        }

        var flags = OperatingSystem.IsMacOS()
            ? MacOpenDirectory | MacOpenNoFollow | MacOpenCloseOnExec
            : GetLinuxOpenDirectoryFlags(RuntimeInformation.ProcessArchitecture);
        var descriptor = open(path, flags);
        if (descriptor < 0)
        {
            throw NativeFailure(
                $"Unable to open snapshot directory '{path}'",
                Marshal.GetLastPInvokeError());
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static int GetLinuxOpenDirectoryFlags(Architecture architecture)
    {
        // ARM and PowerPC preserve their historical open(2) values instead of asm-generic's.
        var directoryAndNoFollow = architecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or
                Architecture.Ppc64le =>
                LinuxArmPpcOpenDirectory | LinuxArmPpcOpenNoFollow,
            Architecture.X86 or Architecture.X64 or Architecture.S390x or
                Architecture.LoongArch64 or Architecture.RiscV64 =>
                LinuxGenericOpenDirectory | LinuxGenericOpenNoFollow,
            var unsupportedArchitecture => throw new PlatformNotSupportedException(
                $"Snapshot export does not define Linux open flags for {unsupportedArchitecture}."),
        };

        return directoryAndNoFollow | LinuxOpenCloseOnExec;
    }

    private static SnapshotDirectoryIdentity GetIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var information = GetWindowsInformation(handle);
            return new SnapshotDirectoryIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        const int statBufferSize = 256;
        var buffer = Marshal.AllocHGlobal(statBufferSize);
        try
        {
            int result;
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (OperatingSystem.IsMacOS() &&
                RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                result = fstat_inode64(descriptor, buffer);
            }
            else
            {
                result = fstat(descriptor, buffer);
            }

            if (result != 0)
            {
                throw NativeFailure(
                    "Unable to inspect a retained snapshot directory",
                    Marshal.GetLastPInvokeError());
            }

            if (OperatingSystem.IsMacOS())
            {
                return new SnapshotDirectoryIdentity(
                    unchecked((uint)Marshal.ReadInt32(buffer, 0)),
                    unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
            }

            return new SnapshotDirectoryIdentity(
                unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ByHandleFileInformation GetWindowsInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeFailure(
                "Unable to inspect a retained snapshot directory",
                Marshal.GetLastPInvokeError());
        }

        return information;
    }

    private static void DeleteTreeWithoutFollowingLinks(string path)
    {
        if (!SnapshotExporter.PathEntryExists(path))
            return;

        var root = new DirectoryInfo(path);
        if (IsLink(root))
        {
            Directory.Delete(path);
            return;
        }

        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory && !IsLink(directory))
                DeleteTreeWithoutFollowingLinks(directory.FullName);
            else if (entry is DirectoryInfo)
                Directory.Delete(entry.FullName);
            else
                File.Delete(entry.FullName);
        }

        Directory.Delete(path);
    }

    private static void ValidateLeaf(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name is "." or ".." ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            name.Contains('\0') ||
            OperatingSystem.IsWindows() && name.Contains(':'))
        {
            throw new InvalidOperationException("A snapshot destination name is invalid.");
        }
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + Path.GetFullPath(path);
    }

    private static InvalidOperationException ParentChanged(Exception? innerException = null) =>
        new(
            "Destination parent changed while the snapshot was being exported. " +
            "Snapshot export requires a trusted destination parent that is not concurrently " +
            "renamed, replaced, or mutated by another same-principal process.",
            innerException);

    private static InvalidOperationException DestinationExists(string destinationPath) =>
        new($"Destination already exists: {destinationPath}. It was not overwritten.");

    private static InvalidOperationException NativeFailure(string operation, int error) =>
        new($"{operation}: {new Win32Exception(error).Message} (error {error}).");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string newFileName,
        uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int descriptor, IntPtr stat);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_inode64(int descriptor, IntPtr stat);

    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int renamex_np(string oldPath, string newPath, uint flags);

}

internal sealed class SnapshotTemporaryDirectory : IDisposable
{
    internal SnapshotTemporaryDirectory(
        string name,
        SafeFileHandle handle,
        SnapshotDirectoryIdentity identity,
        SnapshotDestinationDirectory owner)
    {
        CurrentName = name;
        Handle = handle;
        Identity = identity;
        Owner = owner;
    }

    internal string CurrentName { get; set; }

    internal SafeFileHandle Handle { get; }

    internal SnapshotDirectoryIdentity Identity { get; }

    internal SnapshotDestinationDirectory Owner { get; }

    internal bool Removed { get; set; }

    internal string GetCurrentPath() => Owner.GetOwnedTemporaryPath(this);

    internal void CreateDirectory(string relativePath)
    {
        var current = GetCurrentPath();
        foreach (var component in SnapshotDestinationDirectory.GetRelativePathComponents(relativePath))
        {
            current = Path.Combine(current, component);
            if (!SnapshotExporter.PathEntryExists(current))
            {
                Directory.CreateDirectory(current);
                continue;
            }

            var directory = new DirectoryInfo(current);
            if (!Directory.Exists(current) || SnapshotDestinationDirectory.IsLink(directory))
            {
                throw new InvalidOperationException(
                    $"Staged snapshot path component is not a direct directory: {component}");
            }
        }
    }

    internal FileStream CreateNewFile(string relativePath)
    {
        var components = SnapshotDestinationDirectory.GetRelativePathComponents(relativePath);
        if (components.Length > 1)
            CreateDirectory(Path.Combine(components[..^1]));

        return new FileStream(
            Path.Combine(GetCurrentPath(), Path.Combine(components)),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Dispose() => Handle.Dispose();
}

internal readonly record struct SnapshotDirectoryIdentity(ulong Volume, ulong FileId);
