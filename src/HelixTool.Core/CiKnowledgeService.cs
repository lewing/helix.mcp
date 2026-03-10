namespace HelixTool.Core;

/// <summary>
/// Per-repo CI investigation profile with patterns, tips, and tool guidance.
/// </summary>
public sealed record CiRepoProfile
{
    public required string RepoName { get; init; }
    public required string DisplayName { get; init; }
    public required bool UsesHelix { get; init; }
    public required bool UploadsTestResultsToHelix { get; init; }
    public required string TestResultLocation { get; init; }
    public required string[] FailureSearchPatterns { get; init; }
    public required string[] HelixTaskNames { get; init; }
    public required string[] CommonFailureCategories { get; init; }
    public required string[] InvestigationTips { get; init; }
}

/// <summary>
/// Provides domain knowledge about CI infrastructure for .NET repositories.
/// Used by MCP tools, resources, and error messages to guide investigation.
/// </summary>
public sealed class CiKnowledgeService
{
    private static readonly Dictionary<string, CiRepoProfile> s_profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["runtime"] = new CiRepoProfile
        {
            RepoName = "runtime",
            DisplayName = "dotnet/runtime",
            UsesHelix = true,
            UploadsTestResultsToHelix = false,
            TestResultLocation = "xUnit XML uploaded as *.testResults.xml.txt for CoreCLR; libraries tests publish to AzDO via Arcade test reporter",
            FailureSearchPatterns =
            [
                "[FAIL]",          // xUnit runner failure marker
                "  Failed",        // dotnet test summary (2 leading spaces)
                "Error Message:",  // xUnit error detail block
                "exit code",       // process crashes
                "SIGABRT",         // native crashes
            ],
            HelixTaskNames = ["Send to Helix"],
            CommonFailureCategories =
            [
                "Test assertion failures (xUnit [FAIL] markers)",
                "Infrastructure failures (Docker image pull, timeout, exit code -4)",
                "Native crashes (SIGABRT, core dumps)",
                "Timeout failures (test host killed after hang timeout)",
            ],
            InvestigationTips =
            [
                "helix_test_results works for CoreCLR tests (xUnit XML) but NOT for libraries tests",
                "Use helix_search_log with '[FAIL]' to find xUnit test failures",
                "Use helix_search_log with 'Error Message:' to get error details",
                "azdo_test_runs + azdo_test_results is the most reliable way to get structured test results",
                "CoreCLR work items upload *.testResults.xml.txt files — helix_test_results can parse these",
            ],
        },

        ["aspnetcore"] = new CiRepoProfile
        {
            RepoName = "aspnetcore",
            DisplayName = "dotnet/aspnetcore",
            UsesHelix = true,
            UploadsTestResultsToHelix = false,
            TestResultLocation = "AzDO test reporting only — TRX files are consumed locally by the Arcade test reporter and published to AzDO, never uploaded to Helix blob storage",
            FailureSearchPatterns =
            [
                "  Failed",        // dotnet test summary line (2 leading spaces)
                "Error Message:",  // test error detail
                "Failed!",         // test run summary
                "exit code",       // process crash
            ],
            HelixTaskNames = ["Send to Helix"],
            CommonFailureCategories =
            [
                "Test assertion failures (dotnet test '  Failed' markers)",
                "Timeout failures (test hangs past blame timeout)",
                "Process crashes (testhost exit code non-zero)",
                "Connection/port conflicts in server tests",
            ],
            InvestigationTips =
            [
                "helix_test_results will ALWAYS fail — aspnetcore never uploads TRX to Helix",
                "Use helix_search_log with '  Failed' (2 leading spaces) to find failed test names",
                "Use azdo_test_runs + azdo_test_results for structured test results — this is the primary path",
                "Warning: azdo_test_runs summary counts can be inaccurate — always drill into azdo_test_results",
            ],
        },

        ["sdk"] = new CiRepoProfile
        {
            RepoName = "sdk",
            DisplayName = "dotnet/sdk",
            UsesHelix = true,
            UploadsTestResultsToHelix = false,
            TestResultLocation = "AzDO test reporting — tests often crash before TRX generation",
            FailureSearchPatterns =
            [
                "Failed",          // generic failure marker
                "Error",           // build/test errors
                "exit code",       // process crash (common: exit 130)
                "error MSB",       // MSBuild errors
            ],
            HelixTaskNames = ["🟣 Run TestBuild Tests"],
            CommonFailureCategories =
            [
                "Build-as-test failures (SDK compilation errors surface as test failures)",
                "Architecture mismatch errors",
                "Process crashes (exit code 130 — SIGINT during test)",
                "MSBuild errors in test scenarios",
            ],
            InvestigationTips =
            [
                "helix_test_results will ALWAYS fail — sdk never uploads TRX to Helix",
                "SDK uses build-as-test pattern — many 'test failures' are actually build errors",
                "Helix task name is '🟣 Run TestBuild Tests' (with emoji) — use this in azdo_timeline searches",
                "Use azdo_test_runs + azdo_test_results for structured results",
                "Use helix_search_log with 'error MSB' for MSBuild failures",
            ],
        },

