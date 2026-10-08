using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class DeliveryRetentionRegressionTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-runtime", $"delivery-retention-{Guid.NewGuid():N}"));

    [Fact]
    public void OversizedReceipt_RemainsReadableAfterPublicationAndRetention()
    {
        var store = CreateStore(cap: 1);
        var receipt = Put(store, "ab");
        Assert.True(receipt.Complete);
        AssertReadable(store, receipt, "ab");
        Sweep(store);
        AssertReadable(store, receipt, "ab");
        AssertSoftCapExceeded(store, receipt.Bytes, 1, 1);
    }

    [Fact]
    public void IssuedSiblingReceipts_RemainReadableAfterSubsequentPutsAndAgeSweep()
    {
        var store = CreateStore(cap: 3);
        var first = Put(store, "AA");
        File.SetLastAccessTimeUtc(first.LocalPath, DateTime.UtcNow.AddDays(-2));
        var second = Put(store, "BB");
        Assert.True(first.Complete);
        Assert.True(second.Complete);
        Sweep(store);
        AssertReadable(store, first, "AA");
        AssertReadable(store, second, "BB");
        Assert.True(Directory.EnumerateFiles(Path.GetDirectoryName(first.LocalPath)!, "*.bin")
            .Sum(path => new FileInfo(path).Length) > 3, "Access-first publication must exceed the soft cap instead of invalidating receipts.");
        AssertSoftCapExceeded(store, first.Bytes + second.Bytes, 2, 3);
    }

    [Fact]
    public void IdenticalContent_DeduplicatesAndRefreshesAccessWithoutInvalidatingEarlierReceipt()
    {
        var store = CreateStore(cap: 1);
        var first = Put(store, "identical");
        var oldAccess = DateTime.UtcNow.AddDays(-2);
        ExpirePin(first);
        File.SetLastAccessTimeUtc(first.LocalPath, oldAccess);
        var second = Put(store, "identical");
        Assert.Equal(first.EvidenceId, second.EvidenceId);
        Assert.Equal(first.LocalPath, second.LocalPath);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.True(File.GetLastAccessTimeUtc(first.LocalPath) > oldAccess.AddDays(1));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(first.LocalPath)!, "*.bin"));
        Sweep(store);
        AssertReadable(store, first, "identical");
        AssertReadable(store, second, "identical");
        AssertPinRenewed(second);
    }

    [Fact]
    public void UnpinnedEntries_AreEvictedOldestFirstUntilWithinSoftCap()
    {
        var store = CreateStore(cap: 12);
        var oldest = Put(store, "oldestAA");
        var middle = Put(store, "middleBB");
        var newest = Put(store, "newestCC");
        foreach (var receipt in new[] { oldest, middle, newest })
            ExpirePin(receipt);
        File.SetLastAccessTimeUtc(oldest.LocalPath, DateTime.UtcNow.AddMinutes(-3));
        File.SetLastAccessTimeUtc(middle.LocalPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastAccessTimeUtc(newest.LocalPath, DateTime.UtcNow.AddMinutes(-1));
        Sweep(store);
        Assert.False(File.Exists(oldest.LocalPath));
        Assert.False(File.Exists(Path.ChangeExtension(oldest.LocalPath, ".meta.json")));
        Assert.False(File.Exists(middle.LocalPath));
        Assert.True(File.Exists(newest.LocalPath));
        AssertReadable(store, newest, "newestCC");
    }

    [Fact]
    public void AgeRetention_EvictsExpiredUnpinnedEntriesButNeverPinnedEvidence()
    {
        var store = CreateStore(cap: 10000);
        var expired = Put(store, "expired");
        var pinned = Put(store, "still issued");
        ExpirePin(expired);
        File.SetLastAccessTimeUtc(expired.LocalPath, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(pinned.LocalPath, DateTime.UtcNow.AddDays(-3));
        Sweep(store);
        Assert.False(File.Exists(expired.LocalPath));
        Assert.False(File.Exists(Path.ChangeExtension(expired.LocalPath, ".meta.json")));
        AssertReadable(store, pinned, "still issued");
    }

    [Fact]
    public void SizeRetention_SkipsPinnedOldestAndEvictsOldestEligibleEntry()
    {
        var store = CreateStore(cap: 20);
        var pinned = Put(store, "pinnedAA");
        var oldestEligible = Put(store, "oldestBB");
        var newestEligible = Put(store, "newestCC");
        ExpirePin(oldestEligible);
        ExpirePin(newestEligible);
        File.SetLastAccessTimeUtc(pinned.LocalPath, DateTime.UtcNow.AddMinutes(-3));
        File.SetLastAccessTimeUtc(oldestEligible.LocalPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastAccessTimeUtc(newestEligible.LocalPath, DateTime.UtcNow.AddMinutes(-1));
        Sweep(store);
        Assert.True(File.Exists(pinned.LocalPath));
        Assert.False(File.Exists(oldestEligible.LocalPath));
        Assert.True(File.Exists(newestEligible.LocalPath));
        var status = store.TryGetRetentionStatus("public");
        Assert.NotNull(status);
        Assert.False(status.CapExceeded);
        Assert.Equal(16, status.TotalBytes);
        Assert.Equal(8, status.PinnedBytes);
        Assert.Equal(1, status.PinnedEntryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulReadOrDescriptorLookup_RenewsPersistedPinBeforeLaterSweep(bool descriptorLookup)
    {
        var store = CreateStore(cap: 1);
        var receipt = Put(store, "renewed");
        ExpirePin(receipt);
        if (descriptorLookup)
            Assert.NotNull(store.TryGetDescriptor(receipt.EvidenceId, "public"));
        else
            Assert.Equal("renewed", store.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text);
        AssertPinRenewed(receipt);
        File.SetLastAccessTimeUtc(receipt.LocalPath, DateTime.UtcNow.AddDays(-2));
        Sweep(store);
        AssertReadable(store, receipt, "renewed");
    }

    [Fact]
    public void LegacySidecarWithoutPin_RemainsReadableAndAcquiresProtectionOnHandout()
    {
        var store = CreateStore(cap: 10000);
        var receipt = Put(store, "legacy receipt");
        var path = Path.ChangeExtension(receipt.LocalPath, ".meta.json");
        var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        metadata.Remove("pinnedUntil");
        File.WriteAllText(path, metadata.ToJsonString());
        AssertReadable(store, receipt, "legacy receipt");
        AssertPinRenewed(receipt);
    }

    [Fact]
    public async Task ChunkedRead_RemainsLosslessAcrossConcurrentPublicationAndRetention()
    {
        var store = CreateStore(cap: 8);
        var content = string.Concat(Enumerable.Repeat("ab\U0001f680", 100));
        var receipt = Put(store, content);
        var first = store.Read(receipt.EvidenceId, 0, 6, "utf8", "public");
        Assert.NotNull(first.Next);
        using var start = new SemaphoreSlim(0);
        var publishers = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            await start.WaitAsync();
            var sibling = Put(store, $"sibling-{i}");
            AssertReadable(store, sibling, $"sibling-{i}");
            Sweep(store);
        })).ToArray();
        start.Release(publishers.Length);
        var reconstructed = new StringBuilder(first.Text);
        var offset = (long)first.ReturnedBytes;
        while (offset < receipt.Bytes)
        {
            var chunk = store.Read(receipt.EvidenceId, offset, 6, "utf8", "public");
            Assert.True(chunk.SourceComplete);
            Assert.True(chunk.ReturnedBytes > 0);
            reconstructed.Append(chunk.Text);
            offset += chunk.ReturnedBytes;
            await Task.Yield();
        }
        await Task.WhenAll(publishers);
        Assert.Equal(content, reconstructed.ToString());
        Assert.Equal(receipt.Bytes, offset);
        AssertReadable(store, receipt, content);
    }

    [Fact]
    public async Task IdenticalConcurrentPublication_AcrossStoreInstancesNeverReturnsAnUnreadableReceipt()
    {
        const string content = "same receipt from overlapping requests";
        using var gate = new SemaphoreSlim(0);
        var publishers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await gate.WaitAsync();
            var store = CreateStore(cap: 1);
            var receipt = Put(store, content);
            Assert.True(receipt.Complete);
            AssertReadable(store, receipt, content);
            return receipt;
        })).ToArray();
        gate.Release(publishers.Length);
        var receipts = await Task.WhenAll(publishers);
        Assert.Single(receipts.Select(receipt => receipt.EvidenceId).Distinct());
        var reader = CreateStore(cap: 1);
        foreach (var receipt in receipts)
            AssertReadable(reader, receipt, content);
    }

    private FileEvidenceDeliveryStore CreateStore(long cap) => new(new CacheOptions
    {
        CacheRoot = _root, MaxSizeBytes = cap, ArtifactMaxAge = TimeSpan.FromHours(1)
    });

    private static EvidenceDescriptor Put(FileEvidenceDeliveryStore store, string content) =>
        store.PutText(content, "text/plain", "fingerprint", "timeline:42", "public");

    private static void AssertReadable(FileEvidenceDeliveryStore store, EvidenceDescriptor receipt, string expected)
    {
        Assert.NotNull(store.TryGetDescriptor(receipt.EvidenceId, "public"));
        var read = store.Read(receipt.EvidenceId, 0, 10000, "utf8", "public");
        Assert.True(read.SourceComplete);
        Assert.Equal(expected, read.Text);
        Assert.Equal(receipt.Sha256, read.Sha256);
    }

    private static void ExpirePin(EvidenceDescriptor receipt)
    {
        var path = Path.ChangeExtension(receipt.LocalPath, ".meta.json");
        var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        metadata["pinnedUntil"] = DateTimeOffset.UtcNow.AddDays(-1);
        File.WriteAllText(path, metadata.ToJsonString());
    }

    private static void AssertPinRenewed(EvidenceDescriptor receipt)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(receipt.LocalPath, ".meta.json")));
        Assert.True(metadata.RootElement.GetProperty("pinnedUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(23),
            "Every successful hand-out must renew the persisted minimum 24-hour protection window.");
    }

    private static void AssertSoftCapExceeded(FileEvidenceDeliveryStore store, long expectedBytes, int expectedPins, long cap)
    {
        var status = store.TryGetRetentionStatus("public");
        Assert.NotNull(status);
        Assert.True(status.CapExceeded);
        Assert.Equal(expectedBytes, status.TotalBytes);
        Assert.Equal(expectedBytes, status.PinnedBytes);
        Assert.Equal(expectedPins, status.PinnedEntryCount);
        Assert.Equal(cap, status.CapBytes);
        Assert.True(status.LastSweepUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
    }

    private void Sweep(FileEvidenceDeliveryStore store)
    {
        var method = typeof(FileEvidenceDeliveryStore).GetMethod("EnforceRetention", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        try
        {
            method.Invoke(store, [Path.Combine(new CacheOptions { CacheRoot = _root }.GetEffectiveCacheRoot(), "delivery", "public")]);
        }
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
