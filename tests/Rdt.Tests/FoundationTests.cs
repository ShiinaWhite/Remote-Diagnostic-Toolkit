using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rdt.Auth;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Credentials;
using Rdt.Providers;
using Rdt.Reporting;
using Rdt.Runtime;
using Rdt.Session;
using Rdt.Zcode;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Rdt.Tests;

public sealed class FoundationFixture : IDisposable
{
    public const string FakeKey = "rdt-only-test-key-invalid-for-any-service";
    public const string TestPin = "94175826-test";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "RdtTests-" + Guid.NewGuid().ToString("N"));
    public string Credential => Path.Combine(Root, "credential.rdt");
    public string Reports => Path.Combine(Root, "Reports");
    public CredentialVault Vault { get; } = new();
    public static byte[] Pin() => Encoding.UTF8.GetBytes(TestPin);
    public static byte[] Key() => Encoding.UTF8.GetBytes(FakeKey);
    public static GProviderAdapter Provider() => new(new("https://example.invalid/v1", "fixture-model"));
    public string Executable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "rdt.exe" : "rdt");
    public FoundationFixture()
    {
        PrivateFiles.CreateDirectory(Root);
        var pin = Pin(); var key = Key();
        try { Vault.Create(Credential, pin, key); }
        finally { CryptographicOperations.ZeroMemory(pin); CryptographicOperations.ZeroMemory(key); }
    }
    public void Dispose()
    {
        // This root was created by this fixture; reject links before recursive deletion.
        void Remove(string path)
        {
            Assert.False((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
            if (Directory.Exists(path))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Remove(entry);
                Directory.Delete(path);
            }
            else File.Delete(path);
        }
        if (Directory.Exists(Root)) Remove(Root);
    }
}

