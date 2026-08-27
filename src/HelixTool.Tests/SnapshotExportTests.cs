using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HelixTool.Core.Cache;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Sdk;

namespace HelixTool.Tests;

file sealed record ArtifactFixture(string CacheKey, string RelativePath, byte[] Content);

file sealed record SourceFixture(
    string Root,
    string DatabasePath,
    IReadOnlyList<ArtifactFixture> Artifacts);

internal readonly record struct CheckpointRow(int Busy, int WalPages, int CheckpointedPages);

internal sealed class CheckpointReadinessStateMachine
{
    private readonly TimeSpan _readinessTimeout;
    private readonly Func<TimeSpan> _elapsed;

    public CheckpointReadinessStateMachine(
        TimeSpan readinessTimeout,
        Func<TimeSpan>? elapsed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readinessTimeout, TimeSpan.Zero);
        _readinessTimeout = readinessTimeout;
        if (elapsed is not null)
        {
            _elapsed = elapsed;
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        _elapsed = () => Stopwatch.GetElapsedTime(startedAt);
    }

    public bool IsReady { get; private set; }

    public bool Observe(CheckpointRow row)
    {
        if (row.Busy is < 0 or > 1)
            throw InvalidRow(row, "busy must be 0 or 1");

        var noCurrentWal = row.WalPages == -1 && row.CheckpointedPages == -1;
        if (!noCurrentWal)
        {
            if (row.WalPages < 0 || row.CheckpointedPages < 0)
                throw InvalidRow(row, "negative page counts must be exactly (-1, -1)");
            if (row.CheckpointedPages > row.WalPages)
                throw InvalidRow(row, "checkpointed pages cannot exceed WAL pages");
        }

        if (!IsReady && _elapsed() >= _readinessTimeout)
        {
            throw new TimeoutException(
                $"No positive WAL checkpoint progress within {_readinessTimeout}.");
        }

        if (noCurrentWal)
            return false;

        var madeProgress =
            row.Busy == 0 && row.WalPages > 0 && row.CheckpointedPages > 0;
        if (madeProgress)
            IsReady = true;
        return madeProgress;
    }

    private static InvalidOperationException InvalidRow(CheckpointRow row, string reason) =>
        new(
            $"Invalid WAL checkpoint row ({row.Busy}, {row.WalPages}, " +
            $"{row.CheckpointedPages}): {reason}.");
}

file sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

