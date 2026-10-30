using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Providers;
using Rdt.Reporting;
using Rdt.Runtime;
using Rdt.Runtime.Windows;
using Rdt.Session;
using Rdt.Zcode;
using Xunit;

namespace Rdt.Tests;

public class ProductionTests(FoundationFixture f) : IClassFixture<FoundationFixture>
{
    [Fact]
    public void PackagedIdentityAndNonsecretProviderConfigurationMatchRuntimeContract()
    {
        using var identity = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "config", "runtime-identity.json")));
        Assert.Equal(RuntimeIdentity.Sha256, identity.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(RuntimeIdentity.BuiltinSha256, identity.RootElement.GetProperty("builtinSha256").GetString());
        Assert.Equal(RdtInfo.UpstreamCommit, identity.RootElement.GetProperty("upstreamCommit").GetString());
        var provider = GProviderOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "provider-g.json"));
        Assert.Equal("openai-chat-completions", provider.ApiSchema);
        Assert.Equal("bigmodel-coding-plan", provider.EndpointProfile);
        Assert.Equal("https://open.bigmodel.cn/api/coding/paas/v4", provider.BaseUrl);
    }
    [Theory]
    [InlineData(false, "RUNTIME_MISSING")]
    [InlineData(true, "RUNTIME_HASH_MISMATCH")]
    public void RuntimeImageFailsClosed(bool exists, string code)
    {
        var path = Path.Combine(f.Root, Guid.NewGuid() + ".exe");
        if (exists) File.WriteAllText(path, "not-an-executable");
        Assert.Equal(code, Assert.Throws<RdtException>(() => RuntimeIdentity.OpenVerified(path,
            RuntimeIdentity.Sha256, "RUNTIME_MISSING", "RUNTIME_HASH_MISMATCH")).Code);
    }

    [Fact]
    public void BuiltinTamperIsRejected()
    {
        var path = Path.Combine(f.Root, Guid.NewGuid() + ".json"); File.WriteAllText(path, "{}");
        Assert.Equal("BUILTIN_HASH_MISMATCH", Assert.Throws<RdtException>(() => RuntimeIdentity.OpenVerified(path,
            RuntimeIdentity.BuiltinSha256, "BUILTIN_MISSING", "BUILTIN_HASH_MISMATCH")).Code);
    }

    [Fact]
    public void ProductionLaunchUsesOnlyStdioAndPrivateEnvironment()
    {
        using var session = DiagnosticSession.Create(f.Root);
        var start = ZcodeRuntime.CreateStartInfo(session);
        Assert.Equal(["agent-server", "--stdio", "--cwd", session.Workspace], start.ArgumentList);
        Assert.Equal(RuntimeIdentity.Executable, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal([], start.StandardInputEncoding!.GetPreamble());
        Assert.Throws<DecoderFallbackException>(() => start.StandardOutputEncoding!.GetString([0xff]));
        Assert.Throws<DecoderFallbackException>(() => start.StandardErrorEncoding!.GetString([0xff]));
        Assert.Equal(session.BuiltinFile, start.Environment["ZCODE_BUILTIN_PROVIDER_CONFIG_FILE"]);
        foreach (var (name, value) in start.Environment)
        {
            Assert.DoesNotContain(FoundationFixture.FakeKey, value ?? "");
            if (name is "HOME" or "USERPROFILE" or "APPDATA" or "LOCALAPPDATA" or "TEMP" or "TMP" or "TMPDIR" ||
                name.StartsWith("XDG_", StringComparison.Ordinal) || name.StartsWith("ZCODE_", StringComparison.Ordinal) && name != "ZCODE_MESSAGE_ENABLED")
                Assert.StartsWith(session.Root, value);
        }
        Assert.DoesNotContain("NODE_OPTIONS", start.Environment.Keys);
        Assert.DoesNotContain("API_KEY", start.Environment.Keys);
    }

    [Fact]
    public async Task MultilineTaskPreservesUnicodeAndLiteralText()
    {
        var text = "山西晋铁源\n$(whoami) ; --prompt\n";
        var result = await Rdt.Cli.Program.ReadTaskAsync(new StringReader(text + ".run\nignored"), new StringWriter(), default);
        Assert.Equal(text.Replace("\n", Environment.NewLine), result);
    }

    [Fact]
    public void RedactionCoversLiteralBearerBase64AndTerminalEscape()
    {
        using var redactor = new SecretRedactor(FoundationFixture.Key());
        var value = redactor.Redact(FoundationFixture.FakeKey + " " + Convert.ToBase64String(FoundationFixture.Key()) + " Bearer fixture-token\u001b");
        Assert.DoesNotContain(FoundationFixture.FakeKey, value);
        Assert.DoesNotContain("fixture-token", value); Assert.DoesNotContain('\u001b', value);
    }

    private RuntimeServices Services(string scenario, string? jobFault = null, TimeSpan? timeout = null) => new() {
        VerifyImage = () => File.OpenRead(f.Executable),
        CreateJob = () => new RuntimeJob(jobFault),
        StartInfo = session => {
            var start = ZcodeRuntime.CreateStartInfo(session);
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            start.ArgumentList.Clear();
            // Encoded fixture code avoids changing the host's script execution policy.
            var fixture = "& {\n" + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "protocol-runtime.ps1")) + "\n} '" + scenario + "'";
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(fixture)) }) start.ArgumentList.Add(arg);
            return start;
        },
        SessionTimeout = timeout ?? TimeSpan.FromSeconds(15), StopTimeout = TimeSpan.FromMilliseconds(500)
    };

    [Theory]
    [InlineData("create")]
    [InlineData("config")]
    [InlineData("assign")]
    [InlineData("membership")]
    public async Task JobFailuresNeverStartProtocol(string fault)
    {
        using var session = DiagnosticSession.Create(f.Root);
        var started = false;
        var runtime = new ZcodeRuntime(new("https://example.invalid/v1", "fixture-model"), _ => Task.FromResult("test"), _ => { }, Services("success", fault));
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => runtime.RunAsync(session, () => started = true, default));
            Assert.False(started); Assert.False(File.Exists(Path.Combine(session.Runtime, "create-seen")));
        }
        finally { session.Dispose(); Assert.Equal(CleanupResult.Complete, new SessionCleaner(f.Root).Clean(session.Root)); }
    }

    [Theory]
    [InlineData("success", FailureCode.None)]
    [InlineData("ignore-eof", FailureCode.None)]
    [InlineData("stderr-invalid", FailureCode.ProtocolMalformed)]
    [InlineData("stdout-invalid", FailureCode.ProtocolMalformed)]
    [InlineData("malformed", FailureCode.ProtocolMalformed)]
    [InlineData("early-exit", FailureCode.RuntimeEof)]
    [InlineData("create-failure", FailureCode.SessionCreateFailed)]
    [InlineData("send-failure", FailureCode.SessionSendFailed)]
    [InlineData("auth-error", FailureCode.ProviderAuthError)]
    [InlineData("network-error", FailureCode.ProviderNetworkError)]
    [InlineData("hang", FailureCode.RuntimeTimeout)]
    [InlineData("detached", FailureCode.RuntimeTimeout)]
    public async Task ProductionRunnerOwnsAllFailurePaths(string scenario, FailureCode expected)
    {
        var output = new List<string>(); string? sessionRoot = null;
        var services = Services(scenario, timeout: scenario is "hang" or "detached" ? TimeSpan.FromSeconds(4) : null);
        var runtime = new ZcodeRuntime(new("https://example.invalid/v1", "fixture-model"), _ => Task.FromResult("中文任务"), output.Add, services);
        var observer = new InspectingProvider(FoundationFixture.Provider(), s => sessionRoot = s.Root);
        var result = await new DiagnosticRunner(f.Vault, observer, runtime)
            .RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root);
        Assert.Equal(expected, result.Error); Assert.True(result.CleanupComplete); Assert.True(runtime.JobMembersExited);
        Assert.False(Directory.Exists(sessionRoot!));
        var persisted = string.Concat(Directory.GetFiles(result.ReportPath).Select(File.ReadAllText));
        Assert.Contains("YOLO", persisted);
        Assert.DoesNotContain(FoundationFixture.FakeKey, persisted); Assert.DoesNotContain("fixture-secret-must-not-persist", persisted);
        Assert.DoesNotContain("中文任务", persisted);
        if (scenario == "success") { Assert.Contains(output, x => x.Contains("山西晋铁源", StringComparison.Ordinal)); Assert.Contains("[Tool] Bash", output); }
    }

    [Fact]
    public async Task OperatorCancellationStopsSessionAndCleansJob()
    {
        using var cancel = new CancellationTokenSource();
        string? root = null;
        var runtime = new ZcodeRuntime(new("https://example.invalid/v1", "fixture-model"), _ => {
            cancel.Cancel(); return Task.FromResult("cancelled-task");
        }, _ => { }, Services("hang"));
        var observer = new InspectingProvider(FoundationFixture.Provider(), s => root = s.Root);
        var result = await new DiagnosticRunner(f.Vault, observer, runtime).RunAsync(f.Credential, FoundationFixture.Pin(), f.Reports, f.Root, cancel.Token);
        Assert.Equal(FailureCode.Cancelled, result.Error); Assert.True(result.CleanupComplete); Assert.True(runtime.JobMembersExited);
        Assert.False(Directory.Exists(root!));
    }

    private sealed class InspectingProvider(IProviderAdapter provider, Action<DiagnosticSession> inspect) : IProviderAdapter
    {
        public ProviderId Id => ProviderId.G;
        public void WriteTemporaryConfig(DiagnosticSession session, byte[] key) { inspect(session); provider.WriteTemporaryConfig(session, key); }
    }
}

