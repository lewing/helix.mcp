namespace HelixTool.Core.AzDO;

internal static class AzdoByteDescriptorValidator
{
    public static bool IsByteKey(string key)
        => AzdoCacheKeys.TryParse(key, out var parsed) && parsed.ResourceKind is
            "artifactbytes" or "artifactmemberbytes" or "testattachmentbytes" or "testattachmentmemberbytes";

    public static void Validate(string key, AzdoByteDescriptor descriptor)
    {
        if (!AzdoCacheKeys.TryParse(key, out var parsed) || !IsByteKey(key) ||
            descriptor.SchemaVersion != 1 || descriptor.Provider != "azdo" || descriptor.Bytes < 0 ||
            string.IsNullOrWhiteSpace(descriptor.ContentType) || descriptor.Sha256 is null ||
            descriptor.Sha256.Length != 64 || !descriptor.Sha256.All(Uri.IsHexDigit) ||
            descriptor.Source is null || !descriptor.Source.SourceComplete || descriptor.Source.SourceBytes < 0)
            throw new InvalidDataException("Invalid byte descriptor identity, hash, or completeness.");
        var suffix = parsed.SuffixSegments;
        var artifact = parsed.ResourceKind.StartsWith("artifact", StringComparison.Ordinal);
        var resource = artifact ? $"build:{suffix[1]}:artifact:{suffix[2]}"
            : $"run:{suffix[1]}:{suffix[2]}:{suffix[3]}:attachment:{suffix[4]}";
        if (descriptor.Resource != resource) throw new InvalidDataException("Byte descriptor does not match the requested resource.");
        if (parsed.ResourceKind.Contains("member", StringComparison.Ordinal))
        {
            if (descriptor.MemberPath is null || descriptor.Format != "member")
                throw new InvalidDataException("Member descriptor lacks its canonical relative path.");
            string canonical;
            try { canonical = AzdoDownloadPaths.NormalizeMember(descriptor.MemberPath); }
            catch (ArgumentException ex) { throw new InvalidDataException("Unsafe member descriptor path.", ex); }
            if (AzdoDownloadPaths.Hash(canonical) != suffix[artifact ? 3 : 5])
                throw new InvalidDataException("Member descriptor path hash does not match its key.");
        }
        else if (descriptor.MemberPath is not null)
            throw new InvalidDataException("Whole-byte descriptor contains an unexpected member selector.");
    }
}