        ["roslyn"] = new CiRepoProfile
        {
            RepoName = "roslyn",
            DisplayName = "dotnet/roslyn",
            UsesHelix = true,
            UploadsTestResultsToHelix = false,
            TestResultLocation = "AzDO test reporting — Helix tests are dominated by crashes",
            FailureSearchPatterns =
            [
                "aborted",         // process abort
                "Process exited",  // testhost crash
                "exit code",       // non-zero exit
                "Stack overflow",  // common roslyn crash
                "OutOfMemory",     // OOM in compilation
            ],
            HelixTaskNames = [],
            CommonFailureCategories =
            [
                "Stack overflow in compiler (recursive AST processing)",
                "OutOfMemoryException (large solution analysis)",
                "Testhost crashes (Process exited with code X)",
                "Timeout failures",
            ],
            InvestigationTips =
            [
                "helix_test_results will ALWAYS fail — roslyn never uploads TRX to Helix",
                "Roslyn Helix failures are usually crashes, not assertion failures",
                "Helix tasks are hidden inside 'Run Unit Tests' or 'Test' tasks — no dedicated 'Send to Helix' step",
                "Use helix_search_log with 'aborted' or 'Process exited' to find crashes",
                "Use azdo_test_runs + azdo_test_results for structured results",
            ],
        },

        ["efcore"] = new CiRepoProfile
        {
            RepoName = "efcore",
            DisplayName = "dotnet/efcore",
            UsesHelix = true,
            UploadsTestResultsToHelix = false,
            TestResultLocation = "AzDO test reporting — Arcade test reporter publishes to AzDO",
            FailureSearchPatterns =
            [
                "[FAIL]",          // xUnit runner
                "Failed:",         // summary line
                "Error Message:",  // error detail
                "exit code",       // process crash (common: exit -3 on macOS)
            ],
            HelixTaskNames = ["Send job to helix"],
            CommonFailureCategories =
            [
                "macOS infrastructure crashes (exit code -3)",
                "SQL Server container startup failures",
                "Test assertion failures",
                "Connection timeout to test databases",
            ],
            InvestigationTips =
            [
                "helix_test_results will ALWAYS fail — efcore never uploads TRX to Helix",
                "Helix task name is 'Send job to helix' (lowercase) — different from other repos",
                "macOS tests frequently fail with exit -3 (infrastructure issue, not code bug)",
                "Use azdo_test_runs + azdo_test_results for structured results",
                "Use helix_search_log with '[FAIL]' for xUnit failures",
            ],
        },

