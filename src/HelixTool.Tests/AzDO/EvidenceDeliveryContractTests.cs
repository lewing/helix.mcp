using System.Security.Cryptography;
using System.Text;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class EvidenceDeliveryContractTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-runtime", $"hlx-evidence-reader-{Guid.NewGuid():N}"));

    [Fact]
    public void EvidenceReference_IsPartitionScopedAndVerified_NotAnArbitraryPathReader()
    {
        var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = _root });
        var bytes = Encoding.UTF8.GetBytes("complete \"record\" \U0001f680\n");
        var descriptor = store.PutBytes(bytes, "application/json", "fingerprint", "azdo:abcdef12:org:project:timeline:42", "abcdef12");
        Assert.True(descriptor.Complete);
        Assert.Equal(bytes.LongLength, descriptor.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptor.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(descriptor.LocalPath));
        Assert.Throws<EvidenceNotFoundException>(() => store.Read(descriptor.EvidenceId, 0, 2048, "utf8", "public"));
        Assert.Throws<EvidenceNotFoundException>(() => store.Read(descriptor.LocalPath, 0, 2048, "utf8", "abcdef12"));
        Assert.Throws<EvidenceNotFoundException>(() => store.Read("../../arbitrary-file", 0, 2048, "utf8", "abcdef12"));
        Assert.Null(store.TryGetDescriptor(descriptor.EvidenceId, "public"));
        var read = store.Read(descriptor.EvidenceId, 0, 2048, "utf8", "abcdef12");
        Assert.Equal(Encoding.UTF8.GetString(bytes), read.Text);
        Assert.True(read.SourceComplete);
        Assert.True(read.RangeComplete);
    }

    [Fact]
    public void Delivery_UsesEffectiveCacheRoot_AndSeparateAuthRootsCannotReadEachOthersReceipts()
    {
        var optionsA = new CacheOptions { CacheRoot = _root, CacheRootHash = "aaaaaaaa" };
        var optionsB = optionsA with { CacheRootHash = "bbbbbbbb" };
        var storeA = new FileEvidenceDeliveryStore(optionsA);
        var storeB = new FileEvidenceDeliveryStore(optionsB);
        var receiptA = storeA.PutText("partition A", "text/plain", null, null, "public");
        var receiptB = storeB.PutText("partition B", "text/plain", null, null, "public");

        Assert.Equal(Path.Combine(optionsA.GetEffectiveCacheRoot(), "delivery", "public"),
            Path.GetDirectoryName(receiptA.LocalPath));
        Assert.Equal(Path.Combine(optionsB.GetEffectiveCacheRoot(), "delivery", "public"),
            Path.GetDirectoryName(receiptB.LocalPath));
        Assert.Null(storeB.TryGetDescriptor(receiptA.EvidenceId, "public"));
        Assert.Null(storeA.TryGetDescriptor(receiptB.EvidenceId, "public"));
        Assert.Throws<EvidenceNotFoundException>(() => storeB.Read(receiptA.EvidenceId, 0, 100, "utf8", "public"));
        Assert.Throws<EvidenceNotFoundException>(() => storeA.Read(receiptB.EvidenceId, 0, 100, "utf8", "public"));
        Assert.Equal("partition A", storeA.Read(receiptA.EvidenceId, 0, 100, "utf8", "public").Text);
        Assert.Equal("partition B", storeB.Read(receiptB.EvidenceId, 0, 100, "utf8", "public").Text);
    }

    [Fact]
    public void CacheDisabled_DeliveryStillPublishesAndReadsAccessFirst()
    {
        var options = new CacheOptions { CacheRoot = _root, MaxSizeBytes = 0 };
        var store = new FileEvidenceDeliveryStore(options);
        var receipt = store.PutText("evidence with caching disabled", "text/plain", null, null, "public");
        Assert.True(receipt.Complete);
        Assert.Equal(Path.Combine(_root, "delivery-ephemeral", "public"), Path.GetDirectoryName(receipt.LocalPath));
        var independentReader = new FileEvidenceDeliveryStore(options);
        Assert.Equal("evidence with caching disabled",
            independentReader.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text);
        Assert.False(Directory.Exists(Path.Combine(options.GetEffectiveCacheRoot(), "delivery")));
    }

    [Fact]
    public void DeliveryBytes_AreAccountedAgainstDerivedCacheSizeBudgetWithoutDoubleCounting()
    {
        var options = new CacheOptions { CacheRoot = _root, MaxSizeBytes = 40 };
        var store = new FileEvidenceDeliveryStore(options);
        var receipt = store.PutText("delivery bytes", "text/plain", null, null, "public");
        var status = store.TryGetRetentionStatus("public");
        Assert.NotNull(status);
        Assert.Equal(options.MaxSizeBytes / 4, status.CapBytes);
        Assert.Equal(receipt.Bytes, status.TotalBytes);
        Assert.Equal(receipt.Bytes, status.PinnedBytes);
        Assert.True(status.CapExceeded);
        store.PutText("delivery bytes", "text/plain", null, null, "public");
        Assert.Equal(receipt.Bytes, store.TryGetRetentionStatus("public")!.TotalBytes);
        Assert.Equal("delivery bytes", store.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text);
    }

    [Fact]
    public async Task ConcurrentFirstUse_AcrossIndependentStoresReturnsReadableReceipts()
    {
        using var start = new SemaphoreSlim(0);
        var publications = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            await start.WaitAsync();
            var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = _root });
            var content = $"concurrent initialization {i}";
            var receipt = store.PutText(content, "text/plain", null, null, "public");
            Assert.True(receipt.Complete);
            Assert.Equal(content, store.Read(receipt.EvidenceId, 0, 1000, "utf8", "public").Text);
        })).ToArray();
        start.Release(publications.Length);
        await Task.WhenAll(publications);
    }

    [Fact]
    public void Utf8Chunks_AdvanceByConsumedBytesWithoutSplittingScalars_AndReconstructExactSource()
    {
        var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = _root });
        var content = "a\U0001f680\U0001f680z\u00e9";
        var descriptor = store.PutText(content, "text/plain", "fingerprint", "timeline:42", "public");
        var first = store.Read(descriptor.EvidenceId, 0, 5, "utf8", "public");
        Assert.Equal("a\U0001f680", first.Text);
        Assert.Equal(5, first.ReturnedBytes);
        Assert.NotNull(first.Next);
        var second = store.Read(descriptor.EvidenceId, first.ReturnedBytes, 4, "utf8", "public");
        Assert.Equal("\U0001f680", second.Text);
        var last = store.Read(descriptor.EvidenceId, first.ReturnedBytes + second.ReturnedBytes, 2048, "utf8", "public");
        Assert.Equal(content, first.Text + second.Text + last.Text);
        Assert.Equal(descriptor.Bytes, first.ReturnedBytes + second.ReturnedBytes + last.ReturnedBytes);
        Assert.Null(last.Next);
        Assert.DoesNotContain('\ufffd', first.Text + second.Text + last.Text);
    }

    [Fact]
    public void Utf8RequestedRangeSmallerThanScalar_StillAdvancesWithoutReplacementOrSizeError()
    {
        var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = _root });
        var descriptor = store.PutText("\U0001f680x", "text/plain", "fingerprint", "timeline:42", "public");
        var chunk = store.Read(descriptor.EvidenceId, 0, 1, "utf8", "public");
        Assert.Equal("\U0001f680", chunk.Text);
        Assert.Equal(4, chunk.ReturnedBytes);
        Assert.NotNull(chunk.Next);
        var remaining = store.Read(descriptor.EvidenceId, chunk.ReturnedBytes, 1, "utf8", "public");
        Assert.Equal("x", remaining.Text);
    }

    [Fact]
    public void ExplicitBinaryChunks_AreBoundedAndLossless_WithIndependentSourceAndRangeCompleteness()
    {
        var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = _root });
        var bytes = Enumerable.Range(0, 3000).Select(i => (byte)(i % 256)).ToArray();
        var descriptor = store.PutBytes(bytes, "application/octet-stream", "fingerprint", "blob:42", "public");
        var first = store.Read(descriptor.EvidenceId, 0, 1024, "base64", "public");
        Assert.Equal(bytes[..1024], Convert.FromBase64String(first.Data!));
        Assert.True(first.SourceComplete);
        Assert.True(first.RangeComplete);
        Assert.NotNull(first.Next);
        var remainder = store.Read(descriptor.EvidenceId, 1024, 100000, "base64", "public");
        Assert.Equal(bytes[1024..], Convert.FromBase64String(remainder.Data!));
        Assert.Equal(1976, remainder.ReturnedBytes);
        Assert.Null(remainder.Next);
    }

    [Fact]
    public void EvalDelivery_IsOutsideImmutableSnapshot_NotASelectorSpecificSnapshotKey()
    {
        var snapshot = Path.Combine(_root, "snapshot");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "cache.db"), "immutable snapshot bytes");
        Directory.CreateDirectory(Path.Combine(snapshot, "artifacts"));
        File.WriteAllText(Path.Combine(snapshot, "artifacts", "evidence.txt"), "source evidence");
        var before = HashTree(snapshot);
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(_root, "base-cache"));
            var baseRoot = new CacheOptions().GetBaseCacheRoot();
            var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = snapshot, EvalMode = true });
            var descriptor = store.PutText("derived full record", "application/json", "fingerprint",
                "azdo:dnceng-public:public:timeline:1621192", "public");
            Assert.True(descriptor.Complete);
            var snapshotIdentity = OperatingSystem.IsWindows() ? Path.GetFullPath(snapshot).ToUpperInvariant() : Path.GetFullPath(snapshot);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotIdentity)));
            Assert.Equal(Path.Combine(baseRoot, "eval-delivery", hash, "public"), Path.GetDirectoryName(descriptor.LocalPath));
            Assert.False(Path.GetFullPath(descriptor.LocalPath).StartsWith("/tmp/", StringComparison.Ordinal));
            Assert.False(Path.GetFullPath(descriptor.LocalPath).StartsWith("/private/tmp/", StringComparison.Ordinal));
            Assert.Equal("derived full record", store.Read(descriptor.EvidenceId, 0, 1000, "utf8", "public").Text);
            Assert.NotNull(store.TryGetDescriptor(descriptor.EvidenceId, "public"));
            Assert.Equal(before, HashTree(snapshot));
            if (OperatingSystem.IsWindows())
                Directory.Delete(Path.Combine(baseRoot, "eval-delivery", hash), recursive: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", xdg);
        }
    }

    private static string[] HashTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{Path.GetRelativePath(root, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
            .Concat(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(path => $"directory:{Path.GetRelativePath(root, path)}"))
            .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
