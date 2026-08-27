using ConsoleAppFramework;
using HelixTool.Core.Cache;

/// <summary>CLI commands for snapshot export and integrity validation.</summary>
public class SnapshotCommands
{
    private readonly CacheOptions _cacheOptions;

    public SnapshotCommands(CacheOptions cacheOptions)
    {
        _cacheOptions = cacheOptions;
    }

    /// <summary>
    /// Export the current cache as an offline eval snapshot.
    /// The snapshot preserves cache keys and can be used with the HLX_EVAL_SNAPSHOT environment variable.
    /// </summary>
    /// <param name="destination">
    /// Destination directory for the snapshot. Must not already exist or resolve within the source
    /// cache. Its parent must be a trusted namespace: no other same-principal process may rename,
    /// replace, or mutate entries in that parent during export.
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

        Console.Error.WriteLine();
        Console.Error.WriteLine("Note: auth-scoped replay limitation");
        Console.Error.WriteLine("      Auth-scoped AzDO keys are preserved unchanged.");
        Console.Error.WriteLine(
            "      Eval mode has an environment-only token accessor, so environment-keyed entries");
        Console.Error.WriteLine(
            "      can be replayed with the identical AZDO_TOKEN and effective PAT/Bearer classification.");
        Console.Error.WriteLine(
            "      Set AZDO_TOKEN_TYPE to the same value to preserve that classification reliably.");
        Console.Error.WriteLine(
            "      AzureCliCredential- or az CLI-derived identity partitions are not currently");
        Console.Error.WriteLine(
            "      reproducible in eval mode. Anonymous/public entries work without credentials.");

        Console.Error.WriteLine();

        var progress = new ConsoleProgress();

        try
        {
            var result = await SnapshotExporter.ExportAsync(sourceRoot, destination, progress, ct);

            Console.Error.WriteLine();
            Console.Error.WriteLine("Snapshot exported successfully.");
            Console.Error.WriteLine($"  Destination:    {result.Destination}");
            Console.Error.WriteLine($"  DB size:        {Commands.FormatBytes(result.DbSizeBytes)}");
            Console.Error.WriteLine($"  Artifacts:      {result.ArtifactCount} file(s)");

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
    /// Checks layout, database integrity and schema, SQLite sidecar absence, and artifact references and sizes.
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

    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.Error.WriteLine($"  {value}");
    }
}
