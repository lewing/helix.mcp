using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class DeliveryCrossProcessPinRegressionTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-runtime", $"cross-process-pin-{Guid.NewGuid():N}"));

    [Fact]
    public async Task ExpiredSnapshotAndIndependentProcessRenewal_CannotInvalidateRenewedReceipt()
    {
        var options = new CacheOptions { CacheRoot = _root, MaxSizeBytes = 1, ArtifactMaxAge = TimeSpan.FromHours(1) };
        var sweeper = new FileEvidenceDeliveryStore(options);
        var publisher = new FileEvidenceDeliveryStore(options);
        const string content = "renewed across independent process locks";
        var initial = sweeper.PutText(content, "text/plain", null, null, "public");
        Assert.NotNull(publisher.TryGetRetentionStatus("public"));
        var root = Path.GetDirectoryName(initial.LocalPath)!;
        var metaPath = Path.ChangeExtension(initial.LocalPath, ".meta.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        metadata["pinnedUntil"] = DateTimeOffset.UtcNow.AddDays(-1);
        File.WriteAllText(metaPath, metadata.ToJsonString());
        File.SetLastAccessTimeUtc(initial.LocalPath, DateTime.UtcNow.AddDays(-2));

        var locksField = typeof(FileEvidenceDeliveryStore).GetField("PartitionLocks", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(locksField);
        var locks = Assert.IsType<ConcurrentDictionary<string, object>>(locksField.GetValue(null));
        var originalLock = locks[root];
        var snapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSweep = new ManualResetEventSlim();
        SetSnapshotHook(sweeper, () =>
        {
            snapshot.TrySetResult();
            Assert.True(releaseSweep.Wait(TimeSpan.FromSeconds(15)), "Test coordinator failed to release the captured sweep.");
        });
        Task? sweep = null;
        Task<EvidenceDescriptor>? renewal = null;
        try
        {
            sweep = Task.Run(() => Sweep(sweeper, root));
            await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Different processes do not share PartitionLocks. Replace only this test's unique
            // root entry while the first sweep still holds its original monitor.
            locks[root] = new object();
            AssertCrossProcessLeaseHeld(root);
            renewal = Task.Run(() =>
            {
                renewalStarted.TrySetResult();
                return publisher.PutText(content, "text/plain", null, null, "public");
            });
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseSweep.Set();
            await sweep.WaitAsync(TimeSpan.FromSeconds(10));
            var receipt = await renewal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(receipt.Complete);
            Assert.Equal(initial.EvidenceId, receipt.EvidenceId);
            var read = publisher.Read(receipt.EvidenceId, 0, 1000, "utf8", "public");
            Assert.True(read.SourceComplete);
            Assert.Equal(content, read.Text);
            Assert.True(File.Exists(receipt.LocalPath));
            Assert.NotNull(publisher.TryGetDescriptor(receipt.EvidenceId, "public"));
        }
        finally
        {
            releaseSweep.Set();
            if (sweep is not null)
                await sweep.WaitAsync(TimeSpan.FromSeconds(20));
            if (renewal is not null)
                await renewal.WaitAsync(TimeSpan.FromSeconds(20));
            locks[root] = originalLock;
            SetSnapshotHook(sweeper, null);
        }
    }

    private static void AssertCrossProcessLeaseHeld(string root)
    {
        var field = typeof(FileEvidenceDeliveryStore).GetField("CrossProcessLockFileName",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        var path = Path.Combine(root, Assert.IsType<string>(field.GetRawConstantValue()));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() =>
            {
                using var competing = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            return;
        }
        var fd = Open(path, 2);
        Assert.True(fd >= 0, $"Unable to open the owned partition lock, errno={Marshal.GetLastPInvokeError()}.");
        try
        {
            var result = Flock(fd, 2 | 4);
            var error = Marshal.GetLastPInvokeError();
            if (result == 0)
                Flock(fd, 8);
            Assert.Equal(-1, result);
            Assert.Contains(error, new[] { 11, 35 });
        }
        finally { Close(fd); }
    }

    private static void SetSnapshotHook(FileEvidenceDeliveryStore store, Action? callback)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var field = typeof(FileEvidenceDeliveryStore).GetField("RetentionSnapshotCapturedForTesting", flags);
        if (field is not null)
        {
            field.SetValue(store, callback);
            return;
        }
        var property = typeof(FileEvidenceDeliveryStore).GetProperty("RetentionSnapshotCapturedForTesting", flags);
        Assert.NotNull(property);
        property.SetValue(store, callback);
    }

    private static void Sweep(FileEvidenceDeliveryStore store, string root)
    {
        var method = typeof(FileEvidenceDeliveryStore).GetMethod("EnforceRetention", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        try { method.Invoke(store, [root]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
