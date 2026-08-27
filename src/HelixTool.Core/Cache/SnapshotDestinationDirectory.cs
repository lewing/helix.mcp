using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HelixTool.Core.Cache;

/// <summary>
/// Retains the destination parent selected before export callbacks run.
/// </summary>
internal abstract class SnapshotDestinationDirectory : IDisposable
{
    protected SnapshotDestinationDirectory(string physicalPath)
    {
        PhysicalPath = Path.GetFullPath(physicalPath);
    }

    protected string PhysicalPath { get; }

    public static SnapshotDestinationDirectory Open(string physicalPath)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsSnapshotDestinationDirectory(physicalPath);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return new UnixSnapshotDestinationDirectory(physicalPath);

        throw new PlatformNotSupportedException(
            "Snapshot export supports Windows, Linux, and macOS.");
    }

    public abstract SnapshotTemporaryDirectory CreateTemporaryDirectory(string destinationLeaf);

    public abstract void CompleteStaging(SnapshotTemporaryDirectory temporaryDirectory);

    public abstract void Publish(
        SnapshotTemporaryDirectory temporaryDirectory,
        string currentParentPath,
        string destinationLeaf);

    public abstract string GetCurrentPhysicalPath(string currentParentPath);

    public abstract void Cleanup(SnapshotTemporaryDirectory temporaryDirectory);

    public abstract void Dispose();

    protected static void DeleteTreeWithoutFollowingLinks(string path)
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
            if (entry is DirectoryInfo directory)
            {
                if (IsLink(directory))
                    Directory.Delete(directory.FullName);
                else
                    DeleteTreeWithoutFollowingLinks(directory.FullName);
            }
            else
            {
                File.Delete(entry.FullName);
            }
        }

        Directory.Delete(path);
    }

    internal static bool IsLink(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
        entry.LinkTarget != null;
}

internal abstract class SnapshotTemporaryDirectory : IDisposable
{
    protected SnapshotTemporaryDirectory(string name)
    {
        CurrentName = name;
    }

    internal string CurrentName { get; set; }

    internal bool Removed { get; set; }

    internal abstract string GetCurrentPath();

    internal abstract void CreateDirectory(string relativePath);

    internal abstract FileStream CreateNewFile(string relativePath);

    public abstract void Dispose();

    protected static string[] GetRelativePathComponents(string relativePath)
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

    protected static void CreateRelativeDirectory(string root, string relativePath)
    {
        var current = root;
        foreach (var component in GetRelativePathComponents(relativePath))
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

    protected static FileStream CreateRelativeFile(string root, string relativePath)
    {
        var components = GetRelativePathComponents(relativePath);
        if (components.Length > 1)
        {
            CreateRelativeDirectory(
                root,
                Path.Combine(components[..^1]));
        }

        var path = Path.Combine(root, Path.Combine(components));
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}

/// <summary>
/// Unix anchors the selected parent as a canonical path and publishes with a same-directory,
/// no-replace rename. The caller must trust the destination parent against adversarial namespace
/// mutation between the explicit revalidation points.
/// </summary>
internal sealed class UnixSnapshotDestinationDirectory : SnapshotDestinationDirectory
{
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int MacOpenDirectory = 0x100000;
    private const int MacOpenNoFollow = 0x100;
    private const int MacOpenCloseOnExec = 0x1000000;
    private const uint MacRenameExclusive = 0x00000004;
    private const uint LinuxRenameNoReplace = 1;
    private const int AtCurrentWorkingDirectory = -100;
    private const int ErrorExists = 17;
    private const int ErrorNotEmpty = 39;
    private const int MacErrorNotEmpty = 66;

    private readonly SafeFileHandle _parentHandle;
    private readonly SnapshotDirectoryIdentity _parentIdentity;

    public UnixSnapshotDestinationDirectory(string physicalPath)
        : base(physicalPath)
    {
        _parentHandle = OpenDirectory(PhysicalPath);
        try
        {
            _parentIdentity = GetIdentity(_parentHandle);
        }
        catch
        {
            _parentHandle.Dispose();
            throw;
        }
    }

    public override SnapshotTemporaryDirectory CreateTemporaryDirectory(string destinationLeaf)
    {
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
                return new UnixSnapshotTemporaryDirectory(
                    name,
                    path,
                    handle,
                    GetIdentity(handle),
                    this);
            }
            catch
            {
                handle?.Dispose();
                DeleteTreeWithoutFollowingLinks(path);
                throw;
            }
        }

        throw new InvalidOperationException(
            "Unable to allocate a unique temporary snapshot directory.");
    }

