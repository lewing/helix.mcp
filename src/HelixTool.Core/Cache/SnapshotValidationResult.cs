namespace HelixTool.Core.Cache;

/// <summary>Result of validating a snapshot directory for use with <c>HLX_EVAL_SNAPSHOT</c>.</summary>
/// <param name="IsValid">True when no errors were found.</param>
/// <param name="Errors">Fatal validation errors that prevent use of this snapshot.</param>
/// <param name="Warnings">Non-fatal issues that may affect snapshot usability.</param>
/// <param name="MetadataEntries">Number of rows in <c>cache_metadata</c>.</param>
/// <param name="ArtifactEntries">Number of rows in <c>cache_artifacts</c>.</param>
/// <param name="MissingArtifactFiles">
/// Number of artifact rows whose referenced files are absent from the snapshot.
/// A non-zero value is always reflected as one or more entries in <see cref="Errors"/>.
/// </param>
public sealed record SnapshotValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int MetadataEntries,
    int ArtifactEntries,
    int MissingArtifactFiles);
