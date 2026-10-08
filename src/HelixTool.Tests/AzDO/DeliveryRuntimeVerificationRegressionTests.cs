using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using HelixTool.Core.Cache;
using HelixTool.Core.Delivery;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class DeliveryRuntimeVerificationRegressionTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-runtime", $"runtime-verification-{Guid.NewGuid():N}"));
    private readonly Dictionary<string, string?> _environment = new();
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void RuntimeOwnershipVerification_DoesNotExecuteStatFromPath()
    {
        if (OperatingSystem.IsWindows())
            return;
        var bin = Path.Combine(_root, "bin");
        var runtime = Path.Combine(_root, "runtime");
        CreatePrivate(bin);
        CreatePrivate(runtime);
        var marker = Path.Combine(_root, "stat-used");
        var stat = Path.Combine(bin, "stat");
        File.WriteAllText(stat, $"#!/bin/sh\nprintf 'used\\n' >> {Quote(marker)}\nprintf '%s\\n' '{GetEffectiveUserId()}'\n");
        File.SetUnixFileMode(stat, OwnerDirectory);
        SetEnvironment("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, runtime);
        var store = CreateEvalStore();
        var receipt = store.PutText("trusted verification", "text/plain", null, null, "public");
        Assert.True(receipt.Complete);
        Assert.Equal("trusted verification", store.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text);
        Assert.False(File.Exists(marker), "A PATH-supplied executable must never participate in security verification.");
    }

    [Fact]
    public async Task ConcurrentFirstUse_AtomicDirectoryCreationRaceStillReturnsReadableReceipts()
    {
        CreatePrivate(_root);
        var runtime = Path.Combine(_root, "runtime", "nested", "private");
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, runtime);
        using var start = new SemaphoreSlim(0);
        var puts = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            await start.WaitAsync();
            var store = CreateEvalStore();
            var content = $"concurrent initialization {i}";
            var receipt = store.PutText(content, "text/plain", null, null, "public");
            return (store, content, receipt);
        })).ToArray();
        start.Release(puts.Length);
        foreach (var (store, content, receipt) in await Task.WhenAll(puts))
        {
            Assert.True(receipt.Complete);
            Assert.Equal(content, store.Read(receipt.EvidenceId, 0, 1000, "utf8", "public").Text);
        }
    }

    [Theory]
    [InlineData("HLX_DELIVERY_RUNTIME_DIR", false)]
    [InlineData("HLX_DELIVERY_RUNTIME_DIR", true)]
    [InlineData("XDG_RUNTIME_DIR", false)]
    [InlineData("XDG_RUNTIME_DIR", true)]
    public void ActualRuntimeResolution_RejectsSymlinkedEnvironmentBaseOrAncestor(string variable, bool nested)
    {
        if (OperatingSystem.IsWindows())
            return;
        var target = Path.Combine(_root, "target");
        CreatePrivate(target);
        if (nested)
            CreatePrivate(Path.Combine(target, "nested"));
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, null);
        SetEnvironment("XDG_RUNTIME_DIR", null);
        SetEnvironment(variable, nested ? Path.Combine(link, "nested") : link);
        var before = Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Order().ToArray();
        var error = Assert.Throws<InvalidOperationException>(() =>
            CreateEvalStore().PutText("must not follow environment aliases", "text/plain", null, null, "public"));
        Assert.Contains("link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)]
    public void ActualRuntimeResolution_RejectsReplaceableXdgBase(UnixFileMode grant)
    {
        if (OperatingSystem.IsWindows())
            return;
        var runtime = Path.Combine(_root, "unsafe-xdg");
        CreatePrivate(runtime);
        File.SetUnixFileMode(runtime, OwnerDirectory | grant);
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, null);
        SetEnvironment("XDG_RUNTIME_DIR", runtime);
        Assert.Throws<InvalidOperationException>(() =>
            CreateEvalStore().PutText("unsafe base", "text/plain", null, null, "public"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(runtime));
    }

    [Theory]
    [InlineData("HLX_DELIVERY_RUNTIME_DIR", UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)]
    [InlineData("HLX_DELIVERY_RUNTIME_DIR", UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)]
    [InlineData("XDG_RUNTIME_DIR", UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)]
    [InlineData("XDG_RUNTIME_DIR", UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)]
    public void ActualRuntimeResolution_RejectsReplaceableAncestorOfOtherwisePrivateBase(string variable, UnixFileMode grant)
    {
        if (OperatingSystem.IsWindows())
            return;
        var parent = Path.Combine(_root, "replaceable-parent");
        var runtime = Path.Combine(parent, "runtime");
        CreatePrivate(runtime);
        File.SetUnixFileMode(parent, OwnerDirectory | grant);
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, null);
        SetEnvironment("XDG_RUNTIME_DIR", null);
        SetEnvironment(variable, runtime);
        Assert.Throws<InvalidOperationException>(() =>
            CreateEvalStore().PutText("replaceable ancestor", "text/plain", null, null, "public"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(runtime));
    }

    [Fact]
    public void InitializedRuntime_RejectsAncestorSubstitutionBeforeLaterWrite()
    {
        if (OperatingSystem.IsWindows())
            return;
        var parent = Path.Combine(_root, "parent");
        var runtime = Path.Combine(parent, "runtime");
        CreatePrivate(runtime);
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, runtime);
        var store = CreateEvalStore();
        Assert.True(store.PutText("before replacement", "text/plain", null, null, "public").Complete);
        var outside = Path.Combine(_root, "outside");
        CreatePrivate(Path.Combine(outside, "runtime"));
        Directory.Move(parent, Path.Combine(_root, "original-parent"));
        Directory.CreateSymbolicLink(parent, outside);
        Assert.Throws<InvalidOperationException>(() =>
            store.PutText("after replacement", "text/plain", null, null, "public"));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(outside, "*", SearchOption.AllDirectories),
            path => File.Exists(path));
    }

    [Theory]
    [InlineData("override")]
    [InlineData("xdg")]
    [InlineData("temp-fallback")]
    public void WarmStoreOperations_DoNotRepeatSubprocessChecks_AndKeepNativeAclGuardActive(string source)
    {
        if (OperatingSystem.IsWindows())
            return;
        var runtime = Path.Combine(_root, "runtime");
        CreatePrivate(runtime);
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, source == "override" ? runtime : null);
        SetEnvironment("XDG_RUNTIME_DIR", source == "xdg" ? runtime : null);
        if (source == "temp-fallback")
            SetEnvironment("TMPDIR", runtime);
        var store = CreateEvalStore();
        var receipt = store.PutText("initial receipt", "text/plain", null, null, "public");
        Assert.StartsWith(runtime, receipt.LocalPath);
        var initial = CheckCounts();
        Assert.True(initial.Owner > 0);
        var nativeBefore = OperatingSystem.IsMacOS() ? Counter("NativeAclCheckCount") : 0;
        if (OperatingSystem.IsMacOS())
            Assert.True(nativeBefore > 0);
        for (var i = 0; i < 32; i++)
        {
            Assert.True(store.PutText($"receipt-{i}", "text/plain", null, null, "public").Complete);
            Assert.Equal("initial receipt", store.Read(receipt.EvidenceId, 0, 100, "utf8", "public").Text);
            Assert.NotNull(store.TryGetDescriptor(receipt.EvidenceId, "public"));
            Assert.NotNull(store.TryGetRetentionStatus("public"));
        }
        Assert.Equal(initial, CheckCounts());
        if (OperatingSystem.IsMacOS())
            Assert.True(Counter("NativeAclCheckCount") >= nativeBefore + 128,
                "Each hot operation must keep the cheap synchronous ACL guard; subprocess caching cannot waive use-time safety.");
    }

    [Fact]
    public void InitializedMacRuntime_RejectsAclGrantAddedWithoutChangingModeOrBirthTime()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var runtime = Path.Combine(_root, "runtime");
        CreatePrivate(runtime);
        SetEnvironment(FileEvidenceDeliveryStore.RuntimeDirOverrideEnvironmentVariable, runtime);
        var store = CreateEvalStore();
        Assert.True(store.PutText("before ACL change", "text/plain", null, null, "public").Complete);
        var birthTime = File.GetCreationTimeUtc(runtime);
        using var chmod = Process.Start(new ProcessStartInfo("/bin/chmod")
        {
            ArgumentList = { "+a", "everyone allow read,search", runtime },
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        chmod.WaitForExit();
        Assert.Equal(0, chmod.ExitCode);
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(runtime));
        Assert.Equal(birthTime, File.GetCreationTimeUtc(runtime));
        Assert.Throws<InvalidOperationException>(() =>
            store.PutText("must revalidate changed ACL", "text/plain", null, null, "public"));
    }

    [Fact]
    public void TrustedRunner_TimeoutCoversOutputRead_KillsAndReapsChild()
    {
        if (OperatingSystem.IsWindows())
            return;
        var pidFile = Path.Combine(_root, "timed-out.pid");
        Directory.CreateDirectory(_root);
        var script = $"printf '%s' \"$$\" > {Quote(pidFile)}\nexec /bin/sleep 30\n";
        var watch = Stopwatch.StartNew();
        try
        {
            Assert.Throws<TimeoutException>(() => RunTrusted("/bin/sh", ["-c", script], TimeSpan.FromSeconds(1)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "The deadline must cover blocked stdout reads, not only WaitForExit.");
            Assert.False(IsRunning(ReadPid(pidFile)), "The deadline must kill and reap the owned child before returning.");
        }
        finally
        {
            KillOwnedChildIfRunning(pidFile);
        }
    }

    [Fact]
    public void TrustedRunner_DrainsBothStreamsWithoutPipeDeadlock()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "both-streams.sh");
        var stderr = new string('e', 8192);
        var stdout = new string('o', 8192);
        File.WriteAllText(path, string.Concat(Enumerable.Repeat($"printf '%s' '{stderr}' >&2\nprintf '%s' '{stdout}'\n", 40)));
        var result = RunTrusted("/bin/sh", [path], TimeSpan.FromSeconds(5));
        Assert.Equal(0, ExitCode(result));
        Assert.Equal(new string('o', 8192 * 40), Stdout(result));
    }

    [Fact]
    public void TrustedRunner_OutputDrainIsInsideDeadlineEvenWhenParentAlreadyExited()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_root);
        var pidFile = Path.Combine(_root, "inherited-pipe.pid");
        var script = $"/bin/sleep 30 &\nprintf '%s' \"$!\" > {Quote(pidFile)}\nexit 0\n";
        var watch = Stopwatch.StartNew();
        try
        {
            Assert.Throws<TimeoutException>(() => RunTrusted("/bin/sh", ["-c", script], TimeSpan.FromSeconds(1)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "Output-drain deadlines cannot restart after the parent process exits.");
        }
        finally
        {
            KillOwnedChildIfRunning(pidFile);
        }
    }

    [Fact]
    public void TrustedRunner_FloodedOutputIsBoundedAndChildIsReaped()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "large-output");
        File.WriteAllText(output, new string('x', 2 * 1024 * 1024));
        var pidFile = Path.Combine(_root, "flood.pid");
        var script = $"printf '%s' \"$$\" > {Quote(pidFile)}\nexec /bin/cat {Quote(output)}\n";
        try
        {
            Assert.Throws<InvalidOperationException>(() => RunTrusted("/bin/sh", ["-c", script], TimeSpan.FromSeconds(5)));
            Assert.False(IsRunning(ReadPid(pidFile)));
        }
        finally
        {
            KillOwnedChildIfRunning(pidFile);
        }
    }

    [Fact]
    public void TrustedRunner_NonzeroAndMissingExecutableDoNotAppearSuccessful()
    {
        if (OperatingSystem.IsWindows())
            return;
        var result = RunTrusted("/bin/sh", ["-c", "printf 'diagnostic' >&2; exit 7"], TimeSpan.FromSeconds(2));
        Assert.Equal(7, ExitCode(result));
        Assert.NotNull(Record.Exception(() =>
            RunTrusted(Path.Combine(_root, "missing-executable"), [], TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void MacAclInspectionFailure_IsNeverTreatedAsNoAcl()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var method = UnixSecurityType().GetMethod("HasExtraAcl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        bool? unsafeOrUnverifiable = null;
        var error = Record.Exception(() => unsafeOrUnverifiable =
            (bool)Invoke(method, null, [Path.Combine(_root, "missing-directory")])!);
        Assert.True(error is not null || unsafeOrUnverifiable is true,
            "A nonzero/missing ACL inspection must fail closed on macOS, whose mode bits cannot prove privacy.");
    }

    [Theory]
    [InlineData("/var", "/private/var")]
    [InlineData("/tmp", "/private/tmp")]
    public void MacSystemAliases_HaveExactKnownPhysicalTargetsWithoutFilesystemIo(string alias, string expected)
    {
        var method = KnownAliasMethod();
        object?[] args = [alias, null];
        var recognized = (bool)Invoke(method, null, args)!;
        Assert.Equal(OperatingSystem.IsMacOS(), recognized);
        if (OperatingSystem.IsMacOS())
            Assert.Equal(expected, args[1]);
        else
            Assert.True(string.IsNullOrEmpty(args[1] as string));
    }

    [Theory]
    [InlineData("/var-untrusted")]
    [InlineData("/tmp-sibling")]
    [InlineData("/arbitrary-alias")]
    public void KnownSystemAliasTable_DoesNotExemptArbitraryEnvironmentAncestors(string path)
    {
        object?[] args = [path, null];
        Assert.False((bool)Invoke(KnownAliasMethod(), null, args)!);
    }

    private static MethodInfo KnownAliasMethod()
    {
        var method = typeof(FileEvidenceDeliveryStore).GetMethod("TryGetKnownSystemAlias",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method;
    }

    private static (long Owner, long Acl) CheckCounts()
    {
        return (Counter("OwnershipCheckCount"), Counter("AclCheckCount"));
    }

    private static long Counter(string name)
    {
        var field = UnixSecurityType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Convert.ToInt64(field.GetValue(null));
    }

    private static Type UnixSecurityType() =>
        typeof(FileEvidenceDeliveryStore).Assembly.GetType("HelixTool.Core.Delivery.UnixDirectorySecurity", throwOnError: true)!;

    private static object RunTrusted(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var method = UnixSecurityType().GetMethod("RunTrusted", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Invoke(method, null, [executable, arguments, timeout])!;
    }

    private static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static int ExitCode(object result) => (int)result.GetType().GetProperty("ExitCode")!.GetValue(result)!;
    private static string Stdout(object result) => (string)result.GetType().GetProperty("Stdout")!.GetValue(result)!;
    private static int ReadPid(string path) => int.Parse(File.ReadAllText(path));

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void KillOwnedChildIfRunning(string pidFile)
    {
        if (!File.Exists(pidFile))
            return;
        var pid = ReadPid(pidFile);
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException) { }
    }

    private FileEvidenceDeliveryStore CreateEvalStore() =>
        new(new CacheOptions { CacheRoot = Path.Combine(_root, "snapshot"), EvalMode = true });

    private static void CreatePrivate(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, OwnerDirectory);
    }

    private void SetEnvironment(string name, string? value)
    {
        _environment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    public void Dispose()
    {
        foreach (var (name, value) in _environment)
            Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