file sealed class WindowsDirectoryOplock : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FsctlRequestOplock = 0x00090240;
    private const uint OplockLevelCacheRead = 0x00000001;
    private const uint OplockLevelCacheHandle = 0x00000002;
    private const uint RequestOplockInputFlagRequest = 0x00000001;
    private const ushort RequestOplockCurrentVersion = 1;
    private const int ErrorIoPending = 997;

    private readonly EventWaitHandle _breakEvent =
        new(initialState: false, EventResetMode.ManualReset);
    private SafeFileHandle? _handle;
    private IntPtr _inputBuffer;
    private IntPtr _outputBuffer;
    private IntPtr _overlapped;
    private bool _requestPending;
    private bool _requestCompleted;
    private int _disposed;

    public WindowsDirectoryOplock(string path)
    {
        try
        {
            _handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOverlapped,
                IntPtr.Zero);
            if (_handle.IsInvalid)
            {
                throw NativeFailure(
                    $"Unable to open staged snapshot directory '{path}' for an oplock",
                    Marshal.GetLastPInvokeError());
            }

            var inputSize = Marshal.SizeOf<RequestOplockInputBuffer>();
            var outputSize = Marshal.SizeOf<RequestOplockOutputBuffer>();
            _inputBuffer = Marshal.AllocHGlobal(inputSize);
            _outputBuffer = Marshal.AllocHGlobal(outputSize);
            _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsOverlapped>());
            Marshal.StructureToPtr(
                new RequestOplockInputBuffer
                {
                    StructureVersion = RequestOplockCurrentVersion,
                    StructureLength = checked((ushort)inputSize),
                    RequestedOplockLevel =
                        OplockLevelCacheRead | OplockLevelCacheHandle,
                    Flags = RequestOplockInputFlagRequest,
                },
                _inputBuffer,
                fDeleteOld: false);
            Marshal.StructureToPtr(
                new RequestOplockOutputBuffer
                {
                    StructureVersion = RequestOplockCurrentVersion,
                    StructureLength = checked((ushort)outputSize),
                },
                _outputBuffer,
                fDeleteOld: false);
            Marshal.StructureToPtr(
                new WindowsOverlapped
                {
                    EventHandle = _breakEvent.SafeWaitHandle.DangerousGetHandle(),
                },
                _overlapped,
                fDeleteOld: false);

            if (DeviceIoControl(
                    _handle,
                    FsctlRequestOplock,
                    _inputBuffer,
                    (uint)inputSize,
                    _outputBuffer,
                    (uint)outputSize,
                    IntPtr.Zero,
                    _overlapped))
            {
                throw new XunitException(
                    "The staged-directory oplock completed synchronously instead of being granted.");
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorIoPending)
            {
                throw NativeFailure(
                    "Unable to request an oplock on the staged snapshot directory",
                    error);
            }

            _requestPending = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void WaitForBreak(TimeSpan timeout)
    {
        if (!_breakEvent.WaitOne(timeout))
            throw new XunitException("Timed out waiting for the publication oplock to break.");

        if (!GetOverlappedResult(_handle!, _overlapped, out _, wait: false))
        {
            throw NativeFailure(
                "The staged-directory oplock did not complete successfully",
                Marshal.GetLastPInvokeError());
        }

        _requestCompleted = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_requestPending && !_requestCompleted)
        {
            _ = CancelIoEx(_handle!, _overlapped);
            _requestCompleted = _breakEvent.WaitOne(TimeSpan.FromSeconds(5));
        }

        _handle?.Dispose();
        if (_requestCompleted || !_requestPending)
        {
            if (_inputBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_inputBuffer);
            if (_outputBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_outputBuffer);
            if (_overlapped != IntPtr.Zero)
                Marshal.FreeHGlobal(_overlapped);
            _breakEvent.Dispose();
        }
    }

    private static XunitException NativeFailure(string operation, int error) =>
        new($"{operation} (error {error}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct RequestOplockInputBuffer
    {
        public ushort StructureVersion;
        public ushort StructureLength;
        public uint RequestedOplockLevel;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RequestOplockOutputBuffer
    {
        public ushort StructureVersion;
        public ushort StructureLength;
        public uint OriginalOplockLevel;
        public uint NewOplockLevel;
        public uint Flags;
        public uint AccessMode;
        public ushort ShareMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr EventHandle;
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
    private static extern bool DeviceIoControl(
        SafeFileHandle file,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        IntPtr bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle file,
        IntPtr overlapped,
        out uint bytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
}

internal sealed class CaseSensitiveFileSystemFactAttribute : FactAttribute
{
    private static readonly bool SupportsDistinctCaseOnlyDirectories =
        DetectDistinctCaseOnlyDirectories();

    public CaseSensitiveFileSystemFactAttribute()
    {
        if (!SupportsDistinctCaseOnlyDirectories)
        {
            Skip =
                "The test output filesystem aliases case-only directory spellings; " +
                "this test requires distinct case-only siblings.";
        }
    }

    private static bool DetectDistinctCaseOnlyDirectories()
    {
        var probeRoot = Path.Combine(
            AppContext.BaseDirectory,
            "snapshot-test-data",
            $".case-sensitivity-probe-{Guid.NewGuid():N}");
        var lower = Path.Combine(probeRoot, "case");
        var upper = Path.Combine(probeRoot, "CASE");
        try
        {
            Directory.CreateDirectory(lower);
            return !Directory.Exists(upper);
        }
        finally
        {
            try
            {
                Directory.Delete(probeRoot, recursive: true);
            }
            catch
            {
                // Best effort only.
            }
        }
    }
}

file static class SnapshotTestHelper
{
    private const string CreatedAt = "2026-08-26T00:00:00.0000000+00:00";
    private const string ExpiresAt = "2099-08-26T00:00:00.0000000+00:00";

    public static string CreateWorkspace(string tag)
    {
        var workspace = Path.Combine(
            AppContext.BaseDirectory,
            "snapshot-test-data",
            $"{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    public static SourceFixture CreateSource(
        string workspace,
        int metadataRows = 3,
        int artifactRows = 2,
        bool useWal = true,
        string? sourceRoot = null)
    {
        sourceRoot ??= Path.Combine(workspace, "cache");
        Directory.CreateDirectory(sourceRoot);
        var artifactsRoot = Path.Combine(sourceRoot, "artifacts");
        Directory.CreateDirectory(artifactsRoot);
        var databasePath = Path.Combine(sourceRoot, "cache.db");

        using (var connection = OpenConnection(databasePath, SqliteOpenMode.ReadWriteCreate))
        {
            ExecuteNonQuery(connection, useWal
                ? "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;"
                : "PRAGMA journal_mode=DELETE;");
            ExecuteNonQuery(connection, """
                CREATE TABLE cache_metadata (
                    cache_key TEXT PRIMARY KEY,
                    json_value TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    job_id TEXT NOT NULL
                );
                CREATE TABLE cache_artifacts (
                    cache_key TEXT PRIMARY KEY,
                    file_path TEXT NOT NULL,
                    file_size INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    last_accessed TEXT NOT NULL,
                    job_id TEXT NOT NULL
                );
                CREATE TABLE cache_job_state (
                    job_id TEXT PRIMARY KEY,
                    is_completed INTEGER NOT NULL,
                    finished_at TEXT,
                    cached_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL
                );
                PRAGMA user_version=1;
                """);

            for (var i = 0; i < metadataRows; i++)
            {
                InsertMetadata(
                    connection,
                    $"baseline:metadata:{i:D4}",
                    $"{{\"baseline\":{i}}}",
                    "baseline");
            }

            using var stateCommand = connection.CreateCommand();
            stateCommand.CommandText = """
                INSERT INTO cache_job_state
                    (job_id, is_completed, finished_at, cached_at, expires_at)
                VALUES ('baseline-job', 1, @created, @created, @expires);
                """;
            stateCommand.Parameters.AddWithValue("@created", CreatedAt);
            stateCommand.Parameters.AddWithValue("@expires", ExpiresAt);
            stateCommand.ExecuteNonQuery();
        }

        var artifacts = new List<ArtifactFixture>();
        for (var i = 0; i < artifactRows; i++)
        {
            var relativePath = Path.Combine($"seed-{i:D4}", $"artifact-{i:D4}.bin");
            var content = new byte[] { 0x48, 0x4c, 0x58, (byte)i };
            var fullPath = Path.Combine(artifactsRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, content);

            var artifact = new ArtifactFixture(
                $"baseline:artifact:{i:D4}",
                relativePath,
                content);
            artifacts.Add(artifact);
            InsertArtifactReference(
                databasePath,
                artifact.CacheKey,
                artifact.RelativePath,
                artifact.Content.Length);
        }

        return new SourceFixture(sourceRoot, databasePath, artifacts);
    }

    public static SqliteConnection OpenConnection(
        string databasePath,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static void InsertMetadata(
        SqliteConnection connection,
        string cacheKey,
        string jsonValue,
        string jobId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cache_metadata
                (cache_key, json_value, created_at, expires_at, job_id)
            VALUES (@key, @value, @created, @expires, @jobId);
            """;
        command.Parameters.AddWithValue("@key", cacheKey);
        command.Parameters.AddWithValue("@value", jsonValue);
        command.Parameters.AddWithValue("@created", CreatedAt);
        command.Parameters.AddWithValue("@expires", ExpiresAt);
        command.Parameters.AddWithValue("@jobId", jobId);
        command.ExecuteNonQuery();
    }

    public static void InsertArtifactReference(
        string databasePath,
        string cacheKey,
        string relativePath,
        long fileSize)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cache_artifacts
                (cache_key, file_path, file_size, created_at, last_accessed, job_id)
            VALUES (@key, @path, @size, @created, @created, 'baseline');
            """;
        command.Parameters.AddWithValue("@key", cacheKey);
        command.Parameters.AddWithValue("@path", relativePath);
        command.Parameters.AddWithValue("@size", fileSize);
        command.Parameters.AddWithValue("@created", CreatedAt);
        command.ExecuteNonQuery();
    }

    public static void UpdateArtifactSize(string databasePath, string cacheKey, long size)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cache_artifacts SET file_size=@size WHERE cache_key=@key;";
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@key", cacheKey);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    public static string[] FingerprintTree(string root)
    {
        if (!Directory.Exists(root))
            return [];

        var entries = new List<string>();
        FingerprintDirectory(root, root, entries);
        return entries.Order(StringComparer.Ordinal).ToArray();
    }

    private static void FingerprintDirectory(string root, string directory, List<string> entries)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var info = (attributes & FileAttributes.Directory) != 0
                    ? (FileSystemInfo)new DirectoryInfo(path)
                    : new FileInfo(path);
                entries.Add($"L:{relative}:{info.LinkTarget}");
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                entries.Add($"D:{relative}");
                FingerprintDirectory(root, path, entries);
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            entries.Add($"F:{relative}:{new FileInfo(path).Length}:{hash}");
        }
    }

    public static string[] ParentEntries(string parent)
    {
        if (!Directory.Exists(parent))
            return [];

        return Directory.EnumerateFileSystemEntries(parent)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    public static void CreateDistinctCaseOnlySibling(
        string existingDirectory,
        string caseOnlySibling)
    {
        Assert.True(Directory.Exists(existingDirectory));
        Assert.NotEqual(existingDirectory, caseOnlySibling);
        Assert.Equal(existingDirectory, caseOnlySibling, ignoreCase: true);

        if (Directory.Exists(caseOnlySibling))
        {
            throw new XunitException(
                "The current filesystem does not support distinct case-only sibling " +
                $"directories ('{existingDirectory}' and '{caseOnlySibling}').");
        }

        Directory.CreateDirectory(caseOnlySibling);
        var markerName = $".case-sibling-probe-{Guid.NewGuid():N}";
        var siblingMarker = Path.Combine(caseOnlySibling, markerName);
        var existingMarker = Path.Combine(existingDirectory, markerName);
        File.WriteAllText(siblingMarker, "case-sensitive");
        try
        {
            if (File.Exists(existingMarker))
            {
                throw new XunitException(
                    "The current filesystem aliases case-only directory spellings; " +
                    "a distinct case-only sibling topology cannot be created.");
            }
        }
        finally
        {
            File.Delete(siblingMarker);
        }
    }

    public static async Task<InvalidOperationException> AssertRejectedWithoutPublicationAsync(
        SourceFixture source,
        string destination,
        CancellationToken cancellationToken = default,
        bool assertSourceUnchanged = true)
    {
        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination)!;
        var sourceBefore = FingerprintTree(source.Root);
        var parentBefore = ParentEntries(parent);
        var destinationExisted = Directory.Exists(fullDestination) || File.Exists(fullDestination);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync(
                source.Root,
                destination,
                ct: cancellationToken));

        Assert.Equal(destinationExisted, Directory.Exists(fullDestination) || File.Exists(fullDestination));
        if (assertSourceUnchanged)
            Assert.Equal(sourceBefore, FingerprintTree(source.Root));
        Assert.Equal(parentBefore, ParentEntries(parent));
        return exception;
    }

    public static async Task<ExportResult> ExportAndAssertAtomicPublicationAsync(
        SourceFixture source,
        string destination,
        IProgress<string>? progress = null)
    {
        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination)!;
        var before = ParentEntries(parent);

        var result = await SnapshotExporter.ExportAsync(source.Root, destination, progress);

        var expected = before
            .Append(Path.GetFileName(fullDestination))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, ParentEntries(parent));
        return result;
    }

    public static void AssertFinalLayout(string destination)
    {
        var entries = Directory.EnumerateFileSystemEntries(destination)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "artifacts", "cache.db" }, entries);
        Assert.False(File.Exists(Path.Combine(destination, "cache.db-wal")));
        Assert.False(File.Exists(Path.Combine(destination, "cache.db-shm")));
    }

    public static async Task CreateDirectoryAliasAsync(string alias, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add("/J");
        process.StartInfo.ArgumentList.Add(alias);
        process.StartInfo.ArgumentList.Add(target);

        Assert.True(process.Start(), "Failed to start cmd.exe for junction creation.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Windows junction creation failed with exit code {process.ExitCode}: {output} {error}");
    }

    public static async Task CreateFileAliasAsync(string alias, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(alias, target);
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add(alias);
        process.StartInfo.ArgumentList.Add(target);

        Assert.True(process.Start(), "Failed to start cmd.exe for symbolic-link creation.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Windows symbolic-link creation failed with exit code {process.ExitCode}: " +
            $"{output} {error}");
    }

    public static void DeleteDirectoryAlias(string alias)
    {
        try
        {
            new DirectoryInfo(alias).Delete();
        }
        catch
        {
            // Cleanup is best effort; the containing workspace cleanup is the final fallback.
        }
    }

    public static void DeleteFileAlias(string alias)
    {
        try
        {
            new FileInfo(alias).Delete();
        }
        catch
        {
            // Cleanup is best effort; the containing workspace cleanup is the final fallback.
        }
    }
}

public class SnapshotExporterTests : IDisposable
{
    private readonly List<string> _workspaces = [];

