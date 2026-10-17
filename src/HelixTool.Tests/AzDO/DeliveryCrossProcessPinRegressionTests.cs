using System.Reflection;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class DeliveryCrossProcessPinRegressionTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-runtime", $"cross-process-pin-{Guid.NewGuid():N}"));

    [Fact]
    public async Task RetentionTransactionAndIndependentPublication_CannotInvalidateRenewedReceipt()
    {
        var options = new CacheOptions { CacheRoot = _root, MaxSizeBytes = 4, ArtifactMaxAge = TimeSpan.FromHours(1) };
        var sweeper = new FileEvidenceDeliveryStore(options);
        var publisher = new FileEvidenceDeliveryStore(options);
        const string content = "renewed across independent SQLite connections";
        var initial = sweeper.PutText(content, "text/plain", null, null, "public");
        var root = Path.GetDirectoryName(initial.LocalPath)!;
        Expire(root, initial);

        var snapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSweep = new ManualResetEventSlim();
        SetSnapshotHook(sweeper, () =>
        {
            snapshot.TrySetResult();
            Assert.True(releaseSweep.Wait(TimeSpan.FromSeconds(15)));
        });
        Task? sweep = null;
        Task<EvidenceDescriptor>? renewal = null;
        try
        {
            sweep = Task.Run(() => Sweep(sweeper, root));
            await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));
            renewal = Task.Run(() =>
            {
                renewalStarted.TrySetResult();
                return publisher.PutText(content, "text/plain", null, null, "public");
            });
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseSweep.Set();
            await sweep.WaitAsync(TimeSpan.FromSeconds(10));
            var receipt = await renewal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(initial.EvidenceId, receipt.EvidenceId);
            Assert.True(receipt.Complete);
            Sweep(sweeper, root);
            Assert.Equal(content, sweeper.Read(receipt.EvidenceId, 0, 1000, "utf8", "public").Text);
            Assert.True(publisher.TryGetDescriptor(receipt.EvidenceId, "public")!.PinnedUntilUtc > DateTimeOffset.UtcNow.AddHours(23));
        }
        finally
        {
            releaseSweep.Set();
            if (sweep is not null)
                await sweep.WaitAsync(TimeSpan.FromSeconds(20));
            if (renewal is not null)
                await renewal.WaitAsync(TimeSpan.FromSeconds(20));
            SetSnapshotHook(sweeper, null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentReaderRenewal_IsVisibleToConcurrentRetention(bool descriptorLookup)
    {
        var options = new CacheOptions { CacheRoot = _root, MaxSizeBytes = 4, ArtifactMaxAge = TimeSpan.FromHours(1) };
        var sweeper = new FileEvidenceDeliveryStore(options);
        var reader = new FileEvidenceDeliveryStore(options);
        var receipt = sweeper.PutText("persisted renewal", "text/plain", null, null, "public");
        var root = Path.GetDirectoryName(receipt.LocalPath)!;
        Expire(root, receipt);
        if (descriptorLookup)
            Assert.NotNull(reader.TryGetDescriptor(receipt.EvidenceId, "public"));
        else
            Assert.Equal("persisted renewal", reader.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text);
        await Task.WhenAll(
            Task.Run(() => Sweep(sweeper, root)),
            Task.Run(() => Assert.Equal("persisted renewal", reader.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text)));
        Assert.Equal("persisted renewal", sweeper.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text);
    }

    private static void Expire(string root, EvidenceDescriptor receipt)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "delivery.db"), Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE entries SET pinned_until = @pin, last_access = @access WHERE id = @id";
        command.Parameters.AddWithValue("@pin", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@access", DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@id", receipt.EvidenceId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void SetSnapshotHook(FileEvidenceDeliveryStore store, Action? callback)
    {
        var field = typeof(FileEvidenceDeliveryStore).GetField("RetentionSnapshotCapturedForTesting",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(store, callback);
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
