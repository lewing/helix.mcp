using System.Security.Cryptography;
using System.Text;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class EvidenceDeliveryContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-evidence-reader-{Guid.NewGuid():N}");

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
        var store = new FileEvidenceDeliveryStore(new CacheOptions { CacheRoot = snapshot, EvalMode = true });
        var descriptor = store.PutText("derived full record", "application/json", "fingerprint",
            "azdo:dnceng-public:public:timeline:1621192", "public");
        Assert.True(descriptor.Complete);
        var relative = Path.GetRelativePath(snapshot, Path.GetFullPath(descriptor.LocalPath));
        Assert.StartsWith("..", relative);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshot));
        File.Delete(descriptor.LocalPath);
        var metadata = Path.ChangeExtension(descriptor.LocalPath, ".meta.json");
        if (File.Exists(metadata))
            File.Delete(metadata);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