public class FoundationTests(FoundationFixture f) : IClassFixture<FoundationFixture>
{
    private DiagnosticRunner Runner(IZcodeRuntime runtime) => new(f.Vault, FoundationFixture.Provider(), runtime);
    private string CopyEnvelope(Func<CredentialEnvelope, CredentialEnvelope> change)
    {
        var path = Path.Combine(f.Root, Guid.NewGuid() + ".rdt");
        var envelope = JsonSerializer.Deserialize<CredentialEnvelope>(File.ReadAllBytes(f.Credential))!;
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(change(envelope)));
        return path;
    }

    [Fact]
    public void CorrectPinUnlocksEncryptedCredential()
    {
        var pin = FoundationFixture.Pin();
        var secret = f.Vault.Unlock(f.Credential, pin);
        try
        {
            Assert.Equal(FoundationFixture.FakeKey, Encoding.UTF8.GetString(secret));
            var persisted = File.ReadAllText(f.Credential);
            Assert.DoesNotContain(FoundationFixture.FakeKey, persisted);
            Assert.DoesNotContain(FoundationFixture.TestPin, persisted);
        }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(pin); }
    }

    [Fact]
    public void WrongPinCannotUnlock() => Assert.Equal("CREDENTIAL_UNLOCK_FAILED",
        Assert.Throws<RdtException>(() => f.Vault.Unlock(f.Credential, Encoding.UTF8.GetBytes("wrong-pin-123"))).Code);

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("nonce")]
    [InlineData("salt")]
    public void TamperingIsAuthenticated(string component)
    {
        string Flip(string value) { var bytes = Convert.FromBase64String(value); bytes[0] ^= 1; return Convert.ToBase64String(bytes); }
        var path = CopyEnvelope(e => component switch
        {
            "ciphertext" => e with { Ciphertext = Flip(e.Ciphertext) },
            "tag" => e with { Tag = Flip(e.Tag) },
            "nonce" => e with { Nonce = Flip(e.Nonce) },
            _ => e with { Salt = Flip(e.Salt) }
        });
        Assert.Equal("CREDENTIAL_UNLOCK_FAILED", Assert.Throws<RdtException>(() => f.Vault.Unlock(path, FoundationFixture.Pin())).Code);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("cost")]
    [InlineData("provider")]
    [InlineData("cipher")]
    [InlineData("length")]
    public void UntrustedEnvelopeParametersAreRejectedBeforeKdf(string component)
    {
        var path = CopyEnvelope(e => component switch
        {
            "version" => e with { Version = 2 }, "cost" => e with { N = int.MaxValue },
            "provider" => e with { Provider = "D" }, "cipher" => e with { Cipher = "none" },
            _ => e with { Nonce = "AQ==" }
        });
        Assert.Throws<RdtException>(() => f.Vault.Unlock(path, FoundationFixture.Pin()));
    }

    [Fact]
    public void CredentialNeverOverwritesExistingFile()
    {
        var before = File.ReadAllBytes(f.Credential);
        Assert.Throws<IOException>(() => f.Vault.Create(f.Credential, FoundationFixture.Pin(), FoundationFixture.Key()));
        Assert.Equal(before, File.ReadAllBytes(f.Credential));
    }

    [Fact]
    public void FreshSaltAndNonceForEachCredential()
    {
        var path = Path.Combine(f.Root, "second.rdt");
        f.Vault.Create(path, FoundationFixture.Pin(), FoundationFixture.Key());
        var first = JsonSerializer.Deserialize<CredentialEnvelope>(File.ReadAllText(f.Credential))!;
        var second = JsonSerializer.Deserialize<CredentialEnvelope>(File.ReadAllText(path))!;
        Assert.NotEqual(first.Salt, second.Salt); Assert.NotEqual(first.Nonce, second.Nonce); Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void ShortPinRejected() => Assert.Throws<RdtException>(() => f.Vault.Create(Path.Combine(f.Root, "short.rdt"), "1234"u8.ToArray(), FoundationFixture.Key()));

    [Fact]
    public void SessionEnvironmentIsIsolatedAndParentIsUnchanged()
    {
        var names = new[] { "HOME", "USERPROFILE", "ZCODE_DATA_BASE_DIR", "ZCODE_PERSONAL_PROVIDER_CONFIG_FILE" };
        var original = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var session = DiagnosticSession.Create(f.Root);
        try
        {
            var env = session.EnvironmentVariables();
            Assert.Equal(session.Home, env["HOME"]); Assert.Equal(session.Home, env["USERPROFILE"]);
            Assert.Equal(session.Data, env["ZCODE_DATA_BASE_DIR"]); Assert.Equal(session.ProviderFile, env["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"]);
            Assert.Equal(session.Runtime, env["TEMP"]);
            Assert.All(new[] { "home", "data", "workspace", "runtime" }, d => Assert.True(Directory.Exists(Path.Combine(session.Root, d))));
            Assert.False(env.ContainsKey("PATH")); Assert.False(env.ContainsKey("NODE_OPTIONS")); Assert.False(env.ContainsKey("ZCODE_API_KEY"));
            foreach (var name in names) Assert.Equal(original[name], Environment.GetEnvironmentVariable(name));
        }
        finally { session.Dispose(); Assert.Equal(CleanupResult.Complete, new SessionCleaner(f.Root).Clean(session.Root)); }
    }

    [Fact]
    public void CleanupProtectsActiveSessionAndIsIdempotent()
    {
        var session = DiagnosticSession.Create(f.Root);
        var cleaner = new SessionCleaner(f.Root);
        Assert.Equal(CleanupResult.Active, cleaner.Clean(session.Root));
        session.Dispose();
        Assert.Equal(CleanupResult.Complete, cleaner.Clean(session.Root));
        Assert.Equal(CleanupResult.AlreadyAbsent, cleaner.Clean(session.Root));
    }

    [Fact]
    public void CleanupRefusesUnownedAndOutOfScopeDirectories()
    {
        var unowned = Path.Combine(f.Root, "RDT-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unowned); File.WriteAllText(Path.Combine(unowned, "customer.txt"), "keep");
        var cleaner = new SessionCleaner(f.Root);
        Assert.Equal(CleanupResult.Refused, cleaner.Clean(unowned));
        Assert.Equal(CleanupResult.Refused, cleaner.Clean(f.Root));
        Assert.Equal(CleanupResult.Refused, cleaner.Clean(f.Reports));
        Assert.True(File.Exists(Path.Combine(unowned, "customer.txt")));
    }

    [Fact]
    public void CleanupRefusesMismatchedMarker()
    {
        var s = DiagnosticSession.Create(f.Root); s.Dispose();
        var marker = Path.Combine(s.Root, "owner.json"); var before = File.ReadAllBytes(marker);
        File.WriteAllBytes(marker, JsonSerializer.SerializeToUtf8Bytes(new SessionMarker(1, Guid.NewGuid(), DateTimeOffset.UtcNow)));
        Assert.Equal(CleanupResult.Refused, new SessionCleaner(f.Root).Clean(s.Root));
        File.WriteAllBytes(marker, before);
        Assert.Equal(CleanupResult.Complete, new SessionCleaner(f.Root).Clean(s.Root));
    }

    [Fact]
    public void StaleScanRequiresAgeAndInactiveLease()
    {
        var s = DiagnosticSession.Create(f.Root);
        var marker = Path.Combine(s.Root, "owner.json");
        File.WriteAllBytes(marker, JsonSerializer.SerializeToUtf8Bytes(new SessionMarker(1, s.Id, DateTimeOffset.UtcNow.AddDays(-2))));
        var cleaner = new SessionCleaner(f.Root);
        Assert.DoesNotContain(s.Root, cleaner.FindStale(DateTimeOffset.UtcNow));
        s.Dispose(); Assert.Contains(s.Root, cleaner.FindStale(DateTimeOffset.UtcNow));
        Assert.Equal(CleanupResult.Complete, cleaner.Clean(s.Root));
    }

    [Fact]
    public void ReportsCannotBeNestedInsideSession()
    {
        using var s = DiagnosticSession.Create(f.Root);
        Assert.Throws<RdtException>(() => Report.Create(Path.Combine(s.Root, "Reports"), f.Root));
    }

    [Fact]
    public void GConfigUsesVersionedPersonalProviderAndIsDeleted()
    {
        var s = DiagnosticSession.Create(f.Root);
        s.WriteRuntimeConfig(); FoundationFixture.Provider().WriteTemporaryConfig(s, FoundationFixture.Key());
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(s.ProviderFile)))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
            var config = doc.RootElement.GetProperty("config");
            var p = config.GetProperty("providerConfigRules").GetProperty("providerRules")[0];
            Assert.Equal("G", p.GetProperty("providerName").GetString());
            Assert.Equal("rdt-g", config.GetProperty("defaultModelSelection").GetProperty("providerId").GetString());
            Assert.Equal("openai-chat-completions", p.GetProperty("config").GetProperty("api").GetProperty("type").GetString());
            Assert.Equal(FoundationFixture.FakeKey, p.GetProperty("config").GetProperty("access").GetProperty("apiKey").GetString());
        }
        s.Dispose(); Assert.Equal(CleanupResult.Complete, new SessionCleaner(f.Root).Clean(s.Root)); Assert.False(File.Exists(s.ProviderFile));
    }

    [Theory]
    [InlineData("http://example.invalid/v1")]
    [InlineData("https://user:secret@example.invalid")]
    [InlineData("https://example.invalid?key=secret")]
    public void ProviderRejectsUnsafeEndpoint(string url) => Assert.Throws<RdtException>(() => new GProviderOptions(url, "m").Validate());

    [Fact]
    public void DIsExplicitlyUnavailable()
    {
        using var s = DiagnosticSession.Create(f.Root);
        Assert.Throws<RdtException>(() => new DeferredDProvider().WriteTemporaryConfig(s, FoundationFixture.Key()));
    }

    [Fact]
    public void LaunchContractNeverUsesShellYoloOrSecretArguments()
    {
        using var s = DiagnosticSession.Create(f.Root);
        var text = "diagnose ; $(whoami) & do-not-run";
        var start = ReadOnlyPolicy.CreateStartInfo(f.Executable, s, text);
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        Assert.Contains(text, start.ArgumentList); Assert.Contains("plan", start.ArgumentList); Assert.DoesNotContain("yolo", start.ArgumentList);
        Assert.Contains("Bash", ReadOnlyPolicy.DisallowedTools.Split(' ')); Assert.Contains("ExitPlanMode", ReadOnlyPolicy.DisallowedTools.Split(' '));
        var material = string.Join(" ", start.ArgumentList) + string.Join(" ", start.Environment.Values);
        Assert.DoesNotContain(FoundationFixture.FakeKey, material); Assert.DoesNotContain(FoundationFixture.TestPin, material);
    }

    [Fact]
    public async Task EndToEndMockProcessKeepsReportsAndRemovesTemporaryProfile()
    {
        var actualHome = Environment.GetEnvironmentVariable("USERPROFILE");
        var profile = actualHome == null ? null : Path.Combine(actualHome, ".zcode");
        var before = profile != null && Directory.Exists(profile) ? Directory.GetLastWriteTimeUtc(profile) : (DateTime?)null;
        var pin = FoundationFixture.Pin();
        var result = await Runner(new MockProcessRuntime(f.Executable)).RunAsync(f.Credential, pin, f.Reports, f.Root);
        Assert.True(result.Success, result.Error.ToString()); Assert.Equal(0, result.RuntimeExitCode);
        Assert.True(pin.All(b => b == 0)); Assert.False(Directory.Exists(Path.Combine(f.Root, "RDT-" + result.SessionId!.Value.ToString("N"))));
        var summary = File.ReadAllText(Path.Combine(result.ReportPath, "summary.md"));
        var log = File.ReadAllText(Path.Combine(result.ReportPath, "session.log"));
        Assert.Contains("MOCK", summary); Assert.Contains("READ ONLY", summary);
        Assert.Contains("RuntimeStarted", log); Assert.Contains("CleanupCompleted", log); Assert.Contains("Exited", log);
        Assert.DoesNotContain(FoundationFixture.FakeKey, log + summary); Assert.DoesNotContain(FoundationFixture.TestPin, log + summary);
        Assert.Equal(actualHome, Environment.GetEnvironmentVariable("USERPROFILE"));
        Assert.Equal(before, profile != null && Directory.Exists(profile) ? Directory.GetLastWriteTimeUtc(profile) : (DateTime?)null);
    }

    [Fact]
    public async Task ChildCrashStillCleansAndRecordsExit()
    {
        var r = await Runner(new MockProcessRuntime(f.Executable, MockBehavior.Crash)).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root);
        Assert.False(r.Success); Assert.True(r.RuntimeStarted); Assert.Equal(27, r.RuntimeExitCode); Assert.True(r.CleanupComplete);
        Assert.Equal(FailureCode.RuntimeFailure, r.Error);
    }

    [Fact]
    public async Task CancellationKillsChildBeforeCleanup()
    {
        using var cancel = new CancellationTokenSource();
        var runtime = new StartCancellingRuntime(new MockProcessRuntime(f.Executable, MockBehavior.Hang), cancel);
        var r = await Runner(runtime).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root, cancel.Token);
        Assert.True(r.RuntimeStarted); Assert.Equal(FailureCode.Cancelled, r.Error); Assert.True(r.CleanupComplete);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "RDT-" + r.SessionId!.Value.ToString("N"))));
    }

    [Fact]
    public async Task ArbitraryRuntimeExceptionCannotLeakSecrets()
    {
        var r = await Runner(new ThrowingRuntime()).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root);
        Assert.True(r.CleanupComplete); Assert.Equal(FailureCode.OperationFailed, r.Error);
        var report = string.Concat(Directory.GetFiles(r.ReportPath).Select(File.ReadAllText));
        Assert.DoesNotContain(FoundationFixture.FakeKey, report); Assert.DoesNotContain(FoundationFixture.TestPin, report);
    }

    [Fact]
    public async Task UnlockFailureDoesNotCreateSessionOrLeakPin()
    {
        var wrongPin = Encoding.UTF8.GetBytes("wrong-but-not-logged");
        var r = await Runner(new ThrowingRuntime()).RunAsync(f.Credential, wrongPin, f.Reports, f.Root);
        Assert.Null(r.SessionId); Assert.False(r.RuntimeStarted); Assert.Equal(FailureCode.CredentialRejected, r.Error);
        Assert.True(wrongPin.All(b => b == 0)); Assert.DoesNotContain("wrong-but-not-logged", File.ReadAllText(Path.Combine(r.ReportPath, "session.log")));
    }

    [Fact]
    public async Task RealRuntimeIsFailClosedAndStillCleans()
    {
        var r = await Runner(new ZcodeRuntime()).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root);
        Assert.Equal(FailureCode.RuntimeUnvalidated, r.Error); Assert.False(r.RuntimeStarted); Assert.True(r.CleanupComplete);
    }

    [Fact]
    public async Task CliRequiresPinUnlocksAndShowsOnlyProviderAlias()
    {
        var input = new FixtureInput(); var output = new StringWriter();
        var exit = await Rdt.Cli.Program.RunAsync(["run", "--mock", f.Credential], input, output);
        Assert.Equal(0, exit); Assert.Equal(["Operator PIN: "], input.Labels);
        var ui = output.ToString(); Assert.Contains("Credential unlocked.", ui); Assert.Contains("Provider: G", ui); Assert.Contains("READ ONLY", ui);
        Assert.DoesNotContain("GLM", ui); Assert.DoesNotContain("DeepSeek", ui); Assert.DoesNotContain("ZCode", ui);
        Assert.DoesNotContain(FoundationFixture.FakeKey, ui); Assert.DoesNotContain(FoundationFixture.TestPin, ui);
    }

    [Fact]
    public void RedirectedPinInputIsRejected()
    {
        Assert.True(Console.IsInputRedirected);
        Assert.Equal("INTERACTIVE_TERMINAL_REQUIRED", Assert.Throws<RdtException>(() => new ConsoleSecretInput().Read("PIN")).Code);
    }

    [Fact]
    public async Task ProcessStartFailureStillCleansAndDoesNotClaimStarted()
    {
        var r = await Runner(new MockProcessRuntime(Path.Combine(f.Root, "missing.exe")))
            .RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root);
        Assert.False(r.RuntimeStarted); Assert.True(r.CleanupComplete); Assert.Equal(FailureCode.OperationFailed, r.Error);
    }

    [Fact]
    public async Task ReportSetupFailureAlsoZerosCallerPin()
    {
        var pin = FoundationFixture.Pin();
        using var s = DiagnosticSession.Create(f.Root);
        await Assert.ThrowsAsync<RdtException>(() => Runner(new ThrowingRuntime())
            .RunAsync(f.Credential, pin, Path.Combine(s.Root, "Reports"), f.Root));
        Assert.True(pin.All(b => b == 0));
    }

    [Fact]
    public async Task CancelledBeforeUnlockNeverCreatesSession()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var r = await Runner(new ThrowingRuntime()).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root, cancel.Token);
        Assert.Null(r.SessionId); Assert.False(r.RuntimeStarted); Assert.Equal(FailureCode.Cancelled, r.Error);
    }

    [Fact]
    public void OversizedCredentialIsRejected()
    {
        var path = Path.Combine(f.Root, "oversized.rdt");
        File.WriteAllBytes(path, new byte[65537]);
        Assert.Equal("CREDENTIAL_UNLOCK_FAILED", Assert.Throws<RdtException>(() => f.Vault.Unlock(path, FoundationFixture.Pin())).Code);
    }

    [Fact]
    public void WindowsFilesAndSessionsHaveProtectedUserOnlyAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var s = DiagnosticSession.Create(f.Root);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var fileAcl = new FileInfo(f.Credential).GetAccessControl();
        var dirAcl = new DirectoryInfo(s.Root).GetAccessControl();
        Assert.True(fileAcl.AreAccessRulesProtected); Assert.True(dirAcl.AreAccessRulesProtected);
        foreach (var acl in new FileSystemSecurity[] { fileAcl, dirAcl })
        {
            var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            Assert.Single(rules.Cast<FileSystemAccessRule>());
            foreach (FileSystemAccessRule rule in rules) Assert.Equal(sid, rule.IdentityReference);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupRefusesJunctionAndPreservesOutsideEvidence(bool missingLease)
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = DiagnosticSession.Create(f.Root);
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
        var evidence = Path.Combine(outside, "evidence.txt"); File.WriteAllText(evidence, "customer evidence");
        var junction = Path.Combine(s.Runtime, "escape");
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        // Fixture-generated paths, escaped as PowerShell single-quoted literals. No system policy change.
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("$null = New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' -ErrorAction Stop");
        try
        {
            using var process = Process.Start(psi)!;
            Assert.True(process.WaitForExit(10000)); Assert.Equal(0, process.ExitCode);
            s.Dispose();
            if (missingLease)
            {
                File.Delete(Path.Combine(s.Root, ".lease"));
                File.WriteAllBytes(Path.Combine(s.Root, "owner.json"), JsonSerializer.SerializeToUtf8Bytes(
                    new SessionMarker(1, s.Id, DateTimeOffset.UtcNow.AddDays(-2))));
            }
            Assert.Equal(CleanupResult.Refused, new SessionCleaner(f.Root).Clean(s.Root));
            Assert.DoesNotContain(s.Root, new SessionCleaner(f.Root).FindStale(DateTimeOffset.UtcNow));
            Assert.Equal("customer evidence", File.ReadAllText(evidence));
            Assert.Throws<RdtException>(() => PrivateFiles.WriteNew(Path.Combine(junction, "forbidden.txt"), "no"u8));
            Assert.False(File.Exists(Path.Combine(outside, "forbidden.txt")));
        }
        finally
        {
            s.Dispose();
            // Delete only the junction itself. Never recursively delete through it.
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Assert.Equal(CleanupResult.Complete, new SessionCleaner(f.Root).Clean(s.Root));
        }
    }

    [Fact]
    public async Task ReportExportFailureCannotLeaveTemporaryProfile()
    {
        string? sessionRoot = null;
        var runtime = new CallbackRuntime(s =>
        {
            sessionRoot = s.Root;
            var report = Directory.GetDirectories(f.Reports).OrderBy(Directory.GetCreationTimeUtc).Last();
            Directory.CreateDirectory(Path.Combine(report, "summary.md"));
        });
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            try { await Runner(runtime).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root); }
            catch (UnauthorizedAccessException e) { throw new IOException("Expected export failure", e); }
        });
        Assert.NotNull(sessionRoot); Assert.False(Directory.Exists(sessionRoot));
    }

    [Fact]
    public async Task CleanupFailureIsReportedAndCanBeRetried()
    {
        FileStream? held = null;
        string? sessionRoot = null;
        try
        {
            var runtime = new CallbackRuntime(s =>
            {
                sessionRoot = s.Root;
                held = new FileStream(Path.Combine(s.Runtime, "held"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            });
            var r = await Runner(runtime).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root);
            Assert.False(r.CleanupComplete); Assert.False(r.Success); Assert.Equal(FailureCode.CleanupFailed, r.Error);
            Assert.Contains("CleanupIncomplete", File.ReadAllText(Path.Combine(r.ReportPath, "session.log")));
            Assert.True(File.Exists(Path.Combine(r.ReportPath, "summary.md")));
        }
        finally
        {
            held?.Dispose();
            if (sessionRoot != null) Assert.Equal(CleanupResult.Complete, new SessionCleaner(f.Root).Clean(sessionRoot));
        }
    }

    private sealed class CallbackRuntime(Action<DiagnosticSession> callback) : IZcodeRuntime
    {
        public bool IsMock => true;
        public Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken)
        {
            started(); callback(session); return Task.FromResult(new RuntimeResult(0, false));
        }
    }

    private sealed class FixtureInput : ISecretInput
    {
        public List<string> Labels { get; } = [];
        public byte[] Read(string label, CancellationToken cancellationToken = default) { Labels.Add(label); return FoundationFixture.Pin(); }
    }

    [Fact]
    public async Task CliPropagatesCancellationWhileWaitingForPin()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var input = new CancelledInput();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Rdt.Cli.Program.RunAsync(
            ["run", "--mock", f.Credential], input, new StringWriter(), cancel.Token));
        Assert.True(input.CancellationObserved);
    }

    private sealed class CancelledInput : ISecretInput
    {
        public bool CancellationObserved { get; private set; }
        public byte[] Read(string label, CancellationToken cancellationToken = default)
        {
            CancellationObserved = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected cancellation");
        }
    }
    private sealed class ThrowingRuntime : IZcodeRuntime
    {
        public bool IsMock => true;
        public Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken)
            => throw new InvalidOperationException(FoundationFixture.FakeKey + FoundationFixture.TestPin);
    }
    private sealed class StartCancellingRuntime(IZcodeRuntime inner, CancellationTokenSource cancel) : IZcodeRuntime
    {
        public bool IsMock => true;
        public Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken)
            => inner.RunAsync(session, () => { started(); cancel.CancelAfter(300); }, cancellationToken);
    }
}
