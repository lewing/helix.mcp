namespace HelixTool.Core;

/// <summary>
/// Per-repo CI investigation profile with patterns, tips, and tool guidance.
/// This is a knowledge base — it's intentionally large and data-dense.
/// </summary>
public sealed record CiRepoProfile
{
    public required string RepoName { get; init; }
    public required string DisplayName { get; init; }
    public required bool UsesHelix { get; init; }
    public required string HelixTestResultAvailability { get; init; }
    public required string TestResultLocation { get; init; }
    public required string[] FailureSearchPatterns { get; init; }
    public required string[] HelixTaskNames { get; init; }
    public required string[] CommonFailureCategories { get; init; }
    public required string[] InvestigationTips { get; init; }

    // --- Enriched fields ---

    /// <summary>Named pipelines with optional definition IDs (e.g., "runtime (129)").</summary>
    public string[] PipelineNames { get; init; } = [];

    /// <summary>AzDO org/project (e.g., "dnceng-public/public").</summary>
    public string OrgProject { get; init; } = "dnceng-public/public";

    /// <summary>Exit code meanings (e.g., "-4: Infrastructure failure").</summary>
    public string[] ExitCodeMeanings { get; init; } = [];

    /// <summary>How work items are named (e.g., "AssemblyName.Tests" or "workitem_N").</summary>
    public string WorkItemNamingPattern { get; init; } = "";

    /// <summary>Critical gotchas that agents MUST know about this repo's CI.</summary>
    public string[] KnownGotchas { get; init; } = [];

    /// <summary>Ordered list of recommended tool calls for investigation.</summary>
    public string[] RecommendedInvestigationOrder { get; init; } = [];

    /// <summary>Test framework used (e.g., "xUnit", "NUnit").</summary>
    public string TestFramework { get; init; } = "";

    /// <summary>How tests execute (e.g., "dotnet test", "xunit.console.dll", "Make-based").</summary>
    public string TestRunnerModel { get; init; } = "";

    /// <summary>Key file types uploaded to Helix (e.g., "*.testResults.xml.txt").</summary>
    public string[] UploadedFiles { get; init; } = [];
}