    private string Workspace(string tag)
    {
        var workspace = SnapshotTestHelper.CreateWorkspace(tag);
        _workspaces.Add(workspace);
        return workspace;
    }

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    [Fact]
    public async Task Export_AtomicPublicationPreservesStagedIdentityAndContent()
    {
        const string finalizationMessage = "Finalizing snapshot (atomic rename)...";
        var workspace = Workspace("atomic-publication");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var destinationParent = Path.Combine(workspace, "publish");
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, "snapshot");
        string? temporaryPath = null;
        (uint Volume, ulong FileId)? temporaryIdentity = null;
        var finalizationReports = 0;
        var progress = new SynchronousProgress<string>(message =>
        {
            if (!string.Equals(message, finalizationMessage, StringComparison.Ordinal))
                return;

            Assert.Equal(1, Interlocked.Increment(ref finalizationReports));
            var temporaryLeaf = Assert.Single(
                SnapshotTestHelper.ParentEntries(destinationParent),
                entry => entry.StartsWith("snapshot.tmp.", StringComparison.Ordinal));
            temporaryPath = Path.Combine(destinationParent, temporaryLeaf);
            if (OperatingSystem.IsWindows())
            {
                using var temporaryHandle = File.OpenHandle(
                    Path.Combine(temporaryPath, "cache.db"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                temporaryIdentity = GetWindowsFileIdentity(temporaryHandle);
            }
        });

        var result = await SnapshotTestHelper.ExportAndAssertAtomicPublicationAsync(
            source,
            destination,
            progress);

        Assert.Equal(1, finalizationReports);
        Assert.NotNull(temporaryPath);
        Assert.False(SnapshotExporter.PathEntryExists(temporaryPath));
        Assert.True(Directory.Exists(destination));
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(temporaryIdentity);
            using var destinationHandle = File.OpenHandle(
                Path.Combine(destination, "cache.db"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Assert.Equal(
                temporaryIdentity.Value,
                GetWindowsFileIdentity(destinationHandle));
        }

        SnapshotTestHelper.AssertFinalLayout(destination);
        Assert.Equal(Path.GetFullPath(destination), result.Destination);
        Assert.Equal(source.Artifacts.Count, result.ArtifactCount);
        Assert.Equal(
            new FileInfo(Path.Combine(destination, "cache.db")).Length,
            result.DbSizeBytes);
        foreach (var artifact in source.Artifacts)
        {
            Assert.Equal(
                artifact.Content,
                File.ReadAllBytes(Path.Combine(
                    destination,
                    "artifacts",
                    artifact.RelativePath)));
        }

        var validation = await SnapshotValidator.ValidateAsync(destination);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public async Task Export_EmptyArtifacts_StillCreatesArtifactsDirectory()
    {
        var workspace = Workspace("empty-artifacts");
        var source = SnapshotTestHelper.CreateSource(workspace, artifactRows: 0);
        var destination = Path.Combine(workspace, "snapshot");

        var result = await SnapshotTestHelper.ExportAndAssertAtomicPublicationAsync(
            source,
            destination);

        Assert.Equal(0, result.ArtifactCount);
        SnapshotTestHelper.AssertFinalLayout(destination);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(destination, "artifacts")));
    }

    [Fact]
    public async Task Export_CopiesDistinctReferencedArtifacts_AndOmitsOrphans()
    {
        var workspace = Workspace("artifact-correspondence");
        var source = SnapshotTestHelper.CreateSource(workspace, artifactRows: 1);
        var artifact = Assert.Single(source.Artifacts);
        SnapshotTestHelper.InsertArtifactReference(
            source.DatabasePath,
            "duplicate-reference",
            artifact.RelativePath,
            artifact.Content.Length);

        var orphanPath = Path.Combine(source.Root, "artifacts", "orphan", "unreferenced.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        File.WriteAllBytes(orphanPath, [0x01, 0x02, 0x03]);

        var destination = Path.Combine(workspace, "snapshot");
        var result = await SnapshotTestHelper.ExportAndAssertAtomicPublicationAsync(
            source,
            destination);

        Assert.Equal(1, result.ArtifactCount);
        var copiedFiles = Directory.GetFiles(
            Path.Combine(destination, "artifacts"),
            "*",
            SearchOption.AllDirectories);
        var copied = Assert.Single(copiedFiles);
        Assert.Equal(artifact.RelativePath, Path.GetRelativePath(
            Path.Combine(destination, "artifacts"),
            copied));
        Assert.Equal(artifact.Content, File.ReadAllBytes(copied));
        Assert.False(File.Exists(Path.Combine(destination, "artifacts", "orphan", "unreferenced.bin")));
    }

    [Fact]
    public async Task Export_MissingSourceRoot_IsRejectedWithoutPublicationResidue()
    {
        var workspace = Workspace("missing-source");
        var sourceRoot = Path.Combine(workspace, "missing-cache");
        var source = new SourceFixture(
            sourceRoot,
            Path.Combine(sourceRoot, "cache.db"),
            []);
        var destination = Path.Combine(workspace, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        Assert.Contains(
            "source cache root does not exist",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(sourceRoot));
    }

    [Fact]
    public async Task Export_MissingSourceDatabase_IsRejectedWithoutPublicationResidue()
    {
        var workspace = Workspace("missing-database");
        var sourceRoot = Path.Combine(workspace, "cache");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "sentinel.txt"), "unchanged");
        var source = new SourceFixture(
            sourceRoot,
            Path.Combine(sourceRoot, "cache.db"),
            []);
        var destination = Path.Combine(workspace, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        Assert.Contains(
            "source cache database not found",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_MissingDestinationParent_IsRejectedWithoutCreatingParentOrResidue()
    {
        var workspace = Workspace("missing-destination-parent");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        var missingParent = Path.Combine(workspace, "missing-parent");
        var destination = Path.Combine(missingParent, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        Assert.Contains(
            "destination parent directory does not exist",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(missingParent));
    }

    [Fact]
    public async Task Export_SchemaVersionZero_IsRejectedWithoutPublicationResidue()
    {
        var workspace = Workspace("schema-version-zero");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        using (var connection = SnapshotTestHelper.OpenConnection(source.DatabasePath))
            SnapshotTestHelper.ExecuteNonQuery(connection, "PRAGMA user_version=0;");
        var destination = Path.Combine(workspace, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        Assert.Contains("schema version 0", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_UnsupportedSchemaVersion_IsRejectedWithoutPublicationResidue()
    {
        var workspace = Workspace("unsupported-schema-version");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        using (var connection = SnapshotTestHelper.OpenConnection(source.DatabasePath))
            SnapshotTestHelper.ExecuteNonQuery(connection, "PRAGMA user_version=42;");
        var destination = Path.Combine(workspace, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        Assert.Contains("schema version 42", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected 1", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_MissingRequiredTable_IsRejectedWithoutPublicationResidue()
    {
        var workspace = Workspace("missing-required-table");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        using (var connection = SnapshotTestHelper.OpenConnection(source.DatabasePath))
            SnapshotTestHelper.ExecuteNonQuery(connection, "DROP TABLE cache_job_state;");
        var destination = Path.Combine(workspace, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        Assert.Contains("missing expected table", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cache_job_state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_MissingReferencedArtifact_FailsWithoutPublicationResidue()
    {
        var workspace = Workspace("missing-artifact");
        var source = SnapshotTestHelper.CreateSource(workspace, artifactRows: 1);
        var artifact = Assert.Single(source.Artifacts);
        File.Delete(Path.Combine(source.Root, "artifacts", artifact.RelativePath));
        var destination = Path.Combine(workspace, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination,
            assertSourceUnchanged: false);

        Assert.Contains("artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [CaseSensitiveFileSystemFact]
    public async Task Export_ArtifactLinkIntoCaseOnlySibling_IsRejectedWithoutPublicationResidue()
    {
        var workspace = Workspace("case-artifact-link");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 1,
            useWal: false);
        var artifact = Assert.Single(source.Artifacts);
        var artifactsRoot = Path.Combine(source.Root, "artifacts");
        var caseOnlySibling = Path.Combine(source.Root, "ARTIFACTS");
        SnapshotTestHelper.CreateDistinctCaseOnlySibling(
            artifactsRoot,
            caseOnlySibling);

        var externalArtifact = Path.Combine(caseOnlySibling, artifact.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(externalArtifact)!);
        File.WriteAllBytes(externalArtifact, artifact.Content);

        var referencedArtifact = Path.Combine(artifactsRoot, artifact.RelativePath);
        File.Delete(referencedArtifact);
        await SnapshotTestHelper.CreateFileAliasAsync(referencedArtifact, externalArtifact);
        try
        {
            var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
                source,
                Path.Combine(workspace, "snapshot"));

            Assert.Contains("artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("escape", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SnapshotTestHelper.DeleteFileAlias(referencedArtifact);
        }
    }

    [Fact]
    public async Task Export_PersistedArtifactSizeMismatch_FailsWithoutPublicationResidue()
    {
        var workspace = Workspace("artifact-size");
        var source = SnapshotTestHelper.CreateSource(workspace, artifactRows: 1);
        var artifact = Assert.Single(source.Artifacts);
        SnapshotTestHelper.UpdateArtifactSize(
            source.DatabasePath,
            artifact.CacheKey,
            artifact.Content.Length + 1);
        var destination = Path.Combine(workspace, "snapshot");

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination,
            assertSourceUnchanged: false);

        Assert.Contains("size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_ExistingDestinationAndSentinel_AreUntouched()
    {
        var workspace = Workspace("existing");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var destination = Path.Combine(workspace, "snapshot");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "sentinel.txt");
        File.WriteAllText(sentinel, "do-not-overwrite");
        var destinationBefore = SnapshotTestHelper.FingerprintTree(destination);

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(destinationBefore, SnapshotTestHelper.FingerprintTree(destination));
    }

    [Fact]
    public async Task Export_PreCanceled_FailsWithoutResidueOrSourceMutation()
    {
        var workspace = Workspace("canceled");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var destination = Path.Combine(workspace, "snapshot");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var sourceBefore = SnapshotTestHelper.FingerprintTree(source.Root);
        var parentBefore = SnapshotTestHelper.ParentEntries(workspace);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SnapshotExporter.ExportAsync(source.Root, destination, ct: cancellation.Token));

        Assert.False(Directory.Exists(destination));
        Assert.Equal(sourceBefore, SnapshotTestHelper.FingerprintTree(source.Root));
        Assert.Equal(parentBefore, SnapshotTestHelper.ParentEntries(workspace));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("artifacts")]
    [InlineData("source-child")]
    [InlineData("artifacts-child")]
    [InlineData("relative-source")]
    [InlineData("relative-artifacts-child")]
    public async Task Export_ContainmentMatrix_RejectsLexicalAliasesBeforePublication(string scenario)
    {
        var workspace = Workspace($"boundary-{scenario}");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var destination = scenario switch
        {
            "source" => source.Root,
            "artifacts" => Path.Combine(source.Root, "artifacts"),
            "source-child" => Path.Combine(source.Root, "snapshot"),
            "artifacts-child" => Path.Combine(source.Root, "artifacts", "snapshot"),
            "relative-source" => Path.Combine(
                Path.GetRelativePath(Directory.GetCurrentDirectory(), source.Root),
                ".",
                "unused",
                ".."),
            "relative-artifacts-child" => Path.Combine(
                Path.GetRelativePath(Directory.GetCurrentDirectory(), source.Root),
                ".",
                "artifacts",
                "..",
                "artifacts",
                "snapshot"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
            source,
            destination);

        if (scenario is "source" or "artifacts")
            Assert.DoesNotContain("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsEqualOrDescendant_FilesystemRoot_IsEqualToItself()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))
            ?? throw new InvalidOperationException("The test base directory has no filesystem root.");

        Assert.True(SnapshotExporter.IsEqualOrDescendant(filesystemRoot, filesystemRoot));
    }

    [Fact]
    public void IsEqualOrDescendant_FilesystemRoot_ContainsPathBeneathRoot()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))
            ?? throw new InvalidOperationException("The test base directory has no filesystem root.");
        var descendant = Path.Combine(
            filesystemRoot,
            "snapshot-export-root-regression",
            "descendant");

        Assert.True(SnapshotExporter.IsEqualOrDescendant(descendant, filesystemRoot));
    }

    [Fact]
    public void IsEqualOrDescendant_CaseOnlySibling_DoesNotProveContainmentOutsideWindows()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artifacts");
        var caseOnlySiblingChild =
            Path.Combine(AppContext.BaseDirectory, "ARTIFACTS", "artifact.bin");

        Assert.Equal(
            OperatingSystem.IsWindows(),
            SnapshotExporter.IsEqualOrDescendant(caseOnlySiblingChild, root));
    }

    [Fact]
    public void CouldBeEqualOrDescendant_CaseOnlySibling_IsConservativeOnMacOSAndWindows()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artifacts");
        var caseOnlySiblingChild =
            Path.Combine(AppContext.BaseDirectory, "ARTIFACTS", "artifact.bin");

        Assert.Equal(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            SnapshotExporter.CouldBeEqualOrDescendant(caseOnlySiblingChild, root));
    }

    [Fact]
    public async Task Export_SiblingPrefixDestination_Succeeds()
    {
        var workspace = Workspace("sibling-prefix");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            sourceRoot: Path.Combine(workspace, "cache"));
        var destination = Path.Combine(workspace, "cache-copy");

        await SnapshotTestHelper.ExportAndAssertAtomicPublicationAsync(source, destination);

        SnapshotTestHelper.AssertFinalLayout(destination);
    }

    [Fact]
    public async Task Export_CaseOnlyDestination_UsesConservativeMacOSAndWindowsBoundary()
    {
        var workspace = Workspace("case-boundary");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            sourceRoot: Path.Combine(workspace, "cache"));
        var caseOnlySourceSpelling = Path.Combine(workspace, "CACHE");
        var destination = Path.Combine(caseOnlySourceSpelling, "snapshot");
        var aliasesSource = Directory.Exists(caseOnlySourceSpelling);
        var usesConservativeBoundary =
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        if (!aliasesSource)
        {
            SnapshotTestHelper.CreateDistinctCaseOnlySibling(
                source.Root,
                caseOnlySourceSpelling);
        }

        if (aliasesSource || usesConservativeBoundary)
        {
            var exception = await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
                source,
                destination);
            Assert.Contains("source", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var databaseBefore = ReadLogicalDatabaseState(source.DatabasePath);
            var sourceBefore = FingerprintPersistentSourceTree(source.Root);
            Directory.CreateDirectory(caseOnlySourceSpelling);
            Assert.True(Directory.Exists(source.Root));
            Assert.True(Directory.Exists(caseOnlySourceSpelling));

            await SnapshotTestHelper.ExportAndAssertAtomicPublicationAsync(source, destination);

            SnapshotTestHelper.AssertFinalLayout(destination);
            Assert.Equal(sourceBefore, FingerprintPersistentSourceTree(source.Root));
            Assert.Equal(databaseBefore, ReadLogicalDatabaseState(source.DatabasePath));
        }

        static string[] FingerprintPersistentSourceTree(string root)
        {
            return SnapshotTestHelper.FingerprintTree(root)
                .Where(entry =>
                    !entry.StartsWith("F:cache.db-wal:", StringComparison.Ordinal)
                    && !entry.StartsWith("F:cache.db-shm:", StringComparison.Ordinal))
                .ToArray();
        }

        static string[] ReadLogicalDatabaseState(string databasePath)
        {
            using var connection = SnapshotTestHelper.OpenConnection(
                databasePath,
                SqliteOpenMode.ReadOnly);
            AssertIntegrityCheck(connection);

            var state = new List<string>
            {
                $"schema-version:{ReadInt32(connection, "PRAGMA schema_version;")}",
                $"user-version:{ReadInt32(connection, "PRAGMA user_version;")}",
            };
            AddRows(
                connection,
                state,
                "schema",
                "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name, tbl_name;");
            AddRows(
                connection,
                state,
                "metadata",
                """
                SELECT cache_key, json_value, created_at, expires_at, job_id
                FROM cache_metadata
                ORDER BY cache_key;
                """);
            AddRows(
                connection,
                state,
                "artifacts",
                """
                SELECT cache_key, file_path, file_size, created_at, last_accessed, job_id
                FROM cache_artifacts
                ORDER BY cache_key;
                """);
            AddRows(
                connection,
                state,
                "job-state",
                """
                SELECT job_id, is_completed, finished_at, cached_at, expires_at
                FROM cache_job_state
                ORDER BY job_id;
                """);
            return state.ToArray();
        }

        static void AddRows(
            SqliteConnection connection,
            List<string> state,
            string section,
            string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index)
                        ? "null"
                        : $"{reader.GetFieldType(index).Name}:"
                          + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(
                              Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)!)));
                state.Add($"{section}:{string.Join(",", values)}");
            }
        }
    }

    [Fact]
    public async Task Export_DestinationParentAliasIntoSource_IsRejectedPhysically()
    {
        var workspace = Workspace("destination-alias");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var alias = Path.Combine(workspace, "source-alias");
        await SnapshotTestHelper.CreateDirectoryAliasAsync(alias, source.Root);
        try
        {
            await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
                source,
                Path.Combine(alias, "snapshot"));
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(alias);
        }
    }

    [Fact]
    public async Task Export_DestinationParentIsAnchoredBeforeFirstProgressCallback()
    {
        const string firstProgressMessage = "Validating source cache schema...";
        var workspace = Workspace("early-parent-replacement");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        var sourceBefore = SnapshotTestHelper.FingerprintTree(source.Root);
        var destinationParent = Path.Combine(workspace, "publish");
        var movedParent = Path.Combine(workspace, "publish-original");
        Directory.CreateDirectory(destinationParent);
        File.WriteAllText(Path.Combine(destinationParent, "original.txt"), "original");
        var originalParentBefore = SnapshotTestHelper.FingerprintTree(destinationParent);
        var destination = Path.Combine(destinationParent, "snapshot");
        var movedDestination = Path.Combine(movedParent, "snapshot");
        var callbackReached = false;
        var parentReplaced = false;
        var progress = new SynchronousProgress<string>(message =>
        {
            if (!string.Equals(message, firstProgressMessage, StringComparison.Ordinal))
                return;

            Assert.False(callbackReached);
            callbackReached = true;
            Directory.Move(destinationParent, movedParent);
            Directory.CreateDirectory(destinationParent);
            parentReplaced = true;
        });

        var exception = await Record.ExceptionAsync(
            () => SnapshotExporter.ExportAsync(source.Root, destination, progress));

        Assert.True(callbackReached);
        Assert.Equal(sourceBefore, SnapshotTestHelper.FingerprintTree(source.Root));
        Assert.False(SnapshotExporter.PathEntryExists(destination));
        Assert.False(SnapshotExporter.PathEntryExists(movedDestination));
        if (!parentReplaced)
        {
            Assert.True(
                OperatingSystem.IsWindows(),
                $"Only the Windows no-delete lease may deny the parent move: {exception}");
            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"The Windows no-delete lease did not deny the parent move: {exception}");
            Assert.False(SnapshotExporter.PathEntryExists(movedParent));
            Assert.Equal(
                originalParentBefore,
                SnapshotTestHelper.FingerprintTree(destinationParent));
            return;
        }

        Assert.True(parentReplaced);
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(
            "destination parent",
            invalidOperation.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            originalParentBefore,
            SnapshotTestHelper.FingerprintTree(movedParent));
        Assert.Empty(SnapshotTestHelper.ParentEntries(destinationParent));
    }

    [Theory]
    [InlineData("replace-database")]
    [InlineData("corrupt-database")]
    [InlineData("alter-artifact")]
    [InlineData("remove-artifact")]
    [InlineData("add-sidecar")]
    public async Task Export_FinalProgressMutation_IsRejectedWithoutPublication(string mutation)
    {
        const string finalizationMessage = "Finalizing snapshot (atomic rename)...";
        var workspace = Workspace($"final-mutation-{mutation}");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 1,
            useWal: false);
        var sourceBefore = SnapshotTestHelper.FingerprintTree(source.Root);
        var destinationParent = Path.Combine(workspace, "publish");
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, "snapshot");
        var replacementDatabase = Path.Combine(workspace, "replacement.db");
        if (mutation == "replace-database")
        {
            using var replacement = SnapshotTestHelper.OpenConnection(
                replacementDatabase,
                SqliteOpenMode.ReadWriteCreate);
            SnapshotTestHelper.ExecuteNonQuery(
                replacement,
                "PRAGMA user_version=1; CREATE TABLE replacement_marker(value TEXT NOT NULL);");
        }

        var finalizationReports = 0;
        var progress = new SynchronousProgress<string>(message =>
        {
            if (!string.Equals(message, finalizationMessage, StringComparison.Ordinal))
                return;

            Assert.Equal(1, Interlocked.Increment(ref finalizationReports));
            var tempLeaf = Assert.Single(
                SnapshotTestHelper.ParentEntries(destinationParent),
                entry => entry.StartsWith("snapshot.tmp.", StringComparison.Ordinal));
            var temporaryPath = Path.Combine(destinationParent, tempLeaf);
            var databasePath = Path.Combine(temporaryPath, "cache.db");
            var artifactPath = Path.Combine(
                temporaryPath,
                "artifacts",
                source.Artifacts[0].RelativePath);

            switch (mutation)
            {
                case "replace-database":
                    File.Move(replacementDatabase, databasePath, overwrite: true);
                    break;
                case "corrupt-database":
                    File.WriteAllBytes(databasePath, [0x00, 0x01, 0x02]);
                    break;
                case "alter-artifact":
                    var content = File.ReadAllBytes(artifactPath);
                    content[0] ^= 0xff;
                    File.WriteAllBytes(artifactPath, content);
                    break;
                case "remove-artifact":
                    File.Delete(artifactPath);
                    break;
                case "add-sidecar":
                    File.WriteAllText(databasePath + "-wal", "unexpected");
                    break;
                default:
                    throw new XunitException($"Unknown final mutation '{mutation}'.");
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync(source.Root, destination, progress));

        Assert.Equal(1, finalizationReports);
        Assert.Equal(sourceBefore, SnapshotTestHelper.FingerprintTree(source.Root));
        Assert.False(SnapshotExporter.PathEntryExists(destination));
        Assert.Empty(SnapshotTestHelper.ParentEntries(destinationParent));
    }

    [Fact]
    public async Task Export_ConcurrentDestinationCreation_IsNeverOverwritten()
    {
        const string finalizationMessage = "Finalizing snapshot (atomic rename)...";
        const string sentinelContent = "concurrent destination";
        var workspace = Workspace("concurrent-destination");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        var sourceBefore = SnapshotTestHelper.FingerprintTree(source.Root);
        var destinationParent = Path.Combine(workspace, "publish");
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, "snapshot");
        string? temporaryPath = null;
        WindowsDirectoryOplock? publicationOplock = null;
        Task? createCollision = null;
        var finalizationReports = 0;
        var progress = new SynchronousProgress<string>(message =>
        {
            if (!string.Equals(message, finalizationMessage, StringComparison.Ordinal))
                return;

            Assert.Equal(1, Interlocked.Increment(ref finalizationReports));
            var temporaryLeaf = Assert.Single(
                SnapshotTestHelper.ParentEntries(destinationParent),
                entry => entry.StartsWith("snapshot.tmp.", StringComparison.Ordinal));
            temporaryPath = Path.Combine(destinationParent, temporaryLeaf);
            if (OperatingSystem.IsWindows())
            {
                // This breaks only when publication requests delete access after its final preflight.
                var armedOplock = new WindowsDirectoryOplock(temporaryPath);
                publicationOplock = armedOplock;
                createCollision = Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            armedOplock.WaitForBreak(TimeSpan.FromSeconds(30));
                            Directory.CreateDirectory(destination);
                            File.WriteAllText(
                                Path.Combine(destination, "sentinel.txt"),
                                sentinelContent);
                        }
                        finally
                        {
                            armedOplock.Dispose();
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            else
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "sentinel.txt"), sentinelContent);
            }
        });

