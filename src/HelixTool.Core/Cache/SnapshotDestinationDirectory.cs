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
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxFileType = 0x00000001;
    private const uint LinuxStatxHardLinkCount = 0x00000004;
    private const uint LinuxStatxInode = 0x00000100;
    private const int LinuxStatxBufferSize = 0x100;
    private const int MacFileStatusBufferSize = 144;
    private const uint MacRenameExclusive = 0x00000004;
    private const uint LinuxRenameNoReplace = 1;
    private const int AtCurrentWorkingDirectory = -100;

    // O_RDONLY is zero on every supported Unix. O_NONBLOCK keeps open(2) from waiting on a
    // FIFO or device planted at a snapshot path; asm-generic's 00004000 is shared by every
    // supported Linux architecture, and Darwin's sys/fcntl.h uses 0x0004.
    private const int UnixOpenReadOnly = 0x0;
    private const int LinuxOpenNonBlocking = 0x800;
    private const int MacOpenNonBlocking = 0x4;

    // F_GETFL and F_SETFL share these values on Linux and Darwin.
    private const int UnixGetStatusFlags = 3;
    private const int UnixSetStatusFlags = 4;

    // S_IFMT and S_IFREG from sys/stat.h; identical on Linux and Darwin.
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFileType = 0x8000;

    private const int SnapshotReadBufferSize = 81920;

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileTypeDisk = 0x00000001;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileAttributeTagInformationSize = 8;
    private const int FileIdInformationSize = 24;

    private const int ErrorExists = 17;
    private const int LinuxErrorFunctionNotImplemented = 38;
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
        EnsureSupportedPlatform();
        return new SnapshotDestinationDirectory(physicalPath);
    }

    internal static SnapshotDirectoryIdentity GetDirectoryIdentityNoFollow(string path)
    {
        EnsureSupportedPlatform();
        using var handle = OpenDirectory(path);
        return GetIdentity(handle);
    }

    internal static void RejectSourceIdentityInDestinationAncestors(
        string destinationParent,
        SnapshotDirectoryIdentity sourceRootIdentity,
        SnapshotDirectoryIdentity? sourceArtifactsIdentity)
    {
        EnsureSupportedPlatform();
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationParent));
        while (true)
        {
            SnapshotDirectoryIdentity identity;
            try
            {
                using var handle = OpenDirectory(current);
                identity = GetIdentity(handle);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or IOException or
                    UnauthorizedAccessException or NotSupportedException or
                    DllNotFoundException or EntryPointNotFoundException)
            {
                throw new InvalidOperationException(
                    $"Unable to verify destination-parent ancestor identity '{current}': " +
                    ex.Message,
                    ex);
            }

            if (identity == sourceRootIdentity)
            {
                throw new InvalidOperationException(
                    "Destination must not be the source cache root or a child of it. " +
                    $"A destination-parent ancestor has the source directory identity: {current}");
            }

            if (sourceArtifactsIdentity is { } artifactsIdentity &&
                identity == artifactsIdentity)
            {
                throw new InvalidOperationException(
                    "Destination must not be the source artifacts directory or a child of it. " +
                    $"A destination-parent ancestor has the artifacts directory identity: {current}");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
                return;

            var next = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            if (string.Equals(next, current, StringComparison.Ordinal))
                return;
            current = next;
        }
    }

    /// <summary>
    /// Opens a snapshot file for reading only after the opened handle itself is proven to
    /// reference a regular file with exactly one hard link. The open never blocks on a FIFO
    /// or device planted at <paramref name="path"/>, never follows a final symbolic link, and
    /// never consults the pathname a second time, so no check-to-use window exists.
    /// </summary>
    internal static FileStream OpenRegularFileWithExactlyOneLink(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        EnsureSupportedPlatform();

        var fullPath = Path.GetFullPath(path);
        var handle = OpenRegularFileHandle(fullPath);
        try
        {
            EnsureRegularFile(handle, fullPath);
            EnsureExactlyOneHardLink(handle, fullPath);
            if (!OperatingSystem.IsWindows())
                RestoreBlockingReads(handle, fullPath);

            // FileStream takes ownership: disposing the stream closes this descriptor.
            return new FileStream(handle, FileAccess.Read, SnapshotReadBufferSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void EnsureExactlyOneHardLink(
        SafeFileHandle handle,
        string path)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
            throw new InvalidOperationException("An open snapshot file handle is required.");

        var hardLinkCount = GetHardLinkCount(handle);
        if (hardLinkCount != 1)
        {
            throw new InvalidOperationException(
                $"Snapshot file must have exactly one hard link: {Path.GetFullPath(path)} " +
                $"(found {hardLinkCount}).");
        }
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

            var information = GetWindowsAttributeInformation(handle);
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

    private static SafeFileHandle OpenRegularFileHandle(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // FILE_FLAG_OPEN_REPARSE_POINT is the O_NOFOLLOW analogue: the final component is
            // opened literally so a reparse point can be rejected instead of traversed.
            var handle = CreateFileW(
                ToExtendedPath(path),
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw NativeFailure($"Unable to open snapshot file '{path}'", error);
            }

            return handle;
        }

        var flags = OperatingSystem.IsMacOS()
            ? UnixOpenReadOnly | MacOpenNonBlocking | MacOpenNoFollow | MacOpenCloseOnExec
            : GetLinuxOpenRegularFileFlags(RuntimeInformation.ProcessArchitecture);
        var descriptor = open(path, flags);
        if (descriptor < 0)
        {
            throw NativeFailure(
                $"Unable to open snapshot file '{path}'",
                Marshal.GetLastPInvokeError());
        }

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static int GetLinuxOpenRegularFileFlags(Architecture architecture)
    {
        // ARM and PowerPC preserve their historical open(2) values instead of asm-generic's.
        var noFollow = architecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or
                Architecture.Ppc64le =>
                LinuxArmPpcOpenNoFollow,
            Architecture.X86 or Architecture.X64 or Architecture.S390x or
                Architecture.LoongArch64 or Architecture.RiscV64 =>
                LinuxGenericOpenNoFollow,
            var unsupportedArchitecture => throw new PlatformNotSupportedException(
                $"Snapshot export does not define Linux open flags for {unsupportedArchitecture}."),
        };

        return UnixOpenReadOnly | LinuxOpenNonBlocking | noFollow | LinuxOpenCloseOnExec;
    }

    /// <summary>
    /// Proves the already-open handle references a regular file. Nothing here reopens or
    /// re-resolves the pathname, so the answer describes the object that will be read.
    /// </summary>
    private static void EnsureRegularFile(SafeFileHandle handle, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // FILE_TYPE_DISK excludes pipes, character devices, and consoles. Anything else,
            // including FILE_TYPE_UNKNOWN from a failed query, fails closed.
            if (GetFileType(handle) != FileTypeDisk)
            {
                throw new InvalidOperationException(
                    $"Snapshot file must be a regular file: {path}");
            }

            var information = GetWindowsAttributeInformation(handle);
            if ((information.FileAttributes & FileAttributeDirectory) != 0 ||
                (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Snapshot file must be a regular file: {path}");
            }

            return;
        }

        var status = GetUnixInformation(handle);
        if (!status.HasFileType)
        {
            throw new InvalidOperationException(
                "The filesystem did not provide a stable snapshot file type.");
        }

        var fileType = status.Mode & UnixFileTypeMask;
        if (fileType != UnixRegularFileType)
        {
            throw new InvalidOperationException(
                $"Snapshot file must be a regular file: {path} " +
                $"(file type 0x{fileType:x4}).");
        }
    }

    /// <summary>
    /// Drops O_NONBLOCK once the descriptor is proven regular. O_NONBLOCK is only needed to
    /// survive open(2); leaving it set would let read(2) return EAGAIN on exotic mounts.
    /// </summary>
    private static void RestoreBlockingReads(SafeFileHandle handle, string path)
    {
        var nonBlocking = OperatingSystem.IsMacOS() ? MacOpenNonBlocking : LinuxOpenNonBlocking;
        var addedReference = false;
        try
        {
            handle.DangerousAddRef(ref addedReference);
            var descriptor = checked((int)handle.DangerousGetHandle());

            var flags = FileControl(descriptor, UnixGetStatusFlags, 0);
            if (flags < 0)
            {
                throw NativeFailure(
                    $"Unable to read snapshot file status flags for '{path}'",
                    Marshal.GetLastPInvokeError());
            }

            if ((flags & nonBlocking) == 0)
                return;

            if (FileControl(descriptor, UnixSetStatusFlags, flags & ~nonBlocking) < 0)
            {
                throw NativeFailure(
                    $"Unable to clear non-blocking mode for snapshot file '{path}'",
                    Marshal.GetLastPInvokeError());
            }

            // Read the flags back instead of trusting the store. fcntl is variadic, so a
            // mismarshalled third argument can report success while changing nothing.
            var updated = FileControl(descriptor, UnixGetStatusFlags, 0);
            if (updated < 0 || (updated & nonBlocking) != 0)
            {
                throw new InvalidOperationException(
                    $"Unable to restore blocking reads for snapshot file: {path}");
            }
        }
        finally
        {
            if (addedReference)
                handle.DangerousRelease();
        }
    }

    private static int FileControl(int descriptor, int command, nint argument) =>
        OperatingSystem.IsMacOS()
            ? MacFileControl(descriptor, command, argument)
            : LinuxFileControl(descriptor, command, argument);

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
            return GetWindowsIdentity(handle);

        var status = GetUnixInformation(handle);
        if (!status.HasInode || status.Inode == 0)
        {
            throw new InvalidOperationException(
                "The filesystem did not provide stable snapshot file identity.");
        }

        return new SnapshotDirectoryIdentity(
            status.Device,
            status.Inode,
            0);
    }

    private static uint GetHardLinkCount(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var information = GetWindowsInformation(handle);
            if (information.NumberOfLinks == 0)
            {
                throw new InvalidOperationException(
                    "The filesystem did not provide a stable snapshot hard-link count.");
            }

            return information.NumberOfLinks;
        }

        var status = GetUnixInformation(handle);
        if (!status.HasHardLinkCount || status.HardLinkCount == 0)
        {
            throw new InvalidOperationException(
                "The filesystem did not provide a stable snapshot hard-link count.");
        }

        return status.HardLinkCount;
    }

    private static SnapshotDirectoryIdentity GetWindowsIdentity(SafeFileHandle handle)
    {
        if (!GetFileIdInformationByHandle(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var information,
                (uint)FileIdInformationSize))
        {
            throw NativeFailure(
                "Unable to inspect snapshot file identity",
                Marshal.GetLastPInvokeError());
        }

        var fileIdLow = information.FileId.IdentifierLow;
        var fileIdHigh = information.FileId.IdentifierHigh;
        // MS-FSCC reserves zero for unsupported 128-bit IDs and all-ones when a
        // unique 128-bit ID cannot be established. Neither value can prove identity.
        if ((fileIdLow == 0 && fileIdHigh == 0) ||
            (fileIdLow == ulong.MaxValue && fileIdHigh == ulong.MaxValue))
        {
            throw new InvalidOperationException(
                "The filesystem did not provide a unique 128-bit snapshot file identifier.");
        }

        return new SnapshotDirectoryIdentity(
            information.VolumeSerialNumber,
            fileIdLow,
            fileIdHigh);
    }

    private static UnixFileInformation GetUnixInformation(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Snapshot metadata inspection supports Windows, Linux, and macOS.");
        }

        var addedReference = false;
        try
        {
            handle.DangerousAddRef(ref addedReference);
            var descriptor = checked((int)handle.DangerousGetHandle());
            return OperatingSystem.IsLinux()
                ? GetLinuxInformation(descriptor)
                : GetMacInformation(descriptor);
        }
        finally
        {
            if (addedReference)
                handle.DangerousRelease();
        }
    }

    private static UnixFileInformation GetLinuxInformation(int descriptor)
    {
        // Linux's statx UAPI uses fixed-width fields and a 256-byte layout on every
        // architecture. Calling it through libc's stable syscall wrapper avoids requiring
        // the statx symbol added after the oldest supported libc. AT_EMPTY_PATH makes this
        // query the already-open file, not its name.
        if (LinuxStatxSystemCall(
                GetLinuxStatxSyscallNumber(RuntimeInformation.ProcessArchitecture),
                descriptor,
                string.Empty,
                LinuxAtEmptyPath,
                LinuxStatxFileType | LinuxStatxHardLinkCount | LinuxStatxInode,
                out var status) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == LinuxErrorFunctionNotImplemented)
            {
                throw new PlatformNotSupportedException(
                    "Snapshot metadata inspection requires Linux statx (kernel 4.11 or " +
                    "later) and a sandbox that permits the statx system call.");
            }

            throw NativeFailure(
                "Unable to inspect snapshot file metadata with statx",
                error);
        }

        return new UnixFileInformation(
            ((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
            status.Inode,
            status.HardLinkCount,
            (status.Mask & LinuxStatxInode) != 0,
            (status.Mask & LinuxStatxHardLinkCount) != 0,
            status.Mode,
            (status.Mask & LinuxStatxFileType) != 0);
    }

    private static nint GetLinuxStatxSyscallNumber(Architecture architecture) =>
        architecture switch
        {
            // arch/x86/entry/syscalls/syscall_64.tbl
            Architecture.X64 => 332,
            // arch/x86/entry/syscalls/syscall_32.tbl and
            // arch/powerpc/kernel/syscalls/syscall.tbl
            Architecture.X86 or Architecture.Ppc64le => 383,
            // arch/arm/tools/syscall.tbl
            Architecture.Arm or Architecture.Armv6 => 397,
            // include/uapi/asm-generic/unistd.h
            Architecture.Arm64 or Architecture.LoongArch64 or Architecture.RiscV64 => 291,
            // arch/s390/kernel/syscalls/syscall.tbl
            Architecture.S390x => 379,
            var unsupportedArchitecture => throw new PlatformNotSupportedException(
                $"Snapshot metadata inspection does not define the Linux statx syscall for " +
                $"{unsupportedArchitecture}."),
        };

    private static UnixFileInformation GetMacInformation(int descriptor)
    {
        MacFileStatus status;
        var result = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => MacFStatInode64(descriptor, out status),
            Architecture.Arm64 => MacFStat(descriptor, out status),
            var architecture => throw new PlatformNotSupportedException(
                $"Snapshot metadata inspection does not define the macOS stat ABI for " +
                $"{architecture}."),
        };
        if (result != 0)
        {
            throw NativeFailure(
                "Unable to inspect snapshot file metadata with fstat",
                Marshal.GetLastPInvokeError());
        }

        return new UnixFileInformation(
            unchecked((uint)status.Device),
            status.Inode,
            status.HardLinkCount,
            HasInode: true,
            HasHardLinkCount: true,
            Mode: status.Mode,
            HasFileType: true);
    }

    private static FileAttributeTagInformation GetWindowsAttributeInformation(
        SafeFileHandle handle)
    {
        if (!GetFileAttributeInformationByHandle(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out var information,
                (uint)FileAttributeTagInformationSize))
        {
            throw NativeFailure(
                "Unable to inspect snapshot file attributes",
                Marshal.GetLastPInvokeError());
        }

        return information;
    }

    private static ByHandleFileInformation GetWindowsInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw NativeFailure(
                "Unable to inspect snapshot file metadata",
                Marshal.GetLastPInvokeError());
        }

        return information;
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows() &&
            !OperatingSystem.IsLinux() &&
            !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Snapshot export supports Windows, Linux, and macOS.");
        }
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

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 0x09,
        FileIdInfo = 0x12,
    }

    [StructLayout(LayoutKind.Explicit, Size = FileAttributeTagInformationSize)]
    private struct FileAttributeTagInformation
    {
        [FieldOffset(0)]
        internal uint FileAttributes;

        [FieldOffset(4)]
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Explicit, Size = FileIdInformationSize)]
    private struct FileIdInformation
    {
        [FieldOffset(0)]
        internal ulong VolumeSerialNumber;

        [FieldOffset(8)]
        internal FileId128 FileId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FileId128
    {
        [FieldOffset(0)]
        internal ulong IdentifierLow;

        [FieldOffset(8)]
        internal ulong IdentifierHigh;
    }

    private readonly record struct UnixFileInformation(
        ulong Device,
        ulong Inode,
        uint HardLinkCount,
        bool HasInode,
        bool HasHardLinkCount,
        uint Mode,
        bool HasFileType);

    // include/uapi/linux/stat.h defines this architecture-independent ABI.
    [StructLayout(LayoutKind.Explicit, Size = LinuxStatxBufferSize)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0x00)]
        internal uint Mask;

        [FieldOffset(0x10)]
        internal uint HardLinkCount;

        [FieldOffset(0x1c)]
        internal ushort Mode;

        [FieldOffset(0x20)]
        internal ulong Inode;

        [FieldOffset(0x88)]
        internal uint DeviceMajor;

        [FieldOffset(0x8c)]
        internal uint DeviceMinor;
    }

    // Darwin's 64-bit struct stat layout is shared by arm64 and x86_64. The
    // latter exposes it through the fstat$INODE64 symbol for ABI compatibility.
    [StructLayout(LayoutKind.Explicit, Size = MacFileStatusBufferSize)]
    private struct MacFileStatus
    {
        [FieldOffset(0)]
        internal int Device;

        [FieldOffset(4)]
        internal ushort Mode;

        [FieldOffset(6)]
        internal ushort HardLinkCount;

        [FieldOffset(8)]
        internal ulong Inode;
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        out FileAttributeTagInformation information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        out FileIdInformation information,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string newFileName,
        uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    // glibc and musl declare fcntl variadic, but every Linux architecture this tool supports
    // passes variadic integer arguments in the same registers as named ones, so a fixed
    // arity declaration is ABI-correct. Only F_GETFL and F_SETFL are issued through it, and
    // the third argument is pointer-sized because glibc reads it with va_arg(ap, void *).
    [DllImport(
        "libc",
        EntryPoint = "fcntl",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern int LinuxFileControl(int descriptor, int command, nint argument);

    // Darwin's fcntl is genuinely variadic and Apple's arm64 ABI passes variadic arguments on
    // the stack, so a fixed arity declaration would hand libc an uninitialized third argument.
    // __fcntl is the non-variadic libsystem_kernel entry point that wrapper forwards to.
    [DllImport(
        "libc",
        EntryPoint = "__fcntl",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern int MacFileControl(int descriptor, int command, nint argument);

    [DllImport(
        "libc",
        EntryPoint = "syscall",
        CallingConvention = CallingConvention.Cdecl,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern nint LinuxStatxSystemCall(
        nint number,
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string path,
        int flags,
        uint mask,
        out LinuxFileStatus status);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFStat(int descriptor, out MacFileStatus status);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacFStatInode64(int descriptor, out MacFileStatus status);

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

internal readonly record struct SnapshotDirectoryIdentity(
    ulong Volume,
    ulong FileIdLow,
    ulong FileIdHigh);