/// <summary>
/// Provides domain knowledge about CI infrastructure for .NET repositories.
/// Used by MCP tools, resources, and error messages to guide investigation.
/// Covers 9 repos: runtime, aspnetcore, sdk, roslyn, efcore, dotnet, maui, macios, android.
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
            HelixTestResultAvailability = "partial",
            TestResultLocation = "Mixed: CoreCLR uploads *.testResults.xml.txt (helix_parse_uploaded_trx works); libraries publish to AzDO only via Arcade reporter; XHarness (iOS/Android) uploads testResults.xml",
            PipelineNames = ["runtime (129)"],
            OrgProject = "dnceng-public/public",
            TestFramework = "xUnit",
            TestRunnerModel = "Mixed: dotnet test (libraries), XUnitWrapperGenerator (CoreCLR), XHarness (iOS/Android)",
            WorkItemNamingPattern = "<AssemblyName>.Tests (e.g., System.IO.FileSystem.Watcher.Tests); wasm: WasmTestOn{Browser}-{Runtime}-ST-{Namespace}.Tests",
            FailureSearchPatterns =
            [
                "[FAIL]",          // xUnit runner failure marker (best for runtime)
                "Failed:",         // summary line: "Failed: N"
                "Error Message:",  // xUnit error detail block
                "exit code",       // process crashes
                "SIGABRT",         // native crashes
            ],
            HelixTaskNames = ["Send to Helix"],
            ExitCodeMeanings =
            [
                "0: Passed (but can coexist with [FAIL] results — don't trust exit code alone)",
                "-3: Timeout / crash",
                "-4: Infrastructure failure (Docker image pull, environment setup)",
                "130: SIGINT (process killed)",
            ],
            KnownGotchas =
            [
                "Helix exit code 0 can coexist with [FAIL] test results — xUnit runner may set _commandExitCode=0 despite failures",
                "azdo_test_runs metadata shows failedTests=0 while azdo_test_results contains real failures — always drill in",
                "helix_parse_uploaded_trx works ONLY for CoreCLR tests and XHarness (iOS/Android) — NOT for libraries tests",
                "PR builds run minimal test subsets based on changed files — missing test legs are intentional, not broken",
                "Runtime has 80+ pipeline definitions — the main PR gate is 'runtime' (129), others are outerloop/stress/platform",
                "Wasm build tests follow SDK pattern — failures are build errors, not xUnit assertions",
            ],
            RecommendedInvestigationOrder =
            [
                "azdo_timeline(buildId, filter='failed') → identify failed jobs",
                "Classify: 'Send to Helix' failed → test failure; 'Build product' failed → build error; exit -4/-3 → infra",
                "helix_status(jobId) → find failed work items + exit codes",
                "azdo_test_runs(buildId) → get test run IDs",
                "azdo_test_results(buildId, runId) → structured failures (always drill in, don't trust summary counts)",
                "helix_search_log(jobId, workItem, '[FAIL]') → console confirmation",
                "helix_logs(jobId, workItem) → full context if needed",
            ],
            UploadedFiles =
            [
                "console.*.log (always)",
                "*.testResults.xml.txt (CoreCLR tests only — in testResults category)",
                "testResults.xml (XHarness iOS/Android unit tests — in testResults category)",
            ],
            CommonFailureCategories =
            [
                "Test assertion failures (xUnit [FAIL] markers)",
                "Infrastructure failures (Docker image pull, timeout, exit code -4)",
                "Native crashes (SIGABRT, core dumps)",
                "Timeout failures (test host killed after hang timeout)",
                "Wasm build-as-test failures (build errors, docker failures, not assertions)",
            ],
            InvestigationTips =
            [
                "helix_parse_uploaded_trx works for CoreCLR tests (*.testResults.xml.txt) and XHarness (testResults.xml) but NOT for libraries tests",
                "Use helix_search_log with '[FAIL]' to find xUnit test failures — this is the best pattern for runtime",
                "Use helix_search_log with 'Error Message:' to get error details after finding [FAIL] hits",
                "azdo_test_runs + azdo_test_results is the most reliable path for structured results across all test types",
                "'  Failed' pattern does NOT work for runtime — runtime uses raw xUnit runner, not dotnet test CLI format",
                "Check failureCategory in helix_status: Crash/InfrastructureError → infra, not a test bug",
            ],
        },

        ["aspnetcore"] = new CiRepoProfile
        {
            RepoName = "aspnetcore",
            DisplayName = "dotnet/aspnetcore",
            UsesHelix = true,
            HelixTestResultAvailability = "none",
            TestResultLocation = "AzDO test reporting only — TRX consumed locally by Arcade reporter, published to AzDO, never uploaded to Helix blob storage",
            PipelineNames = ["aspnetcore-ci (83)"],
            OrgProject = "dnceng-public/public",
            TestFramework = "xUnit",
            TestRunnerModel = "dotnet test (vstest adapter)",
            WorkItemNamingPattern = "{TestProjectName}--{TargetFramework} (e.g., Microsoft.AspNetCore.Server.Kestrel.Tests--net11.0)",
            FailureSearchPatterns =
            [
                "  Failed",        // dotnet test summary line (2 leading spaces) — best for aspnetcore
                "[FAIL]",          // xUnit runner format (fewer matches but valid)
                "Error Message:",  // test error detail
                "Test Run Failed.",// test run summary
                "exit code",       // process crash
            ],
            HelixTaskNames = ["Send to Helix"],
            ExitCodeMeanings =
            [
                "0: Passed",
                "1: Test failure",
                "-3: Timeout / crash",
                "-4: Infrastructure failure",
            ],
            KnownGotchas =
            [
                "helix_parse_uploaded_trx ALWAYS fails — aspnetcore never uploads TRX to Helix blob storage",
                "azdo_test_runs summary counts can be inaccurate (failedTests=0 with real failures) — always drill into azdo_test_results",
                "Some test projects produce 400+ individual .log files per work item (e.g., Interop.FunctionalTests) — these are test output, not results",
            ],
            RecommendedInvestigationOrder =
            [
                "helix_status(jobId) → which work items failed? (maps to test projects)",
                "azdo_test_runs(buildId) + azdo_test_results(buildId, runId) → structured failures with stack traces",
                "helix_search_log(jobId, workItem, '  Failed') → quick cross-reference from console",
                "Search GitHub issues with test name + label:'Known Build Error'",
            ],
            UploadedFiles =
            [
                "console.*.log (always)",
                "vstest.log, vstest.host.*.log, vstest.datacollector.*.log",
                "net11.0_global.log",
                "Never: *.trx, testResults.xml, *.binlog, *.dmp",
            ],
            CommonFailureCategories =
            [
                "Test assertion failures (dotnet test '  Failed' markers)",
                "Timeout failures (test hangs past blame timeout)",
                "Process crashes (testhost exit code non-zero)",
                "Connection/port conflicts in server tests",
            ],
            InvestigationTips =
            [
                "helix_parse_uploaded_trx will ALWAYS fail — aspnetcore never uploads TRX to Helix",
                "Use helix_search_log with '  Failed' (2 leading spaces) to find failed test names — gives N+1 matches for N failures",
                "Use azdo_test_runs + azdo_test_results for structured test results — this is the primary path",
                "Warning: azdo_test_runs summary counts can be inaccurate — always drill into azdo_test_results",
                "Skip helix_find_files('*.trx') — always empty for aspnetcore",
            ],
        },

        ["sdk"] = new CiRepoProfile
        {
            RepoName = "sdk",
            DisplayName = "dotnet/sdk",
            UsesHelix = true,
            HelixTestResultAvailability = "none",
            TestResultLocation = "AzDO test reporting — tests often crash before TRX generation; synthetic WorkItemExecution results for crashed items",
            PipelineNames = ["dotnet-sdk-public-ci (101)"],
            OrgProject = "dnceng-public/public",
            TestFramework = "xUnit (but build-as-test pattern dominates)",
            TestRunnerModel = "dotnet test inside Helix work items — SDK 'tests' ARE builds exercising the SDK",
            WorkItemNamingPattern = "<TestAssembly>.dll or <TestAssembly>.dll.<N> for sharded (e.g., EndToEnd.Tests.dll.1)",
            FailureSearchPatterns =
            [
                "Failed",          // generic — best for SDK since crashes dominate
                "Error",           // library load failures, environment issues
                "exit code",       // process crash (common: exit 130)
                "error MSB",       // MSBuild errors in test scenarios
                "incompatible",    // architecture mismatch (x86_64 vs arm64)
            ],
            HelixTaskNames = ["🟣 Run TestBuild Tests"],
            ExitCodeMeanings =
            [
                "0: Passed",
                "1: Test failure (tests ran and failed)",
                "130: Crash (SIGINT / process killed — most common failure mode)",
                "-4: Infrastructure failure (docker/environment)",
            ],
            KnownGotchas =
            [
                "helix_parse_uploaded_trx ALWAYS fails — SDK never uploads TRX to Helix",
                "SDK uses build-as-test pattern — many 'test failures' are actually build errors exercising the SDK",
                "Helix task name is '🟣 Run TestBuild Tests' (with emoji!) — searching for 'Send to Helix' returns nothing",
                "azdo_test_runs metadata shows failedTests=0 even with hundreds of crashed work items — always drill into azdo_test_results",
                "Crashed work items produce synthetic results: testCaseTitle='X.dll Work Item', errorMessage='The Helix Work Item failed'",
                "'  Failed' pattern is unreliable for SDK — use 'Failed' and 'Error' instead since crashes dominate",
            ],
            RecommendedInvestigationOrder =
            [
                "azdo_timeline(buildId, filter='failed') → identify failed jobs",
                "Classify: '🟣 Build' failed → SDK build break; '🟣 Run TestBuild Tests' failed → test/infra failure; all tests skipped → upstream build failure",
                "helix_status(jobId) → exit codes — 130/-4 = infra, 1 = real test failure",
                "azdo_test_runs(buildId) → get test run IDs",
                "azdo_test_results(buildId, runId) → check for real vs synthetic (WorkItemExecution = crash, not assertion failure)",
                "helix_search_log(jobId, workItem, 'Failed') → console error details",
                "helix_logs(jobId, workItem) → full context",
            ],
            UploadedFiles =
            [
                "console.*.log (always — often the ONLY file for crashed work items)",
                "Never on crash: *.trx, *.binlog, testResults.xml",
            ],
            CommonFailureCategories =
            [
                "SDK compilation failure ('🟣 Build' task fails → all test tasks skipped)",
                "Build-as-test failures (SDK compilation errors surface as test failures)",
                "Architecture mismatch errors (x86_64 on arm64 — 'incompatible' in dlopen)",
                "Process crashes (exit code 130 — SIGINT during test)",
                "MSBuild errors in test scenarios",
            ],
            InvestigationTips =
            [
                "helix_parse_uploaded_trx will ALWAYS fail — sdk never uploads TRX to Helix",
                "SDK uses build-as-test pattern — many 'test failures' are actually build errors",
                "Helix task name is '🟣 Run TestBuild Tests' (with emoji) — use this in azdo_timeline searches",
                "Use azdo_test_runs + azdo_test_results for structured results — but watch for synthetic WorkItemExecution entries",
                "Use helix_search_log with 'Failed' or 'Error' (not '[FAIL]') for SDK — crashes don't produce xUnit output",
            ],
        },

        ["roslyn"] = new CiRepoProfile
        {
            RepoName = "roslyn",
            DisplayName = "dotnet/roslyn",
            UsesHelix = true,
            HelixTestResultAvailability = "none",
            TestResultLocation = "AzDO test reporting — Helix failures are dominated by crashes; crash dumps uploaded to Helix",
            PipelineNames = ["roslyn-CI (95)"],
            OrgProject = "dnceng-public/public",
            TestFramework = "xUnit",
            TestRunnerModel = "RunTests.dll --helix (embedded Helix invocation inside test runner tasks)",
            WorkItemNamingPattern = "workitem_<N> (generic numbered: workitem_0, workitem_1 — NOT descriptive, can't determine tests from name)",
            FailureSearchPatterns =
            [
                "aborted",         // process abort — best for roslyn crashes
                "Process exited",  // testhost crash
                "has failed",      // "Work item workitem_N in job ... has failed"
                "exit code",       // non-zero exit
                "Stack overflow",  // common roslyn crash mode
                "OutOfMemory",     // OOM in compilation
            ],
            HelixTaskNames = [],
            ExitCodeMeanings =
            [
                "0: Passed",
                "1: Test failure",
                "-3: Timeout / crash",
                "-4: Infrastructure failure",
            ],
            KnownGotchas =
            [
                "helix_parse_uploaded_trx ALWAYS fails — roslyn uses xUnit XML (work-item-test-results.xml) consumed by reporter, never uploaded",
                "NO separate 'Send to Helix' task — Helix is invoked INSIDE 'Run Unit Tests' (Windows) or 'Test' (Linux) tasks",
                "Helix job IDs only appear in task log output as 'Work item workitem_N in job <GUID> has failed' — must read the log to extract them",
                "azdo_test_runs shows failedTests=0 across ALL runs on crash — crashes are invisible in AzDO structured data",
                "Work item names are generic (workitem_0, workitem_1) — you cannot tell which tests are in a work item from its name",
                "Crashes are the dominant failure mode, not assertion failures — search for 'aborted' or 'Process exited', not '[FAIL]'",
                "Crash dumps are uploaded (unique among dotnet repos): crash.*.dmp, testhost.exe.*.dmp, core.*",
            ],
            RecommendedInvestigationOrder =
            [
                "azdo_timeline(buildId, filter='failed') → identify failed jobs",
                "Read failed 'Run Unit Tests' or 'Test' task log → search for 'has failed' to find Helix job GUIDs",
                "helix_status(jobId) → exit codes + failureCategory (InfrastructureError = crash)",
                "If crash: helix_search_log(jobId, workItem, 'aborted') → crash details",
                "If crash: helix_files(jobId, workItem) → crash dumps available?",
                "If test failure (not crash): azdo_test_runs + azdo_test_results → structured results",
                "helix_logs(jobId, workItem) → full context",
            ],
            UploadedFiles =
            [
                "console.*.log (always)",
                "crash.*.dmp (Windows crashes)",
                "testhost.exe.*.dmp (Windows crashes)",
                "crash.*.dotnet.dmp (Linux crashes)",
                "core.* (Linux core dumps)",
                "Never: *.trx, *.binlog",
            ],
            CommonFailureCategories =
            [
                "Stack overflow in compiler (recursive AST processing)",
                "OutOfMemoryException (large solution analysis)",
                "Testhost crashes (Process exited with code X)",
                "Timeout failures",
            ],
            InvestigationTips =
            [
                "helix_parse_uploaded_trx will ALWAYS fail — roslyn never uploads TRX or result XML to Helix",
                "Roslyn Helix failures are usually crashes, not assertion failures — check helix_status failureCategory first",
                "Helix tasks are hidden inside 'Run Unit Tests' (Windows) or 'Test' (Linux) — no dedicated 'Send to Helix' step",
                "Use helix_search_log with 'aborted' or 'Process exited' to find crashes",
                "Use azdo_test_runs + azdo_test_results for structured results — but only works for non-crash failures",
                "[FAIL] pattern does NOT work for roslyn — the xUnit output format differs",
                "Same test often crashes across multiple platforms (Windows + Linux) — check both",
            ],
        },

        ["efcore"] = new CiRepoProfile
        {
            RepoName = "efcore",
            DisplayName = "dotnet/efcore",
            UsesHelix = true,
            HelixTestResultAvailability = "none",
            TestResultLocation = "AzDO test reporting — dual execution: local agents + Helix; Arcade reporter publishes to AzDO",
            PipelineNames = ["efcore-ci (17)"],
            OrgProject = "dnceng-public/public",
            TestFramework = "xUnit",
            TestRunnerModel = "xunit.console.dll v2.9.3 (NOT dotnet test) — uses raw xUnit console runner",
            WorkItemNamingPattern = "<TestAssembly>.dll (e.g., Microsoft.EntityFrameworkCore.SqlServer.FunctionalTests.dll)",
            FailureSearchPatterns =
            [
                "[FAIL]",          // xUnit console runner — best for efcore
                "Failed:",         // summary: "Total: N, Errors: N, Failed: N, Skipped: N"
                "TEST EXECUTION SUMMARY", // anchor marking end of test run
                "Error Message:",  // error detail
                "exit code",       // process crash (common: exit -3 on macOS)
            ],
            HelixTaskNames = ["Send job to helix"],
            ExitCodeMeanings =
            [
                "0: Passed",
                "1: Test failure",
                "-3: Crash/timeout (very common on macOS — infrastructure, not code bug)",
            ],
            KnownGotchas =
            [
                "helix_parse_uploaded_trx ALWAYS fails — efcore uses xunit.console.dll which outputs xUnit XML, not TRX; result consumed by reporter, never uploaded",
                "Helix task name is 'Send job to helix' (LOWERCASE 'j') — different from runtime/aspnetcore's 'Send to Helix'",
                "Dual execution: same tests run both on local agents AND via Helix — AzDO test run naming tells you which (Windows-xunit_N = local, queue name = Helix)",
                "macOS Helix machines crash frequently (exit -3, all work items fail simultaneously) — usually resolved by retry",
                "Heavy test skipping: some runs show thousands total with 0 passed AND 0 failed — all skipped (wrong platform for DB tests)",
                "Crashed macOS work items often have ZERO uploaded files — diagnosis must come from AzDO task log",
                "Pipeline has 'Publish TRX Test Results' task but it ALWAYS finds nothing — legacy leftover",
                "SQL Server tests only run on Ubuntu (Docker) and Windows (local) — macOS skips them",
            ],
            RecommendedInvestigationOrder =
            [
                "azdo_timeline(buildId, filter='failed') → which phase failed? (local vs Helix)",
                "If Helix: find 'Send job to helix' task log → extract Helix job IDs",
                "helix_status(jobId) → exit codes per work item (all -3 on one queue = infra issue, retry)",
                "If specific DLLs fail → test failure, investigate further",
                "If local phase failed → check task log for test output directly",
                "azdo_test_runs(buildId) → find runs with failures (~51 runs: local + Helix combined)",
                "azdo_test_results(buildId, runId) → structured failures",
                "helix_search_log(jobId, workItem, '[FAIL]') → console confirmation",
            ],
            UploadedFiles =
            [
                "console.*.log (usually — but crashed macOS items may have nothing)",
                "Never: *.trx, *.binlog",
            ],
            CommonFailureCategories =
            [
                "macOS infrastructure crashes (exit code -3, all items on queue fail — retry resolves)",
                "SQL Server container startup failures (Ubuntu Docker queue)",
                "Test assertion failures ([FAIL] in xUnit console output)",
                "Connection timeout to test databases",
                "Cosmos emulator startup failures (test skips or timeouts)",
            ],
            InvestigationTips =
            [
                "helix_parse_uploaded_trx will ALWAYS fail — efcore never uploads TRX to Helix",
                "Helix task name is 'Send job to helix' (lowercase 'j') — different from other repos",
                "macOS tests frequently fail with exit -3 (infrastructure issue, not code bug) — check if ALL items on the queue failed",
                "Use azdo_test_runs + azdo_test_results for structured results — both local and Helix results appear",
                "Use helix_search_log with '[FAIL]' for xUnit failures in console output",
                "Check both local runs (Windows-xunit_N, Linux-xunit_N) AND Helix runs — same test might pass locally but fail on Helix",
            ],
        },

        ["dotnet"] = new CiRepoProfile
        {
            RepoName = "dotnet/dotnet",
            DisplayName = "dotnet/dotnet (VMR)",
            UsesHelix = false,
            HelixTestResultAvailability = "none",
            TestResultLocation = "No Helix — ~30 validation tests (TRX + xUnit XML) published to AzDO via standard tasks; build errors in AzDO build logs",
            PipelineNames = ["dotnet-unified-build (278)"],
            OrgProject = "dnceng-public/public",
            TestFramework = "xUnit (for scenario tests) + MSTest (for unit tests)",
            TestRunnerModel = "dotnet test on build agent (no Helix) — build IS the primary test",
            WorkItemNamingPattern = "N/A — VMR does not use Helix work items",
            FailureSearchPatterns =
            [
                "error MSB3073",   // MSBuild command execution failure (component build)
                "error MSB",       // general MSBuild errors
                "error NU",        // NuGet errors
                "FAILED",          // build target failure
                "exited with code",// component build exit code
            ],
            HelixTaskNames = [],
            ExitCodeMeanings =
            [
                "1: Component build failure (most common)",
            ],
            KnownGotchas =
            [
                "VMR does NOT use Helix — ALL helix_* tools will fail or return irrelevant data",
                "Build IS the primary test — 'does the entire product build?' is the validation",
                "Only ~30 validation tests (13 unit + 17 scenario) per vertical — NOT full repo test suites",
                "~44 platform verticals in main stage — failures are often platform-specific",
                "Multi-pass builds (BuildPass2/Pass3) depend on earlier pass outputs — pass 1 failure cascades",
                "'Shortstack' verticals build a subset of the product",
                "Codeflow PRs (title 'Source code updates from dotnet/dotnet') are automated — check the source repo for actual failure",
                "Source-only build stage tests building from source tarball — failures indicate packaging issues, not code bugs",
            ],
            RecommendedInvestigationOrder =
            [
                "azdo_timeline(buildId, filter='failed') → identify failed verticals",
                "Classify by stage: 'VMR Vertical Build' = product build, 'VMR Source-Only Build' = source packaging, 'VMR Vertical Build Validation' = smoke test",
                "Find 'Build' task logId in timeline → read with azdo_log or azdo_search_log",
                "Search for 'error MSB' to identify which component repo failed",
                "For test failures (rare): azdo_test_runs + azdo_test_results → structured results",
                "Check {Vertical}_BuildLogs_Attempt1 artifact for full build logs",
            ],
            UploadedFiles =
            [
                "N/A — VMR does not use Helix; build logs are in AzDO artifacts ({Vertical}_BuildLogs_Attempt1)",
            ],
            CommonFailureCategories =
            [
                "Component build failures (error MSB3073 — a repo's build.sh/build.cmd failed)",
                "NuGet restore failures",
                "Source-build compatibility issues",
                "Cross-repo dependency version mismatches",
                "Platform-specific build failures (builds on Linux_x64 but fails on OSX_arm64)",
                "Multi-pass dependency failures (pass 1 → pass 2/3 cascade)",
            ],
            InvestigationTips =
            [
                "VMR does NOT use Helix — all helix_* tools will fail or return irrelevant data",
                "Use azdo_timeline to find failed build steps, then azdo_log to read the log",
                "Use azdo_search_log with 'error MSB' for MSBuild failures",
                "Identify which component repo failed from the error — the fix is in that repo, not the VMR",
                "Codeflow PRs are automated — check the source repo for the actual failure",
                "Test failures are rare — when they occur, standard AzDO test results work (TRX + xUnit XML published via standard tasks)",
            ],
        },

        ["maui"] = new CiRepoProfile
        {
            RepoName = "maui",
            DisplayName = "dotnet/maui",
            UsesHelix = true,
            HelixTestResultAvailability = "varies",
            TestResultLocation = "Mixed across 3 pipelines: unit tests → AzDO only; UI tests → AzDO only (no Helix); device tests → Helix (testResults.xml) AND AzDO",
            PipelineNames = ["maui-pr (302)", "maui-pr-uitests (313)", "maui-pr-devicetests (314)"],
            OrgProject = "dnceng-public/public",
            TestFramework = "xUnit (unit + device tests), Appium (UI tests)",
            TestRunnerModel = "3 models: xUnit via Helix (unit), Appium on agents (UI), XHarness CLI on-device via Helix (device)",
            WorkItemNamingPattern = "Pipeline-dependent: unit=DLL names; device=APK names (Android), friendly names (iOS/Mac: 'Controls Tests'), module names (Windows: 'Controls.DeviceTests-packaged')",
            FailureSearchPatterns =
            [
                "[FAIL]",                    // xUnit runner (unit tests)
                "INSTRUMENTATION_CODE: -1",  // Android device test failure
                "  Failed",                  // dotnet test summary
                "exit code",                 // process crash
            ],
            HelixTaskNames = ["Send to Helix"],
            ExitCodeMeanings =
            [
                "0: Passed",
                "1: Test failure",
                "-3: Timeout / crash",
                "-4: Infrastructure failure",
            ],
            KnownGotchas =
            [
                "MAUI has THREE separate pipelines — check which one failed before investigating",
                "maui-pr (302): unit tests via Helix — helix_parse_uploaded_trx FAILS (no TRX uploaded)",
                "maui-pr-uitests (313): Appium UI tests — NO Helix at all, 168 jobs, all helix_* tools useless",
                "maui-pr-devicetests (314): XHarness device tests — helix_parse_uploaded_trx WORKS (testResults.xml uploaded)",
                "Windows device test work items are extremely file-heavy — ~85 per-control XML files per work item",
                "Work items show '.Attempt.2' suffix on retries (e.g., Controls.DeviceTests-packaged.Attempt.2)",
                "Device test work item naming varies by platform: APK (Android), friendly name (iOS/Mac), module name (Windows)",
            ],
            RecommendedInvestigationOrder =
            [
                "Identify which pipeline failed: maui-pr (unit), maui-pr-uitests (UI), maui-pr-devicetests (device)",
                "For maui-pr (unit): azdo_test_runs + azdo_test_results → structured results; helix_search_log '[FAIL]' for console; SKIP helix_parse_uploaded_trx",
                "For maui-pr-uitests (UI): azdo_test_runs + azdo_test_results ONLY — no Helix tools useful; azdo_timeline for control group/platform",
                "For maui-pr-devicetests (device): helix_parse_uploaded_trx WORKS → parse testResults.xml; also azdo_test_runs as alternative",
                "For device tests: check platform-specific logs — adb-logcat-*.log (Android), MacCatalyst.system.log (Mac)",
            ],
            UploadedFiles =
            [
                "console.*.log (unit + device tests)",
                "testResults.xml (device tests only — in testResults category)",
                "adb-logcat-*.log (Android device tests)",
                "MacCatalyst.system.log, test-maccatalyst.log (Mac device tests)",
                "<Assembly>.DeviceTests.log (per-assembly device test logs)",
                "TestResults-com_microsoft_maui_controls_devicetests_<Control>.xml (~85 files, Windows device tests)",
                "Never (unit tests): *.trx, testResults.xml",
            ],
            CommonFailureCategories =
            [
                "Unit test assertion failures (xUnit [FAIL])",
                "Device test failures (INSTRUMENTATION_CODE: -1 on Android)",
                "Appium UI test failures (element not found, timeout, emulator issues)",
                "XHarness orchestration errors (device not found, app install failure)",
                "Infrastructure crashes (exit -3/-4)",
            ],
            InvestigationTips =
            [
                "First identify which of the 3 pipelines failed — investigation approach differs completely",
                "maui-pr: helix_parse_uploaded_trx fails; use azdo_test_runs + azdo_test_results or helix_search_log '[FAIL]'",
                "maui-pr-uitests: NO Helix — skip all helix_* tools; AzDO test runs are the only source",
                "maui-pr-devicetests: helix_parse_uploaded_trx WORKS — try it first for structured per-test results",
                "For Android device tests, check INSTRUMENTATION_RESULT lines in console for test summaries",
                "UI test AzDO run names follow pattern: <ControlGroup>_<platform>_ui_tests_<runtime>_controls_<api>",
            ],
        },

        ["macios"] = new CiRepoProfile
        {
            RepoName = "macios",
            DisplayName = "xamarin/macios",
            UsesHelix = false,
            HelixTestResultAvailability = "none",
            TestResultLocation = "AzDO test reporting on devdiv org — NUnit XML (vsts-*.xml) published via PublishTestResults@2; HTML reports on VSDrops",
            PipelineNames = ["xamarin-macios-sim-pr-tests", "xamarin-macios (13947)", "xamarin-macios-pr-apidiff"],
            OrgProject = "devdiv/DevDiv",
            TestFramework = "NUnit (primary — 35+ csproj files)",
            TestRunnerModel = "Make-based (make -C tests jenkins) — runs on Mac agents directly, NOT Helix",
            WorkItemNamingPattern = "N/A — no Helix work items; test stages named by platform/runtime: monotouch_ios, dotnettests_macos, etc.",
            FailureSearchPatterns =
            [
                "TESTS_JOBSTATUS]Failed",  // AzDO variable set on failure
                "NUnit",                   // test framework output
                "crash",                   // crash reports collected post-test
            ],
            HelixTaskNames = [],
            ExitCodeMeanings = [],
            KnownGotchas =
            [
                "⚠️ NOT on dnceng — lives on devdiv.visualstudio.com/DevDiv — standard helix_* AND ado-dnceng-* tools DO NOT WORK",
                "No Helix at all — tests run directly on Mac agents via 'make -C tests jenkins'",
                "Uses NUnit (NOT xUnit) — test patterns and result formats differ from all other dotnet repos",
                "Result format is NUnit XML (vsts-*.xml) — NOT xUnit XML or TRX",
                "Dynamic test matrix generated by configure_build stage — test set varies by PR labels",
                "Tests across 5 macOS versions × architectures (M1 arm64, x64)",
                "XHarness is referenced but only for labeling/harness — NOT the same XHarness CLI used by runtime/MAUI",
                "Uses 1ES/MicroBuild pipeline templates, Provisionator for environment setup — not Arcade",
                "Also has GitHub Actions workflow for Linux build verification",
            ],
            RecommendedInvestigationOrder =
            [
                "⚠️ Standard dnceng tools won't work — you need devdiv AzDO access",
                "Look for PublishTestResults tasks in timeline for NUnit XML results",
                "AzDO test runs on devdiv org are the primary structured result source",
                "Check HtmlReport.zip artifact for visual test summary",
                "Check crash-reports/ artifact for macOS/iOS crash logs",
                "Console output from 'make -C tests jenkins' has test runner output",
            ],
            UploadedFiles =
            [
                "N/A — no Helix; artifacts: HtmlReport.zip, TestSummary.md, crash-reports/, VSDrops HTML results",
            ],
            CommonFailureCategories =
            [
                "iOS simulator test failures (various macOS versions)",
                "macOS test failures (M1 arm64 vs x64 differences)",
                "Binding generator test failures",
                "Crash reports (collected by collect-and-upload-crash-reports.sh)",
                "Environment setup failures (Provisionator, Xcode, brew)",
            ],
            InvestigationTips =
            [
                "⚠️ This repo is on devdiv.visualstudio.com — standard dnceng-public tools DO NOT WORK",
                "No Helix — skip ALL helix_* tools entirely",
                "AzDO test runs on devdiv are the primary result source (NUnit XML format)",
                "Test stages: monotouch_* (Mono runtime), dotnettests_* (.NET runtime), generator, linker, introspection, fsharp, xcframework",
                "macOS stages: mac_12_m1 through mac_26_arm64 — test across 5 OS versions",
                "Check HtmlReport.zip artifact for visual summary — often more useful than raw AzDO results",
            ],
        },

        ["android"] = new CiRepoProfile
        {
            RepoName = "android",
            DisplayName = "dotnet/android",
            UsesHelix = false,
            HelixTestResultAvailability = "none",
            TestResultLocation = "AzDO test reporting — TRX format (VSTest) published via PublishTestResults@2; primarily on devdiv org, fork PRs on dnceng-public",
            PipelineNames = ["Xamarin.Android (devdiv)", "Xamarin.Android-Private (devdiv)", "Public fork pipeline (dnceng-public)"],
            OrgProject = "devdiv/DevDiv",
            TestFramework = "NUnit (primary) + xUnit (secondary)",
            TestRunnerModel = "dotnet test --logger trx — runs on agents with Android emulator managed by MSBuild targets",
            WorkItemNamingPattern = "N/A — no Helix work items; tests sharded via dotnet-test-slicer",
            FailureSearchPatterns =
            [
                "Failed",          // dotnet test output
                "Error",           // build/test errors
                "AcquireAndroidTarget", // emulator setup failures
            ],
            HelixTaskNames = [],
            ExitCodeMeanings = [],
            KnownGotchas =
            [
                "⚠️ Primarily on devdiv.visualstudio.com — standard helix_* AND ado-dnceng-* tools DO NOT WORK for internal builds",
                "Fork PRs go to dnceng-public — standard tools work ONLY for those builds",
                "No Helix, no XHarness — tests run directly on agents via 'dotnet test'",
                "Uses NUnit (primary) + xUnit — mixed test framework, unlike most dotnet repos",
                "Result format is TRX (VSTest) — differs from macios (NUnit XML)",
                "Android emulators managed by MSBuild targets (AcquireAndroidTarget/ReleaseAndroidTarget) — not XHarness",
                "Test sharding via dotnet-test-slicer — not Helix work items",
                "Builds on macOS + Windows + Linux (broader than macios which is Mac-only)",
                "Shares Xamarin.yaml-templates repo with macios",
            ],
            RecommendedInvestigationOrder =
            [
                "Check which org the build is on — devdiv (internal) vs dnceng-public (fork PRs)",
                "Standard dnceng tools work for public fork builds ONLY",
                "AzDO test runs (TRX/VSTest format) are the primary result source",
                "Skip ALL helix_* tools — no Helix usage",
                "Look for AcquireAndroidTarget task failures for emulator setup issues",
                "Check dotnet test output for NUnit/xUnit failures",
            ],
            UploadedFiles =
            [
                "N/A — no Helix; TRX files published to AzDO test runs",
            ],
            CommonFailureCategories =
            [
                "Android emulator startup/connection failures (AcquireAndroidTarget)",
                "Build failures (dotnet build on 3 platforms)",
                "Test assertion failures (NUnit + xUnit)",
                "Environment setup failures (Android SDK, emulator images)",
            ],
            InvestigationTips =
            [
                "⚠️ This repo is primarily on devdiv.visualstudio.com — standard dnceng-public tools work ONLY for fork PR builds",
                "No Helix — skip ALL helix_* tools entirely",
                "AzDO test runs (TRX format) are the primary result source",
                "Look for emulator issues in AcquireAndroidTarget task logs",
                "Test sharding uses dotnet-test-slicer, not Helix partitioning",
                "Builds on macOS + Windows + Linux — check all platform legs",
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
            "| Repo | Org | Helix? | Helix structured results? | Best log pattern | Start with |",
            "|------|-----|--------|--------------------------|------------------|------------|",
        };

        foreach (var p in s_profiles.Values)
        {
            var helix = p.UsesHelix ? "✅" : "❌";
            var trxStatus = p.HelixTestResultAvailability switch
            {
                "partial" => "⚠️ Partial",
                "varies" => "⚠️ Varies",
                _ => p.UsesHelix ? "❌ Fails" : "N/A"
            };
            var pattern = p.FailureSearchPatterns.Length > 0 ? $"`{p.FailureSearchPatterns[0]}`" : "N/A";
            var results = p.HelixTestResultAvailability switch
            {
                "partial" => "helix_parse_uploaded_trx (supported legs)",
                "varies" => "check pipeline notes",
                _ => p.UsesHelix ? "azdo_test_runs → azdo_test_results" : "azdo_timeline → azdo_log"
            };
            lines.Add($"| {p.DisplayName} | {p.OrgProject} | {helix} | {trxStatus} | {pattern} | {results} |");
        }

        lines.Add("");
        lines.Add("## Key Insights");
        lines.Add("");
        lines.Add("- **Use repo profiles to choose the first tool path.** If the table says `❌ Fails`, skip `helix_parse_uploaded_trx` and go straight to `azdo_test_runs + azdo_test_results` for structured results.");
        lines.Add("- **Most .NET repos do NOT upload test results to Helix.** `helix_parse_uploaded_trx` fails for aspnetcore, sdk, roslyn, efcore.");
        lines.Add("- **Partial support exists, but only for specific test legs.** runtime CoreCLR/XHarness tests upload result XML; MAUI device tests (maui-pr-devicetests pipeline) upload testResults.xml.");
        lines.Add("- **Use `helix_search_log` as the remote-first console path.** The best search pattern varies by repo/test runner; check the repo profile before broad log reads.");
        lines.Add("- **azdo_test_runs + azdo_test_results** is the most reliable path for structured results across all repos.");
        lines.Add("- **⚠️ macios and android are on devdiv, not dnceng** — standard `helix_*` and `ado-dnceng-*` tools do not work.");
        lines.Add("- **failedTests=0 is a lie** — always drill into `azdo_test_results`, don't trust run-level summary counts.");

        return string.Join('\n', lines);
    }

    private static string FormatProfile(CiRepoProfile profile)
    {
        var lines = new List<string>
        {
            $"# {profile.DisplayName} — CI Investigation Guide",
            "",
            $"**Org/Project:** {profile.OrgProject}",
        };

        if (profile.PipelineNames.Length > 0)
            lines.Add($"**Pipeline(s):** {string.Join(", ", profile.PipelineNames)}");

        lines.Add($"**Uses Helix:** {(profile.UsesHelix ? "Yes" : "No")}");
        var testResultsLine = profile.HelixTestResultAvailability switch
        {
            "partial" => "Partial — helix_parse_uploaded_trx works for some tests (see TestResultLocation for details), use azdo_test_runs + azdo_test_results for full coverage",
            "varies" => "Varies by pipeline — check pipeline-specific notes below",
            _ => "No — use azdo_test_runs + azdo_test_results"
        };
        lines.Add($"**Test results in Helix:** {testResultsLine}");
        lines.Add($"**Test result location:** {profile.TestResultLocation}");

        if (!string.IsNullOrEmpty(profile.TestFramework))
            lines.Add($"**Test framework:** {profile.TestFramework}");
        if (!string.IsNullOrEmpty(profile.TestRunnerModel))
            lines.Add($"**Test runner:** {profile.TestRunnerModel}");
        if (!string.IsNullOrEmpty(profile.WorkItemNamingPattern))
            lines.Add($"**Work item naming:** {profile.WorkItemNamingPattern}");

        if (profile.HelixTaskNames.Length > 0)
            lines.Add($"**Helix task name in AzDO timeline:** {string.Join(", ", profile.HelixTaskNames.Select(n => $"'{n}'"))}");

        lines.Add("");
        lines.Add("## Start Here");
        lines.Add("");
        lines.Add(profile.HelixTestResultAvailability switch
        {
            "partial" => "- Structured results: try `helix_parse_uploaded_trx` for the supported Helix-uploaded test legs below, but use `azdo_test_runs + azdo_test_results` for full coverage.",
            "varies" => "- Structured results: pipeline-dependent. Check the pipeline notes and recommended order below before choosing between `helix_parse_uploaded_trx` and `azdo_test_runs + azdo_test_results`.",
            _ when profile.UsesHelix => "- Structured results: skip `helix_parse_uploaded_trx` for this repo and start with `azdo_test_runs + azdo_test_results`.",
            _ => "- Structured results: this repo does not use Helix; start with `azdo_timeline` and `azdo_log`."
        });

        if (profile.UsesHelix && profile.FailureSearchPatterns.Length > 0)
            lines.Add($"- Console search: use `helix_search_log` with `{profile.FailureSearchPatterns[0]}` first, then follow the recommended order below.");
        else if (!profile.UsesHelix)
            lines.Add("- Console/build logs: use AzDO timeline/log tools rather than Helix tools.");

        // Known gotchas — these are CRITICAL
        if (profile.KnownGotchas.Length > 0)
        {
            lines.Add("");
            lines.Add("## ⚠️ Known Gotchas");
            lines.Add("");
            foreach (var g in profile.KnownGotchas)
                lines.Add($"- {g}");
        }

        // Exit code meanings
        if (profile.ExitCodeMeanings.Length > 0)
        {
            lines.Add("");
            lines.Add("## Exit Code Reference");
            lines.Add("");
            foreach (var e in profile.ExitCodeMeanings)
                lines.Add($"- {e}");
        }

        lines.Add("");
        lines.Add("## Recommended Search Patterns");
        lines.Add("");
        foreach (var p in profile.FailureSearchPatterns)
            lines.Add($"- `{p}`");

        // Recommended investigation order
        if (profile.RecommendedInvestigationOrder.Length > 0)
        {
            lines.Add("");
            lines.Add("## Recommended Investigation Order");
            lines.Add("");
            for (int i = 0; i < profile.RecommendedInvestigationOrder.Length; i++)
                lines.Add($"{i + 1}. {profile.RecommendedInvestigationOrder[i]}");
        }

        // Uploaded files
        if (profile.UploadedFiles.Length > 0)
        {
            lines.Add("");
            lines.Add("## Helix File Inventory");
            lines.Add("");
            foreach (var f in profile.UploadedFiles)
                lines.Add($"- {f}");
        }

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

            ## Start Here
            1. **If you know the repo, call `helix_ci_guide(repo)`** — repo profiles tell you whether to skip `helix_parse_uploaded_trx`, which `helix_search_log` pattern to try, and whether devdiv tooling is required
            2. **For structured results, start with `azdo_test_runs` + `azdo_test_results`** — this is the safest default across .NET repos
            3. **Try `helix_parse_uploaded_trx` only if the repo uploads structured results to Helix** — works for repos/pipelines that publish TRX or Helix-hosted result XML
            4. **Use `helix_search_log`** with these patterns to find failures in console output:
               - `'  Failed'` (2 leading spaces) — dotnet test / xUnit summary
               - `'[FAIL]'` — xUnit runner failure marker
               - `'Error Message:'` — test error details
               - `'exit code'` — process crashes

            ## Getting Build Failures
            1. Use `azdo_timeline` to find failed steps (filter='failed')
            2. Use `azdo_log` or `azdo_search_log` to read the failed step's log
            3. Search for `'error MSB'` for MSBuild errors, `'error NU'` for NuGet errors

            ## Known Repos
            Profiles exist for: {string.Join(", ", s_profiles.Values.Select(p => p.DisplayName))}
            Use `helix_ci_guide` with a repo name for detailed guidance.
            """;
    }
}
