using ConsoleAppFramework;
using HelixTool.Core.Cache;

/// <summary>CLI commands for snapshot export and validation.</summary>
public class SnapshotCommands
{
    private readonly CacheOptions _cacheOptions;

    public SnapshotCommands(CacheOptions cacheOptions)
    {
        _cacheOptions = cacheOptions;
    }

    /// <summary>
    /// Export the current cache as a portable eval snapshot.
    /// The snapshot can be used offline with the HLX_EVAL_SNAPSHOT environment variable.
    /// </summary>
    /// <param name="destination">
    /// Destination directory for the snapshot. Must not already exist.
    /// </param>
    [Command("snapshot export")]
    public async Task Export([Argument] string destination, CancellationToken ct = default)
    {
        if (_cacheOptions.EvalMode)
        {
            Console.Error.WriteLine(
                "Error: 'snapshot export' cannot run in eval mode (HLX_EVAL_SNAPSHOT is set).");
            Console.Error.WriteLine(
                "       Unset HLX_EVAL_SNAPSHOT and run against the live cache.");
            Environment.ExitCode = 1;
            return;
        }

        var sourceRoot = _cacheOptions.GetEffectiveCacheRoot();
        var destFull = Path.GetFullPath(destination);

        Console.Error.WriteLine($"Source:      {sourceRoot}");
        Console.Error.WriteLine($"Destination: {destFull}");

        // Document the auth-scoped key limitation (see SnapshotExporter XML doc).
        // We warn whenever an auth context is active because auth-scoped AzDO keys
        // won't match in eval mode unless the same context is configured there.
        if (!string.IsNullOrEmpty(_cacheOptions.AuthTokenHash) ||
            !string.IsNullOrEmpty(_cacheOptions.CacheRootHash))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Note: auth-scoped key limitation");
            Console.Error.WriteLine(
                "      Cache entries recorded under an AzDO auth context use an auth-hash prefix");
            Console.Error.WriteLine(
                "      in the cache key. In eval mode (HLX_EVAL_SNAPSHOT), those entries are only");
            Console.Error.WriteLine(
                "      accessible if the eval-mode client is configured with the same auth context.");
            Console.Error.WriteLine(
                "      Public Helix cache entries are always accessible without auth context.");
            Console.Error.WriteLine(
                "      Key normalization (stripping the auth prefix) is intentionally not performed.");
        }

        Console.Error.WriteLine();

        var progress = new Progress<string>(msg => Console.Error.WriteLine($"  {msg}"));

        try
        {
            var result = await SnapshotExporter.ExportAsync(sourceRoot, destination, progress, ct);

            Console.Error.WriteLine();
            Console.Error.WriteLine("Snapshot exported successfully.");
            Console.Error.WriteLine($"  Destination:    {result.Destination}");
            Console.Error.WriteLine($"  DB size:        {Commands.FormatBytes(result.DbSizeBytes)}");
            Console.Error.WriteLine($"  Artifacts:      {result.ArtifactCount} file(s)");

            if (result.WalBusy)
            {
                Console.Error.WriteLine(
                    $"  WAL checkpoint: {result.WalPagesWritten}/{result.WalPagesTotal} pages written " +
                    "(incomplete — active reader blocked TRUNCATE; WAL side-files included)");
            }
            else
            {
                Console.Error.WriteLine(
                    $"  WAL checkpoint: {result.WalPagesWritten}/{result.WalPagesTotal} pages written (complete)");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine($"Use with:  HLX_EVAL_SNAPSHOT={result.Destination} hlx <command>");
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Export cancelled.");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Validate a snapshot directory for use with HLX_EVAL_SNAPSHOT.
    /// Checks directory layout, database schema version, expected tables, and artifact file references.
    /// </summary>
    /// <param name="snapshotPath">Path to the snapshot directory to validate.</param>
    [Command("snapshot validate")]
    public async Task Validate([Argument] string snapshotPath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(snapshotPath);
        Console.Error.WriteLine($"Validating snapshot: {fullPath}");
        Console.Error.WriteLine();

        SnapshotValidationResult result;
        try
        {
            result = await SnapshotValidator.ValidateAsync(snapshotPath, ct);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Validation cancelled.");
            Environment.ExitCode = 1;
            return;
        }

        if (result.Warnings.Count > 0)
        {
            Console.Error.WriteLine("Warnings:");
            foreach (var w in result.Warnings)
                Console.Error.WriteLine($"  !  {w}");
            Console.Error.WriteLine();
        }

        if (result.Errors.Count > 0)
        {
            Console.Error.WriteLine("Errors:");
            foreach (var e in result.Errors)
                Console.Error.WriteLine($"  x  {e}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Snapshot is INVALID.");
            Environment.ExitCode = 1;
            return;
        }

        Console.Error.WriteLine("Snapshot is VALID.");
        Console.Error.WriteLine($"  Metadata entries: {result.MetadataEntries}");
        Console.Error.WriteLine($"  Artifact entries: {result.ArtifactEntries}");
        Console.Error.WriteLine($"  Missing files:    {result.MissingArtifactFiles}");
    }
}