// A blocking in-memory duplex stream makes response correlation/reverse requests
// deterministic without subprocesses, credentials or external network access.
public sealed class ProtocolTests
{
    private sealed class Lines : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        public void Put(string frame) => lines.Writer.TryWrite(frame + "\n");
        public void End() => lines.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            if (!await lines.Reader.WaitToReadAsync(token)) return 0;
            var line = await lines.Reader.ReadAsync(token); line.AsMemory().CopyTo(buffer); return line.Length;
        }
    }

    [Fact]
    public async Task UnicodeTaskIsOnlyInSendContentAndCreateIsExplicitYolo()
    {
        var input = new Lines(); var output = new StringWriter();
        await using var client = new ZcodeProtocolClient(input, output, "test"); client.Start();
        var create = client.CreateSessionAsync("中文路径");
        using var frame = JsonDocument.Parse(output.ToString().Trim());
        var p = frame.RootElement.GetProperty("params");
        Assert.Equal("yolo", p.GetProperty("mode").GetString()); Assert.False(p.GetProperty("dynamicWorkflowEnabled").GetBoolean());
        Assert.False(p.GetProperty("offPeakToolEnabled").GetBoolean()); Assert.False(p.GetProperty("titleGenerationEnabled").GetBoolean());
        Assert.False(p.TryGetProperty("toolAllowlist", out _)); Assert.False(p.TryGetProperty("toolDenylist", out _));
        input.Put("{\"id\":1,\"result\":{}}"); await create;
        var send = client.SendTaskAsync("山西晋铁源 中文任务");
        var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var sent = JsonDocument.Parse(rows[1]);
        Assert.Equal("山西晋铁源 中文任务", sent.RootElement.GetProperty("params").GetProperty("content").GetString());
        input.Put("{\"id\":2,\"result\":{}}"); await send;
    }

    [Theory]
    [InlineData("garbage", "PROTOCOL_MALFORMED")]
    [InlineData("[]", "PROTOCOL_MALFORMED")]
    [InlineData("{\"id\":1,\"error\":{\"message\":\"sensitive-payload\"}}", "SESSION_CREATE_FAILED")]
    public async Task MalformedAndProtocolErrorsAreSafe(string frame, string code)
    {
        var input = new Lines(); await using var client = new ZcodeProtocolClient(input, new StringWriter(), "test"); client.Start();
        var request = client.CreateSessionAsync("workspace"); input.Put(frame);
        var error = await Assert.ThrowsAsync<RdtException>(() => request); Assert.Equal(code, error.Code); Assert.DoesNotContain("sensitive-payload", error.ToString());
    }

    [Fact]
    public async Task EofImmediatelyFailsPendingRequest()
    {
        var input = new Lines(); await using var client = new ZcodeProtocolClient(input, new StringWriter(), "test"); client.Start();
        var request = client.ReadSessionAsync(); input.End();
        Assert.Equal("RUNTIME_EOF", (await Assert.ThrowsAsync<RdtException>(() => request)).Code);
    }

    [Fact]
    public async Task ProtocolTimeoutIsBounded()
    {
        var input = new Lines(); await using var client = new ZcodeProtocolClient(input, new StringWriter(), "test", null, TimeSpan.FromMilliseconds(30)); client.Start();
        Assert.Equal("PROTOCOL_TIMEOUT", (await Assert.ThrowsAsync<RdtException>(() => client.ReadSessionAsync())).Code);
    }

    [Theory]
    [InlineData("session/requestRuntimePreferences", "test", "user-execution", 0)]
    [InlineData("session/requestRuntimePreferences", "test", "runtime-materialization", 0)]
    [InlineData("session/requestRuntimePreferences", "other", "user-execution", -32602)]
    [InlineData("session/requestRuntimePreferences", "test", "unknown", -32602)]
    [InlineData("permission/approve", "test", "user-execution", -32601)]
    public async Task ReverseRequestsUseExactSchemaAndRejectUnknownMethods(string method, string sessionId, string scope, int errorCode)
    {
        var input = new Lines(); var output = new StringWriter(); var seen = new List<string>();
        await using var client = new ZcodeProtocolClient(input, output, "test", seen.Add); client.Start();
        input.Put(JsonSerializer.Serialize(new { id = "reverse-1", method, @params = new { sessionId, scope } }));
        var watch = Stopwatch.StartNew(); while (output.ToString().Length == 0 && watch.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(5);
        using var response = JsonDocument.Parse(output.ToString().Trim()); var frame = response.RootElement;
        if (errorCode == 0) Assert.False(frame.GetProperty("result").GetProperty("nativeSearchEnhancementsEnabled").GetBoolean());
        else Assert.Equal(errorCode, frame.GetProperty("error").GetProperty("code").GetInt32());
        if (errorCode == -32601) Assert.Equal([method], seen);
    }
}