        ["vmr"] = new CiRepoProfile
        {
            RepoName = "vmr",
            DisplayName = "dotnet/dotnet (VMR)",
            UsesHelix = false,
            UploadsTestResultsToHelix = false,
            TestResultLocation = "No Helix — pure MSBuild builds. Errors in AzDO build logs only.",
            FailureSearchPatterns =
            [
                "error MSB3073",   // MSBuild command execution failure
                "error MSB",       // general MSBuild errors
                "error NU",        // NuGet errors
                "FAILED",          // build target failure
            ],
            HelixTaskNames = [],
            CommonFailureCategories =
            [
                "Component build failures (error MSB3073)",
                "NuGet restore failures",
                "Source-build compatibility issues",
                "Cross-repo dependency version mismatches",
            ],
            InvestigationTips =
            [
                "VMR does NOT use Helix — all helix_* tools will fail or return irrelevant data",
                "Use azdo_timeline to find failed build steps, then azdo_log to read the log",
                "Use azdo_search_log with 'error MSB' for MSBuild failures",
                "Codeflow PRs (title 'Source code updates from dotnet/dotnet') are automated — check the source repo for the actual failure",
            ],
        },
    };

    /// <summary>All known repository names.</summary>
    public static IReadOnlyCollection<string> KnownRepos => s_profiles.Keys;

    /// <summary>Get a profile by repo name (case-insensitive). Returns null if unknown.</summary>
    public static CiRepoProfile? GetProfile(string repoName)
    {
        // Try direct match first
        if (s_profiles.TryGetValue(repoName, out var profile))
            return profile;

        // Try matching by full repo path (e.g., "dotnet/runtime" → "runtime")
        var shortName = repoName.Contains('/') ? repoName.Split('/').Last() : null;
        if (shortName != null && s_profiles.TryGetValue(shortName, out profile))
            return profile;

        // Special case: "dotnet" repo is the VMR
        if (repoName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            repoName.Equals("dotnet/dotnet", StringComparison.OrdinalIgnoreCase))
            return s_profiles["vmr"];

        return null;
    }

    /// <summary>Get formatted investigation guide for a repo.</summary>
    public static string GetGuide(string repoName)
    {
        var profile = GetProfile(repoName);
        if (profile is null)
            return GetGeneralGuide(repoName);

        return FormatProfile(profile);
    }

    /// <summary>Get a concise guide listing all known repos and their key traits.</summary>
    public static string GetOverview()
    {
        var lines = new List<string>
        {
            "# CI Investigation Guide — .NET Repositories",
            "",
            "## Quick Reference",
            "",
            "| Repo | Helix? | TRX in Helix? | Best failure pattern | Primary test results |",
            "|------|--------|---------------|---------------------|---------------------|",
        };

        foreach (var p in s_profiles.Values)
        {
            var helix = p.UsesHelix ? "✅" : "❌";
            var trx = p.UploadsTestResultsToHelix ? "✅" : "❌";
            var pattern = p.FailureSearchPatterns.Length > 0 ? $"`{p.FailureSearchPatterns[0]}`" : "N/A";
            var results = p.UploadsTestResultsToHelix
                ? "helix_test_results"
                : p.UsesHelix ? "azdo_test_runs → azdo_test_results" : "azdo_timeline → azdo_log";
            lines.Add($"| {p.DisplayName} | {helix} | {trx} | {pattern} | {results} |");
        }

        lines.Add("");
        lines.Add("## Key Insight");
        lines.Add("");
        lines.Add("**No major .NET repo uploads TRX files to Helix.** The `helix_test_results` tool will fail for most repos. ");
        lines.Add("Use `azdo_test_runs` + `azdo_test_results` for structured test results, or `helix_search_log` with repo-specific patterns for console output.");

        return string.Join('\n', lines);
    }

    private static string FormatProfile(CiRepoProfile profile)
    {
        var lines = new List<string>
        {
            $"# {profile.DisplayName} — CI Investigation Guide",
            "",
            $"**Uses Helix:** {(profile.UsesHelix ? "Yes" : "No")}",
            $"**Test results in Helix:** {(profile.UploadsTestResultsToHelix ? "Yes — use helix_test_results" : "No — use azdo_test_runs + azdo_test_results")}",
            $"**Test result location:** {profile.TestResultLocation}",
        };

        if (profile.HelixTaskNames.Length > 0)
        {
            lines.Add($"**Helix task name in AzDO timeline:** {string.Join(", ", profile.HelixTaskNames.Select(n => $"'{n}'"))}");
        }

        lines.Add("");
        lines.Add("## Recommended Search Patterns");
        lines.Add("");
        foreach (var p in profile.FailureSearchPatterns)
            lines.Add($"- `{p}`");

        lines.Add("");
        lines.Add("## Common Failure Categories");
        lines.Add("");
        foreach (var c in profile.CommonFailureCategories)
            lines.Add($"- {c}");

        lines.Add("");
        lines.Add("## Investigation Tips");
        lines.Add("");
        foreach (var t in profile.InvestigationTips)
            lines.Add($"- {t}");

        return string.Join('\n', lines);
    }

    private static string GetGeneralGuide(string repoName)
    {
        return $"""
            # CI Investigation Guide — {repoName}

            No specific profile found for '{repoName}'. Here are general recommendations:

            ## Getting Test Results
            1. **Try `helix_test_results`** — works if the repo uploads TRX or xUnit XML to Helix
            2. **If that fails, use `azdo_test_runs` + `azdo_test_results`** — most repos publish results to AzDO
            3. **Use `helix_search_log`** with these patterns to find failures in console output:
               - `'  Failed'` (2 leading spaces) — dotnet test / xUnit summary
               - `'[FAIL]'` — xUnit runner failure marker
               - `'Error Message:'` — test error details
               - `'exit code'` — process crashes

            ## Getting Build Failures
            1. Use `azdo_timeline` to find failed steps (filter='failed')
            2. Use `azdo_log` or `azdo_search_log` to read the failed step's log
            3. Search for `'error MSB'` for MSBuild errors, `'error NU'` for NuGet errors
            """;
    }
}