    public override void CompleteStaging(SnapshotTemporaryDirectory temporaryDirectory)
    {
        _ = GetOwnedTemporaryPath(RequireTemporaryDirectory(temporaryDirectory));
    }

    public override void Publish(
        SnapshotTemporaryDirectory temporaryDirectory,
        string currentParentPath,
        string destinationLeaf)
    {
        var temporary = RequireTemporaryDirectory(temporaryDirectory);
        var parentPath = GetCurrentPhysicalPath(currentParentPath);
        var sourcePath = GetOwnedTemporaryPath(temporary);
        var destinationPath = Path.Combine(parentPath, destinationLeaf);

        if (SnapshotExporter.PathEntryExists(destinationPath))
        {
            throw new InvalidOperationException(
                $"Destination already exists: {destinationPath}. It was not overwritten.");
        }

        int result;
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

        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorExists or ErrorNotEmpty or MacErrorNotEmpty)
            {
                throw new InvalidOperationException(
                    $"Destination already exists: {destinationPath}. It was not overwritten.");
            }

            throw NativeFailure("Unable to atomically publish the snapshot", error);
        }

        temporary.CurrentName = destinationLeaf;
    }

    public override string GetCurrentPhysicalPath(string currentParentPath)
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
            throw new InvalidOperationException(
                "Destination parent changed or can no longer be resolved.",
                ex);
        }

        if (!SnapshotExporter.PathsAreEqualForPositiveProof(currentPath, PhysicalPath))
        {
            throw new InvalidOperationException(
                "Destination parent changed while the snapshot was being exported.");
        }

        using var currentHandle = OpenDirectory(currentPath);
        if (GetIdentity(currentHandle) != _parentIdentity)
        {
            throw new InvalidOperationException(
                "Destination parent changed while the snapshot was being exported.");
        }

        return PhysicalPath;
    }

    public override void Cleanup(SnapshotTemporaryDirectory temporaryDirectory)
    {
        var temporary = RequireTemporaryDirectory(temporaryDirectory);
        if (temporary.Removed)
            return;

        DeleteTreeWithoutFollowingLinks(GetOwnedTemporaryPath(temporary));
        temporary.Removed = true;
    }

    public override void Dispose() => _parentHandle.Dispose();

    internal string GetOwnedTemporaryPath(UnixSnapshotTemporaryDirectory temporary)
    {
        var expectedPath = Path.Combine(PhysicalPath, temporary.CurrentName);
        if (!SnapshotExporter.PathsAreEqualForPositiveProof(temporary.Path, expectedPath) ||
            !Directory.Exists(temporary.Path) ||
            IsLink(new DirectoryInfo(temporary.Path)))
        {
            throw new InvalidOperationException(
                "The owned temporary snapshot directory is no longer present at its retained name.");
        }

        if (GetIdentity(temporary.Handle) != temporary.Identity)
        {
            throw new InvalidOperationException(
                "The retained temporary snapshot directory identity changed.");
        }

        using var namedHandle = OpenDirectory(temporary.Path);
        if (GetIdentity(namedHandle) != temporary.Identity)
        {
            throw new InvalidOperationException(
                "The owned temporary snapshot directory is no longer present at its retained name.");
        }

        return temporary.Path;
    }

    private static UnixSnapshotTemporaryDirectory RequireTemporaryDirectory(
        SnapshotTemporaryDirectory temporaryDirectory)
    {
        if (temporaryDirectory is not UnixSnapshotTemporaryDirectory temporary)
            throw new ArgumentException("Temporary directory belongs to another platform backend.");
        return temporary;
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var flags = OperatingSystem.IsMacOS()
            ? MacOpenDirectory | MacOpenNoFollow | MacOpenCloseOnExec
            : LinuxOpenDirectory | LinuxOpenNoFollow | LinuxOpenCloseOnExec;
        var descriptor = open(path, flags);
        if (descriptor < 0)
        {
            throw NativeFailure(
                $"Unable to open snapshot directory '{path}'",
                Marshal.GetLastPInvokeError());
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static SnapshotDirectoryIdentity GetIdentity(SafeFileHandle handle)
    {
        // Supported 64-bit Linux stat ABIs place dev_t and ino_t at offsets 0 and 8.
        // Darwin places its 32-bit dev_t at 0 and 64-bit ino_t at 8; Intel Darwin
        // requires the INODE64 entry point. The oversized buffer avoids depending on
        // unrelated trailing fields in either native struct.
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

    private static InvalidOperationException NativeFailure(string operation, int error) =>
        new($"{operation}: {new Win32Exception(error).Message} (error {error}).");

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

internal sealed class UnixSnapshotTemporaryDirectory : SnapshotTemporaryDirectory
{
    private readonly UnixSnapshotDestinationDirectory _owner;

    public UnixSnapshotTemporaryDirectory(
        string name,
        string path,
        SafeFileHandle handle,
        SnapshotDirectoryIdentity identity,
        UnixSnapshotDestinationDirectory owner)
        : base(name)
    {
        Path = path;
        Handle = handle;
        Identity = identity;
        _owner = owner;
    }

    internal string Path { get; }

    internal SafeFileHandle Handle { get; }

    internal SnapshotDirectoryIdentity Identity { get; }

    internal override string GetCurrentPath() => _owner.GetOwnedTemporaryPath(this);

    internal override void CreateDirectory(string relativePath) =>
        CreateRelativeDirectory(GetCurrentPath(), relativePath);

    internal override FileStream CreateNewFile(string relativePath) =>
        CreateRelativeFile(GetCurrentPath(), relativePath);

    public override void Dispose() => Handle.Dispose();
}

internal readonly record struct SnapshotDirectoryIdentity(ulong Volume, ulong FileId);

/// <summary>
/// Windows retains parent and staging handles. After the last callback, the complete staged tree is
/// opened without write or delete sharing while its serialized files are validated.
/// </summary>
internal sealed class WindowsSnapshotDestinationDirectory : SnapshotDestinationDirectory
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int FileRenameInformationClass = 10;

    private readonly SafeFileHandle _parentHandle;
    private readonly SnapshotDirectoryIdentity _parentIdentity;

    public WindowsSnapshotDestinationDirectory(string physicalPath)
        : base(physicalPath)
    {
        _parentHandle = OpenDirectory(
            PhysicalPath,
            FileListDirectory | FileTraverse | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete);
        try
        {
            _parentIdentity = GetIdentity(_parentHandle);
            if (!SnapshotExporter.PathsAreEqualForPositiveProof(
                    GetFinalPath(_parentHandle),
                    PhysicalPath))
            {
                throw new InvalidOperationException(
                    "Destination parent changed while its directory handle was opened.");
            }
        }
        catch
        {
            _parentHandle.Dispose();
            throw;
        }
    }

    public override SnapshotTemporaryDirectory CreateTemporaryDirectory(string destinationLeaf)
    {
        var parentPath = GetFinalPath(_parentHandle);
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var name = $"{destinationLeaf}.tmp.{Guid.NewGuid():N}";
            var path = Path.Combine(parentPath, name);
            if (SnapshotExporter.PathEntryExists(path))
                continue;

            Directory.CreateDirectory(path);
            SafeFileHandle? handle = null;
            try
            {
                handle = OpenDirectory(
                    path,
                    FileListDirectory | FileReadAttributes,
                    FileShareRead | FileShareWrite | FileShareDelete);
                var identity = GetIdentity(handle);
                return new WindowsSnapshotTemporaryDirectory(
                    name,
                    handle,
                    identity,
                    this);
            }
            catch
            {
                handle?.Dispose();
                DeleteTreeWithoutFollowingLinks(path);
                throw;
            }
        }

        throw new InvalidOperationException(
            "Unable to allocate a unique temporary snapshot directory.");
    }

    public override void CompleteStaging(SnapshotTemporaryDirectory temporaryDirectory)
    {
        var temporary = RequireTemporaryDirectory(temporaryDirectory);
        _ = GetOwnedTemporaryPath(temporary);
        temporary.Freeze();
        _ = GetOwnedTemporaryPath(temporary);
    }

    public override void Publish(
        SnapshotTemporaryDirectory temporaryDirectory,
        string currentParentPath,
        string destinationLeaf)
    {
        var temporary = RequireTemporaryDirectory(temporaryDirectory);
        _ = GetCurrentPhysicalPath(currentParentPath);
        var temporaryPath = GetOwnedTemporaryPath(temporary);
        var destinationPath = Path.Combine(GetFinalPath(_parentHandle), destinationLeaf);
        if (SnapshotExporter.PathEntryExists(destinationPath))
        {
            throw new InvalidOperationException(
                $"Destination already exists: {destinationPath}. It was not overwritten.");
        }

        temporary.ReleaseFreeze();
        using var publishingHandle = OpenDirectory(
            temporaryPath,
            FileListDirectory | FileReadAttributes | DeleteAccess,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (GetIdentity(publishingHandle) != temporary.Identity)
        {
            throw new InvalidOperationException(
                "The temporary snapshot changed before publication.");
        }

        RenameByHandle(publishingHandle, _parentHandle, destinationLeaf);
        temporary.CurrentName = destinationLeaf;
    }

    public override string GetCurrentPhysicalPath(string currentParentPath)
    {
        SafeFileHandle current;
        try
        {
            current = OpenDirectory(
                currentParentPath,
                FileListDirectory | FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                followFinalLink: true);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or
                UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "Destination parent changed or can no longer be resolved.",
                ex);
        }

        using (current)
        {
            if (GetIdentity(current) != _parentIdentity)
            {
                throw new InvalidOperationException(
                    "Destination parent changed while the snapshot was being exported.");
            }
        }

        return GetFinalPath(_parentHandle);
    }

    public override void Cleanup(SnapshotTemporaryDirectory temporaryDirectory)
    {
        var temporary = RequireTemporaryDirectory(temporaryDirectory);
        if (temporary.Removed)
            return;

        temporary.ReleaseFreeze();
        var currentPath = GetFinalPath(temporary.Handle);
        DeleteTreeWithoutFollowingLinks(currentPath);
        temporary.Removed = true;
    }

    public override void Dispose() => _parentHandle.Dispose();

    internal string GetOwnedTemporaryPath(WindowsSnapshotTemporaryDirectory temporary)
    {
        if (GetIdentity(temporary.Handle) != temporary.Identity)
        {
            throw new InvalidOperationException(
                "The retained temporary snapshot directory identity changed.");
        }

        var parentPath = GetFinalPath(_parentHandle);
        var temporaryPath = GetFinalPath(temporary.Handle);
        var expectedPath = Path.Combine(parentPath, temporary.CurrentName);
        if (!SnapshotExporter.PathsAreEqualForPositiveProof(temporaryPath, expectedPath))
        {
            throw new InvalidOperationException(
                "The owned temporary snapshot directory is no longer present at its retained name.");
        }

        using var named = OpenDirectory(
            expectedPath,
            FileListDirectory | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete);
        if (GetIdentity(named) != temporary.Identity)
        {
            throw new InvalidOperationException(
                "The owned temporary snapshot directory is no longer present at its retained name.");
        }

        return temporaryPath;
    }

    internal void FreezeTree(WindowsSnapshotTemporaryDirectory temporary)
    {
        var rootPath = GetOwnedTemporaryPath(temporary);
        var leases = new List<IDisposable>();
        try
        {
            FreezeDirectory(rootPath, temporary.Identity, leases);
            temporary.SetFreeze(leases);
        }
        catch
        {
            foreach (var lease in leases)
                lease.Dispose();
            throw;
        }
    }

    private static void FreezeDirectory(
        string path,
        SnapshotDirectoryIdentity expectedIdentity,
        List<IDisposable> leases)
    {
        var directoryHandle = OpenDirectory(
            path,
            FileListDirectory | FileReadAttributes,
            FileShareRead);
        if (GetIdentity(directoryHandle) != expectedIdentity)
        {
            directoryHandle.Dispose();
            throw new InvalidOperationException(
                "A staged snapshot directory changed while it was being frozen.");
        }

        leases.Add(directoryHandle);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                throw new InvalidOperationException(
                    $"The staged snapshot contains an unsafe filesystem link: {entry.FullName}");
            }

            if (entry is DirectoryInfo directory)
            {
                using var identityHandle = OpenDirectory(
                    directory.FullName,
                    FileListDirectory | FileReadAttributes,
                    FileShareRead | FileShareWrite | FileShareDelete);
                var identity = GetIdentity(identityHandle);
                FreezeDirectory(directory.FullName, identity, leases);
                continue;
            }

            leases.Add(new FileStream(
                entry.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan));
        }
    }

    private static WindowsSnapshotTemporaryDirectory RequireTemporaryDirectory(
        SnapshotTemporaryDirectory temporaryDirectory)
    {
        if (temporaryDirectory is not WindowsSnapshotTemporaryDirectory temporary)
            throw new ArgumentException("Temporary directory belongs to another platform backend.");
        return temporary;
    }

    private static SafeFileHandle OpenDirectory(
        string path,
        uint desiredAccess,
        uint shareMode,
        bool followFinalLink = false)
    {
        var flags = FileFlagBackupSemantics |
            (followFinalLink ? 0 : FileFlagOpenReparsePoint);
        var handle = CreateFileW(
            ToExtendedPath(path),
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NativeFailure($"Unable to open snapshot directory '{path}'", error);
        }

        var information = GetInformation(handle);
        if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
            (!followFinalLink &&
             (information.FileAttributes & FileAttributeReparsePoint) != 0))
        {
            handle.Dispose();
            throw new InvalidOperationException(
                $"Snapshot handle is not a direct directory: {path}");
        }

        return handle;
    }

    private static SnapshotDirectoryIdentity GetIdentity(SafeFileHandle handle)
    {
        var information = GetInformation(handle);
        return new SnapshotDirectoryIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeFailure(
                "Unable to inspect a retained snapshot directory",
                Marshal.GetLastPInvokeError());
        }

        return information;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var requiredLength = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (requiredLength == 0)
        {
            throw NativeFailure(
                "Unable to resolve a retained directory handle",
                Marshal.GetLastPInvokeError());
        }

        var buffer = new StringBuilder(checked((int)requiredLength + 1));
        var actualLength = GetFinalPathNameByHandleW(
            handle,
            buffer,
            checked((uint)buffer.Capacity),
            0);
        if (actualLength == 0 || actualLength >= buffer.Capacity)
        {
            throw NativeFailure(
                "Unable to resolve a retained directory handle",
                Marshal.GetLastPInvokeError());
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        return Path.GetFullPath(path);
    }

    private static void RenameByHandle(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationLeaf)
    {
        ValidateLeaf(destinationLeaf);
        var nameBytes = Encoding.Unicode.GetBytes(destinationLeaf);
        var informationSize = Marshal.SizeOf<FileRenameInformation>();
        var nameOffset = Marshal.OffsetOf<FileRenameInformation>(
            nameof(FileRenameInformation.FileName)).ToInt32();
        var bufferSize = checked(informationSize + nameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var parentAddedRef = false;
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            destinationParent.DangerousAddRef(ref parentAddedRef);
            var information = new FileRenameInformation
            {
                ReplaceIfExists = 0,
                RootDirectory = destinationParent.DangerousGetHandle(),
                FileNameLength = checked((uint)nameBytes.Length),
                FileName = '\0',
            };
            Marshal.StructureToPtr(information, buffer, false);
            Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);

            var status = NtSetInformationFile(
                source,
                out _,
                buffer,
                (uint)bufferSize,
                FileRenameInformationClass);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                if (error is ErrorFileExists or ErrorAlreadyExists)
                {
                    throw new InvalidOperationException(
                        $"Destination already exists: {destinationLeaf}. It was not overwritten.");
                }

                throw NativeFailure("Unable to atomically publish the snapshot", error);
            }
        }
        finally
        {
            if (parentAddedRef)
                destinationParent.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateLeaf(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name is "." or ".." ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            name.Contains('\0') ||
            name.Contains(':'))
        {
            throw new InvalidOperationException(
                "A handle-relative publication received an invalid destination name.");
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

    private static InvalidOperationException NativeFailure(string operation, int error) =>
        new($"{operation}: {new Win32Exception(error).Message} (error {error}).");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInformation
    {
        public byte ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        [MarshalAs(UnmanagedType.U2)]
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr StatusOrPointer;
        public UIntPtr Information;
    }

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
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder? path,
        uint pathLength,
        uint flags);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(
        SafeFileHandle file,
        out IoStatusBlock ioStatusBlock,
        IntPtr information,
        uint length,
        int informationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}

internal sealed class WindowsSnapshotTemporaryDirectory : SnapshotTemporaryDirectory
{
    private readonly WindowsSnapshotDestinationDirectory _owner;
    private List<IDisposable>? _freezeLeases;

    public WindowsSnapshotTemporaryDirectory(
        string name,
        SafeFileHandle handle,
        SnapshotDirectoryIdentity identity,
        WindowsSnapshotDestinationDirectory owner)
        : base(name)
    {
        Handle = handle;
        Identity = identity;
        _owner = owner;
    }

    internal SafeFileHandle Handle { get; }

    internal SnapshotDirectoryIdentity Identity { get; }

    internal override string GetCurrentPath() => _owner.GetOwnedTemporaryPath(this);

    internal override void CreateDirectory(string relativePath) =>
        CreateRelativeDirectory(GetCurrentPath(), relativePath);

    internal override FileStream CreateNewFile(string relativePath) =>
        CreateRelativeFile(GetCurrentPath(), relativePath);

    internal void Freeze() => _owner.FreezeTree(this);

    internal void SetFreeze(List<IDisposable> leases)
    {
        if (_freezeLeases != null)
            throw new InvalidOperationException("The temporary snapshot is already frozen.");
        _freezeLeases = leases;
    }

    internal void ReleaseFreeze()
    {
        if (_freezeLeases == null)
            return;

        foreach (var lease in _freezeLeases)
            lease.Dispose();
        _freezeLeases = null;
    }

    public override void Dispose()
    {
        ReleaseFreeze();
        Handle.Dispose();
    }
}
