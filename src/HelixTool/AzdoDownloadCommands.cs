using System.Text.Json;
using ConsoleAppFramework;
using HelixTool.Core;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using HelixTool.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

public sealed class AzdoDownloadCommands(IServiceProvider services)
{
    /// <summary>Read bounded text or explicitly selected base64 ranges from an hlx-owned evidence reference.</summary>
    /// <param name="evidenceId">Opaque evidence ID from a delivery descriptor, never an arbitrary path.</param>
    /// <param name="offsetBytes">Starting local byte offset; independent of provider HTTP Range support.</param>
    /// <param name="lengthBytes">Positive requested byte count. Large requests advance through bounded next actions.</param>
    /// <param name="output">Copy the complete evidence payload to this path. Without output, byte selectors return bounded local ranges.</param>
    /// <param name="resume">Reuse a verified identical output; never append unverified provider bytes.</param>
    [McpEquivalent("hlx_read_evidence")]
    [Command("evidence read")]
    public async Task ReadEvidence([Argument] string evidenceId, long offsetBytes = 0, long lengthBytes = 2048,
        string encoding = "auto", long maxResponseBytes = 12288, string? output = null,
        bool resume = true, bool overwrite = false, string? cacheDir = null, bool json = false, bool schema = false, CancellationToken ct = default)
    {
        if (Commands.TryPrintSchema<EvidenceReadResult>(schema)) return;
        var cacheOptions = services.GetRequiredService<CacheOptions>();
        if (cacheDir is not null)
        {
            if (cacheOptions.EvalMode) throw new ArgumentException("--cache-dir cannot replace the active read-only snapshot.");
            cacheOptions.CacheRoot = Path.GetFullPath(cacheDir);
        }
        var store = services.GetRequiredService<IEvidenceDeliveryStore>();
        var reader = new HlxEvidenceMcpTools(store, cacheOptions, services.GetRequiredService<IAzdoTokenAccessor>());
        var read = await reader.ReadEvidence(evidenceId, offsetBytes, lengthBytes, encoding, maxResponseBytes);
        if (output is not null)
        {
            var path = Path.GetFullPath(output);
            AzdoDownloadPaths.ValidateOutputOutsideSnapshot(path, cacheOptions);
            var parent = Path.GetDirectoryName(path)!;
            // Reuse the output-directory containment/reparse checks, retaining the caller's filename.
            _ = AzdoDownloadPaths.OutputPath(parent, "evidence", Path.GetFileName(path), read.Sha256);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Output cannot be a symlink/reparse point.");
            if (File.Exists(path) && !overwrite)
            {
                await using var prior = File.OpenRead(path);
                if (!resume || Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(prior, ct)).ToLowerInvariant() != read.Sha256)
                    throw new IOException("Output exists with different content; use --overwrite.");
            }
            else
            {
                var staging = path + $".{Guid.NewGuid():N}.partial";
                try
                {
                    FileEvidenceDeliveryStore.CheckHeadroom(staging, read.TotalBytes);
                    await using (var source = File.OpenRead(read.Delivery.LocalPath))
                    await using (var target = AzdoDownloadPaths.CreatePrivateOutput(staging))
                        await source.CopyToAsync(target, 65536, ct);
                    File.Move(staging, path, overwrite);
                }
                finally { File.Delete(staging); }
            }
            Environment.ExitCode = 0;
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(read, AzdoDownloadService.JsonOptions));
        else Console.WriteLine(read.Text ?? read.Data ?? read.Delivery.LocalPath);
    }
    /// <summary>Stream selected AzDO artifact archives/members to cache and server-local files. Binlogs are download-only.</summary>
    /// <param name="buildId">AzDO build ID or full build URL.</param>
    /// <param name="artifactName">Exact artifact name, exclusive with --pattern.</param>
    /// <param name="pattern">Explicit artifact-name glob, without implicit failed-leg/type filters.</param>
    /// <param name="memberPath">Exact relative member path. Native transfer avoids downloading the ZIP.</param>
    /// <param name="memberPattern">Relative member glob. **/Build.binlog also matches root. No member selection means original archive.</param>
    /// <param name="outputDir">Destination for binary copies. Defaults to private cache-backed delivery.</param>
    /// <param name="allowLarge">Waive logical planning bounds; never bypass real disk/auth/integrity checks.</param>
    /// <param name="resume">Reuse verified completed files. Upstream Range is not promised; the current file may restart.</param>
    [McpEquivalent("azdo_download_artifacts")]
    [Command("azdo download-artifacts")]
    public Task Artifacts([Argument] string buildId, string? artifactName = null, string? pattern = null,
        string? memberPath = null, string? memberPattern = null, int memberOffset = 0, int memberLimit = 10,
        bool allMembers = false, string contentKind = "auto", string? outputDir = null, bool overwrite = false,
        int offset = 0, int limit = 10, bool all = false, string? viewId = null, bool resume = true, string delivery = "auto",
        bool allowLarge = false, long? maxFileBytes = null, long? maxArchiveBytes = null, long? maxTotalBytes = null,
        long? maxTotalStreamBytes = null, long maxResponseBytes = 12288, string? cacheDir = null, string? output = null,
        bool json = false, bool schema = false, CancellationToken ct = default)
        => RunAsync(buildId, null, null, "dnceng-public", "public",
            AzdoDownloadMcpTools.Options(artifactName, null, pattern, memberPath, memberPattern, memberOffset, memberLimit,
                contentKind, offset, limit, viewId, maxFileBytes, maxArchiveBytes, maxTotalBytes, maxTotalStreamBytes,
                allowLarge, all, allMembers, delivery, resume, maxResponseBytes) with
            { OutputDir = outputDir, Overwrite = overwrite },
            cacheDir, output, json, schema, ct);

    /// <summary>Stream run/result test attachments or selected ZIP members. Dumps remain original binaries unless members are explicitly selected.</summary>
    /// <param name="runId">Positive AzDO test run ID.</param>
    /// <param name="resultId">Omit for run-level attachments.</param>
    /// <param name="attachmentId">Exact attachment ID, exclusive with --pattern.</param>
    [McpEquivalent("azdo_download_test_attachments")]
    [Command("azdo download-test-attachments")]
    public Task Attachments([Argument] int runId, int? resultId = null, int? attachmentId = null, string? pattern = null,
        string? memberPath = null, string? memberPattern = null, int memberOffset = 0, int memberLimit = 10,
        bool allMembers = false, string contentKind = "auto", string org = "dnceng-public", string project = "public",
        string? outputDir = null, bool overwrite = false, int offset = 0, int limit = 10, bool all = false,
        string? viewId = null, bool resume = true, string delivery = "auto", bool allowLarge = false,
        long? maxFileBytes = null, long? maxArchiveBytes = null, long? maxTotalBytes = null,
        long? maxTotalStreamBytes = null, long maxResponseBytes = 12288, string? cacheDir = null, string? output = null,
        bool json = false, bool schema = false, CancellationToken ct = default)
        => RunAsync(null, runId, resultId, org, project,
            AzdoDownloadMcpTools.Options(null, attachmentId, pattern, memberPath, memberPattern, memberOffset, memberLimit,
                contentKind, offset, limit, viewId, maxFileBytes, maxArchiveBytes, maxTotalBytes, maxTotalStreamBytes,
                allowLarge, all, allMembers, delivery, resume, maxResponseBytes) with
            { OutputDir = outputDir, Overwrite = overwrite },
            cacheDir, output, json, schema, ct);

    private async Task RunAsync(string? buildId, int? runId, int? resultId, string org, string project,
        AzdoDownloadOptions options, string? cacheDir, string? output, bool json, bool schema, CancellationToken ct)
    {
        if (Commands.TryPrintSchema<AzdoDownloadReport>(schema)) return;
        var cacheOptions = services.GetRequiredService<CacheOptions>();
        if (cacheDir is not null)
        {
            if (cacheOptions.EvalMode) throw new ArgumentException("--cache-dir cannot replace the active read-only snapshot.");
            cacheOptions.CacheRoot = Path.GetFullPath(cacheDir);
        }
        var service = services.GetRequiredService<AzdoDownloadService>();
        var report = buildId is not null
            ? await service.DownloadArtifactsAsync(buildId, options, Console.Error.WriteLine, ct)
            : await service.DownloadTestAttachmentsAsync(runId!.Value, resultId, org, project, options, Console.Error.WriteLine, ct);
        var complete = report.Delivery is not null && report.Ok &&
            report.IncompleteDetails.Count == 0;
        if (output is not null)
        {
            var path = Path.GetFullPath(output);
            AzdoDownloadPaths.ValidateOutputOutsideSnapshot(path, cacheOptions);
            _ = AzdoDownloadPaths.OutputPath(Path.GetDirectoryName(path)!, "report", Path.GetFileName(path), report.Delivery!.Sha256);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Report output cannot be a symlink/reparse point.");
            if (File.Exists(path) && !options.Overwrite)
            {
                await using var existing = File.OpenRead(path);
                if (Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(existing, ct)).ToLowerInvariant() != report.Delivery.Sha256)
                    throw new IOException("Report output exists with different content; use --overwrite.");
            }
            else
            {
                FileEvidenceDeliveryStore.CheckHeadroom(path, report.Delivery.Bytes);
                var stage = path + $".{Guid.NewGuid():N}.partial";
                try
                {
                    await using (var source = File.OpenRead(report.Delivery.LocalPath))
                    await using (var destination = AzdoDownloadPaths.CreatePrivateOutput(stage))
                        await source.CopyToAsync(destination, 65536, ct);
                    File.Move(stage, path, options.Overwrite);
                }
                finally { File.Delete(stage); }
            }
        }
        report = AzdoDownloadMcpTools.Shape(report, output is null ? options : options with { Delivery = "file" });
        if (json) Console.WriteLine(JsonSerializer.Serialize(report, AzdoDownloadService.JsonOptions));
        else
        {
            Console.WriteLine($"Downloaded {report.Bytes} bytes; source transfer {report.StreamedBytes} bytes.");
            Console.WriteLine($"Report: {output ?? report.Delivery?.LocalPath}");
            Console.WriteLine(report.Note);
        }
        // Explicit file delivery represents the entire verified report; a partial inline page remains exit 2.
        Environment.ExitCode = !report.Ok ? 1 : (complete && (output is not null || options.Delivery is "file" or "chunked" || options.OutputDir is not null) || report.Complete) ? 0 : 2;
    }
}