        InvalidOperationException exception;
        try
        {
            exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SnapshotExporter.ExportAsync(source.Root, destination, progress));
            if (createCollision is not null)
                await createCollision;
        }
        finally
        {
            publicationOplock?.Dispose();
        }

        Assert.Equal(1, finalizationReports);
        Assert.NotNull(temporaryPath);
        Assert.False(SnapshotExporter.PathEntryExists(temporaryPath));
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(createCollision);
            Assert.Equal(
                $"Destination already exists: {Path.GetFileName(destination)}. " +
                "It was not overwritten.",
                exception.Message);
        }
        else
        {
            Assert.Contains(
                "already exists",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(sourceBefore, SnapshotTestHelper.FingerprintTree(source.Root));
        Assert.True(Directory.Exists(destination));
        Assert.Equal(
            sentinelContent,
            File.ReadAllText(Path.Combine(destination, "sentinel.txt")));
        Assert.Equal(
            new[] { "sentinel.txt" },
            SnapshotTestHelper.ParentEntries(destination));
        Assert.Equal(
            new[] { "snapshot" },
            SnapshotTestHelper.ParentEntries(destinationParent));
    }

    [CaseSensitiveFileSystemFact]
    public async Task Export_CaseOnlyDestinationParentRetarget_IsRejectedAndCleansTemporarySnapshot()
    {
        var workspace = Workspace("case-parent-retarget");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        var originalParent = Path.Combine(workspace, "publish");
        var retargetedParent = Path.Combine(workspace, "PUBLISH");
        Directory.CreateDirectory(originalParent);
        SnapshotTestHelper.CreateDistinctCaseOnlySibling(
            originalParent,
            retargetedParent);

        var alias = Path.Combine(workspace, "destination-parent");
        await SnapshotTestHelper.CreateDirectoryAliasAsync(alias, originalParent);
        try
        {
            await AssertDestinationParentRetargetRejectedAsync(
                source.Root,
                alias,
                originalParent,
                retargetedParent);
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(alias);
        }
    }

    [Fact]
    public async Task Export_DistinctDestinationParentRetarget_IsRejectedOnEveryPlatform()
    {
        var workspace = Workspace("distinct-parent-retarget");
        var source = SnapshotTestHelper.CreateSource(workspace, useWal: false);
        var originalParent = Path.Combine(workspace, "publish-a");
        var retargetedParent = Path.Combine(workspace, "publish-b");
        Directory.CreateDirectory(originalParent);
        Directory.CreateDirectory(retargetedParent);

        var alias = Path.Combine(workspace, "destination-parent");
        await SnapshotTestHelper.CreateDirectoryAliasAsync(alias, originalParent);
        try
        {
            await AssertDestinationParentRetargetRejectedAsync(
                source.Root,
                alias,
                originalParent,
                retargetedParent);
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(alias);
        }
    }

    [Fact]
    public async Task Export_DestinationBelowPhysicalLinkedArtifactsRoot_IsRejected()
    {
        var workspace = Workspace("linked-artifacts");
        var source = SnapshotTestHelper.CreateSource(workspace, artifactRows: 0);
        var artifactsLink = Path.Combine(source.Root, "artifacts");
        Directory.Delete(artifactsLink);
        var physicalArtifacts = Path.Combine(workspace, "physical-artifacts");
        Directory.CreateDirectory(physicalArtifacts);
        await SnapshotTestHelper.CreateDirectoryAliasAsync(artifactsLink, physicalArtifacts);
        try
        {
            await SnapshotTestHelper.AssertRejectedWithoutPublicationAsync(
                source,
                Path.Combine(physicalArtifacts, "snapshot"));
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(artifactsLink);
        }
    }

    [Fact]
    public async Task Export_SourceRootAlias_IsCanonicalizedAndSucceeds()
    {
        var workspace = Workspace("source-alias-success");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var sourceAlias = Path.Combine(workspace, "source-alias");
        await SnapshotTestHelper.CreateDirectoryAliasAsync(sourceAlias, source.Root);
        var destination = Path.Combine(workspace, "snapshot");
        try
        {
            var aliasedSource = source with
            {
                Root = sourceAlias,
                DatabasePath = Path.Combine(sourceAlias, "cache.db"),
            };
            await SnapshotTestHelper.ExportAndAssertAtomicPublicationAsync(
                aliasedSource,
                destination);
            SnapshotTestHelper.AssertFinalLayout(destination);
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(sourceAlias);
        }
    }

    [Fact]
    public async Task Export_DanglingDestinationParentAlias_FailsClosedWithoutResidue()
    {
        var workspace = Workspace("dangling-alias");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var alias = Path.Combine(workspace, "dangling");
        await SnapshotTestHelper.CreateDirectoryAliasAsync(
            alias,
            Path.Combine(workspace, "missing-target"));
        var sourceBefore = SnapshotTestHelper.FingerprintTree(source.Root);
        var parentBefore = SnapshotTestHelper.ParentEntries(workspace);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => SnapshotExporter.ExportAsync(source.Root, Path.Combine(alias, "snapshot")));

            Assert.Equal(sourceBefore, SnapshotTestHelper.FingerprintTree(source.Root));
            Assert.Equal(parentBefore, SnapshotTestHelper.ParentEntries(workspace));
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(alias);
        }
    }

    [Fact]
    public async Task Export_CyclicDestinationParentAlias_FailsClosedWithoutResidue()
    {
        var workspace = Workspace("cyclic-alias");
        var source = SnapshotTestHelper.CreateSource(workspace);
        var first = Path.Combine(workspace, "cycle-a");
        var second = Path.Combine(workspace, "cycle-b");
        await SnapshotTestHelper.CreateDirectoryAliasAsync(first, second);
        await SnapshotTestHelper.CreateDirectoryAliasAsync(second, first);
        var sourceBefore = SnapshotTestHelper.FingerprintTree(source.Root);
        var parentBefore = SnapshotTestHelper.ParentEntries(workspace);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => SnapshotExporter.ExportAsync(source.Root, Path.Combine(first, "snapshot")));

            Assert.Equal(sourceBefore, SnapshotTestHelper.FingerprintTree(source.Root));
            Assert.Equal(parentBefore, SnapshotTestHelper.ParentEntries(workspace));
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(first);
            SnapshotTestHelper.DeleteDirectoryAlias(second);
        }
    }

    private static async Task AssertDestinationParentRetargetRejectedAsync(
        string sourceRoot,
        string destinationParentAlias,
        string originalParent,
        string retargetedParent)
    {
        var destination = Path.Combine(destinationParentAlias, "snapshot");
        var originalDestination = Path.Combine(originalParent, "snapshot");
        var retargetedDestination = Path.Combine(retargetedParent, "snapshot");
        var sourceBefore = SnapshotTestHelper.FingerprintTree(sourceRoot);
        var originalParentBefore = SnapshotTestHelper.ParentEntries(originalParent);
        var retargetedParentBefore = SnapshotTestHelper.ParentEntries(retargetedParent);
        var reachedValidation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeExport = new ManualResetEventSlim(initialState: false);
        var validationReports = 0;
        var progress = new SynchronousProgress<string>(message =>
        {
            if (!string.Equals(
                    message,
                    "Validating temporary snapshot...",
                    StringComparison.Ordinal))
            {
                return;
            }

            if (Interlocked.Increment(ref validationReports) != 1)
                return;

            reachedValidation.TrySetResult();
            if (!resumeExport.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The destination-parent retarget hook was not released.");
        });

        var exportTask = Task.Run(
            () => SnapshotExporter.ExportAsync(sourceRoot, destination, progress));
        try
        {
            var completed = await Task.WhenAny(reachedValidation.Task, exportTask)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (completed == exportTask)
            {
                var earlyFailure = await Record.ExceptionAsync(async () => await exportTask);
                throw new XunitException(
                    earlyFailure == null
                        ? "Export completed before the deterministic retarget hook was reached."
                        : "Export failed before the deterministic retarget hook was reached: " +
                          earlyFailure.Message);
            }

            SnapshotTestHelper.DeleteDirectoryAlias(destinationParentAlias);
            Assert.False(Directory.Exists(destinationParentAlias));
            Assert.False(File.Exists(destinationParentAlias));
            await SnapshotTestHelper.CreateDirectoryAliasAsync(
                destinationParentAlias,
                retargetedParent);
        }
        finally
        {
            resumeExport.Set();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await exportTask);

        Assert.Contains(
            "destination parent changed",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBefore, SnapshotTestHelper.FingerprintTree(sourceRoot));
        Assert.Equal(originalParentBefore, SnapshotTestHelper.ParentEntries(originalParent));
        Assert.Equal(retargetedParentBefore, SnapshotTestHelper.ParentEntries(retargetedParent));
        Assert.False(Directory.Exists(originalDestination));
        Assert.False(Directory.Exists(retargetedDestination));
    }

    [Fact]
    public void CheckpointReadiness_NoWalThenPositive_TransitionsOnlyOnPositiveProgress()
    {
        var state = new CheckpointReadinessStateMachine(TimeSpan.FromMinutes(1));

        Assert.False(state.Observe(new CheckpointRow(0, -1, -1)));
        Assert.False(state.IsReady);

        Assert.True(state.Observe(new CheckpointRow(0, 4, 4)));
        Assert.True(state.IsReady);

        Assert.False(state.Observe(new CheckpointRow(1, -1, -1)));
        Assert.True(state.IsReady);
    }

    [Fact]
    public void CheckpointReadiness_PersistentNoWal_TimesOutDeterministically()
    {
        var timeout = TimeSpan.FromSeconds(10);
        var elapsed = TimeSpan.Zero;
        var state = new CheckpointReadinessStateMachine(timeout, () => elapsed);

        Assert.False(state.Observe(new CheckpointRow(0, -1, -1)));
        elapsed = timeout - TimeSpan.FromTicks(1);
        Assert.False(state.Observe(new CheckpointRow(1, -1, -1)));
        elapsed = timeout;

        Assert.Throws<TimeoutException>(
            () => state.Observe(new CheckpointRow(0, -1, -1)));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0, -2, -2)]
    [InlineData(0, -2, 0)]
    [InlineData(0, 0, -2)]
    [InlineData(0, 1, 2)]
    public void CheckpointReadiness_InvalidRowsFail(
        int busy,
        int walPages,
        int checkpointedPages)
    {
        var state = new CheckpointReadinessStateMachine(TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidOperationException>(
            () => state.Observe(new CheckpointRow(busy, walPages, checkpointedPages)));
    }

    [Fact]
    public async Task Export_ActiveWalWriterAndPassiveCheckpointer_ProducesConsistentSnapshots()
    {
        var workspace = Workspace("online-backup");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            metadataRows: 3,
            artifactRows: 2);
        using var stop = new CancellationTokenSource();
        var writerInitialized = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointerInitialized = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var committedWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointProgress = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;
        var checkpointCount = 0;
        var anchor = SnapshotTestHelper.OpenConnection(source.DatabasePath);

        var writer = Task.Run(
            () => RunWriterAsync(
                anchor,
                checkpointerInitialized.Task,
                writerInitialized,
                committedWrite,
                () => Interlocked.Increment(ref writeCount),
                stop.Token));
        var checkpointer = Task.Run(
            () => RunCheckpointerAsync(
                source.DatabasePath,
                writerInitialized.Task,
                committedWrite.Task,
                checkpointerInitialized,
                checkpointProgress,
                () => Interlocked.Increment(ref checkpointCount),
                stop.Token));

        try
        {
            await Task.WhenAll(writerInitialized.Task, checkpointerInitialized.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(committedWrite.Task, checkpointProgress.Task)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(Volatile.Read(ref writeCount) >= 1);
            Assert.True(Volatile.Read(ref checkpointCount) >= 1);

            var previousHead = 0;
            for (var snapshotNumber = 0; snapshotNumber < 4; snapshotNumber++)
            {
                Assert.False(writer.IsCompleted, "Writer stopped before export.");
                Assert.False(checkpointer.IsCompleted, "Checkpointer stopped before export.");
                var destination = Path.Combine(workspace, $"snapshot-{snapshotNumber}");

                await Task.Run(
                        () => SnapshotExporter.ExportAsync(source.Root, destination))
                    .WaitAsync(TimeSpan.FromSeconds(30));

                SnapshotTestHelper.AssertFinalLayout(destination);
                using var snapshot = SnapshotTestHelper.OpenConnection(
                    Path.Combine(destination, "cache.db"),
                    SqliteOpenMode.ReadOnly);
                AssertIntegrityCheck(snapshot);

                var head = ReadInt32(
                    snapshot,
                    "SELECT json_value FROM cache_metadata WHERE cache_key='snapshot-stress:head';");
                var committedRows = ReadInt32(
                    snapshot,
                    "SELECT COUNT(*) FROM cache_metadata WHERE cache_key LIKE 'snapshot-stress:row:%';");
                Assert.Equal(head, committedRows);
                Assert.True(head >= previousHead);
                previousHead = head;

                Assert.Equal(
                    new[]
                    {
                        ("baseline:metadata:0000", """{"baseline":0}"""),
                        ("baseline:metadata:0001", """{"baseline":1}"""),
                        ("baseline:metadata:0002", """{"baseline":2}"""),
                    },
                    ReadMetadata(
                        snapshot,
                        "SELECT cache_key, json_value FROM cache_metadata "
                        + "WHERE job_id='baseline' ORDER BY cache_key;"));
                Assert.Equal(
                    1,
                    ReadInt32(
                        snapshot,
                        "SELECT COUNT(*) FROM cache_job_state WHERE job_id='baseline-job' AND is_completed=1;"));
                Assert.Equal(
                    source.Artifacts.Count,
                    ReadInt32(snapshot, "SELECT COUNT(*) FROM cache_artifacts;"));

                foreach (var artifact in source.Artifacts)
                {
                    Assert.Equal(
                        artifact.Content,
                        File.ReadAllBytes(Path.Combine(
                            destination,
                            "artifacts",
                            artifact.RelativePath)));
                }

                var validation = await SnapshotValidator.ValidateAsync(destination);
                Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
                Assert.Equal(0, validation.MissingArtifactFiles);
            }
        }
        finally
        {
            stop.Cancel();
            try
            {
                await Task.WhenAll(writer, checkpointer);
            }
            finally
            {
                anchor.Dispose();
            }
        }
    }

    private static async Task RunWriterAsync(
        SqliteConnection connection,
        Task checkpointerInitialized,
        TaskCompletionSource initialized,
        TaskCompletionSource committedWrite,
        Action committed,
        CancellationToken cancellationToken)
    {
        try
        {
            AssertWalMode(connection, setWalMode: true);
            Assert.Equal(0, ReadInt32(connection, "PRAGMA wal_autocheckpoint=0;"));
            Assert.Equal(
                new CheckpointRow(0, 0, 0),
                ReadCheckpointRow(connection, truncate: true));
            initialized.TrySetResult();
            await checkpointerInitialized.WaitAsync(cancellationToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));
            var sequence = 0;

            do
            {
                sequence++;
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO cache_metadata
                        (cache_key, json_value, created_at, expires_at, job_id)
                    VALUES (@rowKey, @sequence, @now, @expires, 'snapshot-stress');
                    INSERT INTO cache_metadata
                        (cache_key, json_value, created_at, expires_at, job_id)
                    VALUES ('snapshot-stress:head', @sequence, @now, @expires, 'snapshot-stress')
                    ON CONFLICT(cache_key) DO UPDATE SET json_value=excluded.json_value;
                    """;
                command.Parameters.AddWithValue("@rowKey", $"snapshot-stress:row:{sequence:D10}");
                command.Parameters.AddWithValue(
                    "@sequence",
                    sequence.ToString(CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("@expires", DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
                command.ExecuteNonQuery();
                transaction.Commit();
                committed();
                committedWrite.TrySetResult();
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            initialized.TrySetCanceled(cancellationToken);
            committedWrite.TrySetCanceled(cancellationToken);
            // Expected bounded shutdown.
        }
        catch (Exception exception)
        {
            initialized.TrySetException(exception);
            committedWrite.TrySetException(exception);
            throw;
        }
    }

    private static async Task RunCheckpointerAsync(
        string databasePath,
        Task writerInitialized,
        Task committedWrite,
        TaskCompletionSource initialized,
        TaskCompletionSource checkpointProgress,
        Action checkpointed,
        CancellationToken cancellationToken)
    {
        try
        {
            await writerInitialized.WaitAsync(cancellationToken);
            using var connection = SnapshotTestHelper.OpenConnection(databasePath);
            Assert.Equal(0, ReadInt32(connection, "PRAGMA wal_autocheckpoint=0;"));
            SnapshotTestHelper.ExecuteNonQuery(connection, "PRAGMA busy_timeout=2000;");
            AssertWalMode(connection, setWalMode: false);
            Assert.Equal(3, ReadInt32(connection, "SELECT COUNT(*) FROM cache_metadata;"));
            initialized.TrySetResult();

            await committedWrite.WaitAsync(cancellationToken);
            var readiness = new CheckpointReadinessStateMachine(TimeSpan.FromSeconds(10));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));

            do
            {
                if (readiness.Observe(ReadCheckpointRow(connection, truncate: false)))
                {
                    checkpointed();
                    checkpointProgress.TrySetResult();
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            initialized.TrySetCanceled(cancellationToken);
            checkpointProgress.TrySetCanceled(cancellationToken);
            // Expected bounded shutdown.
        }
        catch (Exception exception)
        {
            initialized.TrySetException(exception);
            checkpointProgress.TrySetException(exception);
            throw;
        }
    }

    private static void AssertWalMode(SqliteConnection connection, bool setWalMode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = setWalMode ? "PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode;";
        var journalMode = Convert.ToString(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture);
        Assert.True(
            string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase),
            $"Expected SQLite journal mode 'wal', found '{journalMode ?? "<null>"}'.");
    }

    private static CheckpointRow ReadCheckpointRow(
        SqliteConnection connection,
        bool truncate)
    {
        using var command = connection.CreateCommand();
        command.CommandText = truncate
            ? "PRAGMA wal_checkpoint(TRUNCATE);"
            : "PRAGMA wal_checkpoint(PASSIVE);";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var row = new CheckpointRow(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
        Assert.False(reader.Read());
        return row;
    }

    private static void AssertIntegrityCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        Assert.Equal(new[] { "ok" }, rows);
    }

    private static int ReadInt32(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static (string CacheKey, string JsonValue)[] ReadMetadata(
        SqliteConnection connection,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<(string CacheKey, string JsonValue)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows.ToArray();
    }

    private static (uint Volume, ulong FileId) GetWindowsFileIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new XunitException(
                "Unable to inspect a Windows file handle " +
                $"(error {Marshal.GetLastPInvokeError()}).");
        }

        return (
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}

public class SnapshotValidatorTests : IDisposable
{
    private readonly List<string> _workspaces = [];

    private string Workspace(string tag)
    {
        var workspace = SnapshotTestHelper.CreateWorkspace($"validator-{tag}");
        _workspaces.Add(workspace);
        return workspace;
    }

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    [Fact]
    public async Task Validate_ValidSnapshot_ReportsCountsAndNoMissingFiles()
    {
        var workspace = Workspace("valid");
        var snapshot = SnapshotTestHelper.CreateSource(
            workspace,
            metadataRows: 4,
            artifactRows: 3,
            useWal: false).Root;

        var result = await SnapshotValidator.ValidateAsync(snapshot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Errors);
        Assert.Equal(4, result.MetadataEntries);
        Assert.Equal(3, result.ArtifactEntries);
        Assert.Equal(0, result.MissingArtifactFiles);
    }

    [Fact]
    public async Task Validate_MissingSnapshotDirectory_IsInvalidWithFocusedDiagnostic()
    {
        var workspace = Workspace("missing-directory");
        var snapshot = Path.Combine(workspace, "missing-snapshot");

        var result = await SnapshotValidator.ValidateAsync(snapshot);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("directory does not exist", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(snapshot));
    }

    [Fact]
    public async Task Validate_MissingDatabase_IsInvalidWithFocusedDiagnostic()
    {
        var workspace = Workspace("missing-database");
        var snapshot = Path.Combine(workspace, "snapshot");
        Directory.CreateDirectory(snapshot);

        var result = await SnapshotValidator.ValidateAsync(snapshot);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("missing required database file", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cache.db", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_WrongSchemaVersion_IsInvalidWithFocusedDiagnostic()
    {
        var workspace = Workspace("wrong-schema");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 0,
            useWal: false);
        using (var connection = SnapshotTestHelper.OpenConnection(source.DatabasePath))
            SnapshotTestHelper.ExecuteNonQuery(connection, "PRAGMA user_version=42;");

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("schema version mismatch", StringComparison.OrdinalIgnoreCase)
                && error.Contains("expected 1", StringComparison.OrdinalIgnoreCase)
                && error.Contains("found 42", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_MissingRequiredTable_IsInvalidWithFocusedDiagnostic()
    {
        var workspace = Workspace("missing-required-table");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 0,
            useWal: false);
        using (var connection = SnapshotTestHelper.OpenConnection(source.DatabasePath))
            SnapshotTestHelper.ExecuteNonQuery(connection, "DROP TABLE cache_job_state;");

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("missing required table", StringComparison.OrdinalIgnoreCase)
                && error.Contains("cache_job_state", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_CorruptDatabase_IsInvalidWithoutThrowingAndReportsIntegrityFailure()
    {
        var workspace = Workspace("corrupt-database");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 0,
            useWal: false);
        using (var connection = SnapshotTestHelper.OpenConnection(source.DatabasePath))
        {
            SnapshotTestHelper.ExecuteNonQuery(connection, """
                PRAGMA writable_schema=ON;
                UPDATE sqlite_schema
                SET rootpage = (
                    SELECT rootpage
                    FROM sqlite_schema
                    WHERE type='table' AND name='cache_metadata'
                )
                WHERE type='table' AND name='cache_job_state';
                PRAGMA writable_schema=OFF;
                """);
        }

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("integrity", StringComparison.OrdinalIgnoreCase)
                || error.Contains("corrupt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_DatabaseAliasOutsideSnapshot_IsRejectedAtSnapshotBoundary()
    {
        var workspace = Workspace("external-database");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 0,
            useWal: false);
        var externalDirectory = Path.Combine(workspace, "external-database");
        Directory.CreateDirectory(externalDirectory);
        var externalDatabase = Path.Combine(externalDirectory, "cache.db");
        File.Move(source.DatabasePath, externalDatabase);
        await SnapshotTestHelper.CreateFileAliasAsync(source.DatabasePath, externalDatabase);

        try
        {
            var result = await SnapshotValidator.ValidateAsync(source.Root);

            AssertPhysicalSnapshotBoundaryError(result, "database");
        }
        finally
        {
            SnapshotTestHelper.DeleteFileAlias(source.DatabasePath);
        }
    }

    [Fact]
    public async Task Validate_ArtifactsAliasOutsideSnapshot_IsRejectedAtSnapshotBoundary()
    {
        var workspace = Workspace("external-artifacts");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 1,
            useWal: false);
        var artifactsPath = Path.Combine(source.Root, "artifacts");
        var externalArtifacts = Path.Combine(workspace, "external-artifacts");
        Directory.Move(artifactsPath, externalArtifacts);
        await SnapshotTestHelper.CreateDirectoryAliasAsync(artifactsPath, externalArtifacts);

        try
        {
            var result = await SnapshotValidator.ValidateAsync(source.Root);

            AssertPhysicalSnapshotBoundaryError(result, "artifacts");
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(artifactsPath);
        }
    }

    [Fact]
    public async Task Validate_ArtifactsAliasToSnapshotRoot_IsRejectedAtSnapshotBoundary()
    {
        var workspace = Workspace("root-artifacts");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 0,
            useWal: false);
        var artifactsPath = Path.Combine(source.Root, "artifacts");
        Directory.Delete(artifactsPath);
        await SnapshotTestHelper.CreateDirectoryAliasAsync(artifactsPath, source.Root);

        try
        {
            var result = await SnapshotValidator.ValidateAsync(source.Root);

            AssertPhysicalSnapshotBoundaryError(result, "artifacts");
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(artifactsPath);
        }
    }

    [CaseSensitiveFileSystemFact]
    public async Task Validate_ArtifactLinkIntoCaseOnlySibling_IsUnsafeAndNotMissing()
    {
        var workspace = Workspace("case-artifact-link");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 1,
            useWal: false);
        var artifact = Assert.Single(source.Artifacts);
        var artifactsRoot = Path.Combine(source.Root, "artifacts");
        var caseOnlySibling = Path.Combine(source.Root, "ARTIFACTS");
        SnapshotTestHelper.CreateDistinctCaseOnlySibling(
            artifactsRoot,
            caseOnlySibling);

        var externalArtifact = Path.Combine(caseOnlySibling, artifact.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(externalArtifact)!);
        File.WriteAllBytes(externalArtifact, artifact.Content);

        var referencedArtifact = Path.Combine(artifactsRoot, artifact.RelativePath);
        File.Delete(referencedArtifact);
        await SnapshotTestHelper.CreateFileAliasAsync(referencedArtifact, externalArtifact);
        try
        {
            var result = await SnapshotValidator.ValidateAsync(source.Root);

            Assert.False(result.IsValid);
            Assert.Equal(0, result.MissingArtifactFiles);
            Assert.Contains(result.Errors, IsUnsafeArtifactPathError);
            Assert.DoesNotContain(result.Errors, IsMissingError);
        }
        finally
        {
            SnapshotTestHelper.DeleteFileAlias(referencedArtifact);
        }
    }

    [CaseSensitiveFileSystemFact]
    public async Task Validate_MissingArtifactBelowCaseOnlyExternalParent_IsUnsafeAndNotMissing()
    {
        var workspace = Workspace("case-missing-parent");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            metadataRows: 0,
            artifactRows: 0,
            useWal: false);
        var artifactsRoot = Path.Combine(source.Root, "artifacts");
        var caseOnlySibling = Path.Combine(source.Root, "ARTIFACTS");
        SnapshotTestHelper.CreateDistinctCaseOnlySibling(
            artifactsRoot,
            caseOnlySibling);

        var externalParentAlias = Path.Combine(artifactsRoot, "external-parent");
        await SnapshotTestHelper.CreateDirectoryAliasAsync(
            externalParentAlias,
            caseOnlySibling);
        SnapshotTestHelper.InsertArtifactReference(
            source.DatabasePath,
            "case-only-external-parent",
            Path.Combine("external-parent", "missing.bin"),
            1);
        try
        {
            var result = await SnapshotValidator.ValidateAsync(source.Root);

            Assert.False(result.IsValid);
            Assert.Equal(1, result.ArtifactEntries);
            Assert.Equal(0, result.MissingArtifactFiles);
            Assert.Contains(result.Errors, IsUnsafeArtifactPathError);
            Assert.DoesNotContain(result.Errors, IsMissingError);
        }
        finally
        {
            SnapshotTestHelper.DeleteDirectoryAlias(externalParentAlias);
        }
    }

    [Fact]
    public async Task Validate_TraversalOnly_IsInvalidWithoutIncrementingMissingCount()
    {
        var workspace = Workspace("traversal");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            metadataRows: 0,
            artifactRows: 0,
            useWal: false);
        SnapshotTestHelper.InsertArtifactReference(
            source.DatabasePath,
            "traversal",
            Path.Combine("..", "outside.bin"),
            1);

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.MissingArtifactFiles);
        Assert.Contains(result.Errors, IsTraversalError);
        Assert.DoesNotContain(result.Errors, IsMissingError);
    }

    [Fact]
    public async Task Validate_TraversalAndOneMissingInRoot_CountsOnlyMissingFile()
    {
        var workspace = Workspace("mixed");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            metadataRows: 0,
            artifactRows: 0,
            useWal: false);
        SnapshotTestHelper.InsertArtifactReference(
            source.DatabasePath,
            "traversal",
            Path.Combine("..", "outside.bin"),
            1);
        SnapshotTestHelper.InsertArtifactReference(
            source.DatabasePath,
            "missing",
            Path.Combine("missing", "inside.bin"),
            1);

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.MissingArtifactFiles);
        Assert.Contains(result.Errors, IsTraversalError);
        Assert.Contains(result.Errors, IsMissingError);
    }

    [Fact]
    public async Task Validate_ArtifactSizeMismatch_IsInvalidButNotMissing()
    {
        var workspace = Workspace("size");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 1,
            useWal: false);
        var artifact = Assert.Single(source.Artifacts);
        SnapshotTestHelper.UpdateArtifactSize(
            source.DatabasePath,
            artifact.CacheKey,
            artifact.Content.Length + 5);

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.MissingArtifactFiles);
        Assert.Contains(
            result.Errors,
            error => error.Contains("size", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task Validate_SqliteSidecar_IsInvalid(string suffix)
    {
        var workspace = Workspace($"sidecar{suffix}");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            artifactRows: 0,
            useWal: false);
        File.WriteAllBytes(source.DatabasePath + suffix, [0x01]);

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains($"cache.db{suffix}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_MissingArtifactDirectoryWithNoRows_RemainsWarningOnly()
    {
        var workspace = Workspace("no-artifacts-directory");
        var source = SnapshotTestHelper.CreateSource(
            workspace,
            metadataRows: 0,
            artifactRows: 0,
            useWal: false);
        Directory.Delete(Path.Combine(source.Root, "artifacts"));

        var result = await SnapshotValidator.ValidateAsync(source.Root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("artifacts", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertPhysicalSnapshotBoundaryError(
        SnapshotValidationResult result,
        string pathKind)
    {
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains(pathKind, error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physical snapshot", error, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTraversalError(string error)
        => error.Contains("travers", StringComparison.OrdinalIgnoreCase)
           || error.Contains("escape", StringComparison.OrdinalIgnoreCase)
           || error.Contains("unsafe", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsafeArtifactPathError(string error)
        => IsTraversalError(error)
           || error.Contains("outside", StringComparison.OrdinalIgnoreCase)
           || error.Contains("cannot be resolved safely", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingError(string error)
        => error.Contains("missing artifact", StringComparison.OrdinalIgnoreCase);
}

[CollectionDefinition("SnapshotProcessGlobals", DisableParallelization = true)]
public sealed class SnapshotProcessGlobalsCollection;

[Collection("SnapshotProcessGlobals")]
public class SnapshotCommandOutputTests : IDisposable
{
    private readonly string _workspace = SnapshotTestHelper.CreateWorkspace("command-output");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    [Fact]
    public async Task Export_NullRuntimeHashes_StillExplainsEnvironmentReplayAndAzureCliLimitation()
    {
        var cacheRoot = Path.Combine(_workspace, "cache-home");
        var effectiveRoot = Path.Combine(cacheRoot, "public");
        SnapshotTestHelper.CreateSource(
            _workspace,
            metadataRows: 1,
            artifactRows: 0,
            sourceRoot: effectiveRoot);
        var destination = Path.Combine(_workspace, "snapshot");
        var options = new CacheOptions
        {
            CacheRoot = cacheRoot,
            AuthTokenHash = null,
            CacheRootHash = null,
        };
        var command = new SnapshotCommands(options);
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        var originalContext = SynchronizationContext.Current;
        using var captured = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetError(captured);
            Environment.ExitCode = 0;
            SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

            await command.Export(destination);

            Assert.Equal(0, Environment.ExitCode);
            var output = captured.ToString();
            Assert.Contains("AZDO_TOKEN", output, StringComparison.Ordinal);
            Assert.Contains("AZDO_TOKEN_TYPE", output, StringComparison.Ordinal);
            Assert.Contains("environment", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("replay", output, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                output.Contains("Azure CLI", StringComparison.OrdinalIgnoreCase)
                || output.Contains("AzureCliCredential", StringComparison.OrdinalIgnoreCase)
                || output.Contains("az CLI", StringComparison.OrdinalIgnoreCase),
                $"Expected Azure CLI limitation in output:{Environment.NewLine}{output}");
            Assert.DoesNotContain("checkpoint", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("side-files included", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Copying cache.db-wal", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Copying cache.db-shm", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
        }
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => callback(state);
    }
}
