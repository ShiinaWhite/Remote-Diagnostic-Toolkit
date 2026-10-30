using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rdt.Auth;
using Rdt.Common;
using Rdt.Credentials;
using Rdt.Providers;
using Rdt.Runtime;
using Rdt.Session;
using Rdt.Zcode;
using Rdt.Runtime.Windows;
using System.Net;

namespace Rdt.RuntimeAcceptance;

// Explicit local acceptance only, outside CI and the production dependency graph.
// Never writes raw protocol, model output, tool payloads, credentials or PINs.
internal static class Program
{
    private static string phase = "input";
    private const string Expected = "山西晋铁源-真实G端到端测试";
    private static string Repo => FindRepo();
    private static string FindRepo()
    {
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Rdt.slnx"))) return d.FullName;
        throw new RdtException("REPOSITORY_REQUIRED");
    }

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args is ["preflight"])
            {
                phase = "profile-snapshot";
                var profile = Snapshot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode"));
                phase = "provider-dns";
                var options = GProviderOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "provider-g.json"));
                var addresses = await Dns.GetHostAddressesAsync(new Uri(options.BaseUrl).Host, cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { preflight = "PASS", profile_files = profile.Count, provider_addresses = addresses.Length }));
                return 0;
            }
            return await RunAsync(args, cancellation.Token);
        }
        catch (Exception error)
        {
            var failure = new { status = "PARTIAL", phase, error_type = error.GetType().Name };
            Console.WriteLine(JsonSerializer.Serialize(failure));
            var output = Path.Combine(Repo, "artifacts", "production-acceptance"); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "preparation-failure.json"), JsonSerializer.Serialize(failure));
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        var fixture = args is ["fixture", _];
        if (!fixture && args is not ["real", _]) throw new RdtException("ACCEPTANCE_INPUT_INVALID");
        var output = Path.Combine(Repo, "artifacts", "production-acceptance", fixture ? "fixture" : "real");
        Directory.CreateDirectory(output);
        var vault = new CredentialVault(); byte[] pin;
        string credential;
        var options = fixture ? new GProviderOptions("https://example.invalid/v1", "rdt-yolo-production") :
            GProviderOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "provider-g.json"));
        phase = "profile-snapshot";
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode");
        var before = Snapshot(profile);
        phase = "provider-dns";
        var expectedAddresses = fixture ? new HashSet<IPAddress>() : (await Dns.GetHostAddressesAsync(new Uri(options.BaseUrl).Host, token)).ToHashSet();
        if (fixture)
        {
            if (!Uri.TryCreate(args[1], UriKind.Absolute, out var loopback) || loopback.Scheme != "http" ||
                loopback.Host != "127.0.0.1" || loopback.AbsolutePath != "/v1" || loopback.UserInfo.Length != 0 ||
                loopback.Query.Length != 0 || loopback.Fragment.Length != 0) throw new RdtException("LOOPBACK_ONLY");
            credential = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N") + ".rdt");
            pin = Encoding.UTF8.GetBytes("rdt-acceptance-fixture-pin");
            var fakeKey = Encoding.UTF8.GetBytes("rdt-qualification-invalid-local-fixture");
            try { vault.Create(credential, pin, fakeKey); } finally { CryptographicOperations.ZeroMemory(fakeKey); }
        }
        else
        {
            credential = Path.GetFullPath(args[1]);
            Console.WriteLine("First real G acceptance: synthetic Windows/Unicode task only.");
            phase = "credential-pin";
            pin = new ConsoleSecretInput().Read("Operator PIN: ", token);
        }
        var facts = new Facts();
        NetworkProbe? network = null;
        DiagnosticSession? current = null;
        IProviderAdapter provider = new ObservedProvider(new GProviderAdapter(options), session => {
            current = session;
            facts.Workspace = session.Workspace;
            if (fixture)
            {
                // Synthetic loopback endpoint exists only in this explicitly selected developer harness.
                var config = JsonNode.Parse(File.ReadAllBytes(session.ProviderFile))!;
                config["config"]!["providerConfigRules"]!["providerRules"]![0]!["config"]!["api"]!["baseUrl"] = args[1];
                File.WriteAllText(session.ProviderFile, config.ToJsonString());
                var launch = Path.Combine(Repo, "artifacts", "production-acceptance", "production", "launch.json");
                Directory.CreateDirectory(Path.GetDirectoryName(launch)!);
                File.WriteAllText(launch + ".tmp", JsonSerializer.Serialize(new {
                    workspace = session.Workspace,
                    powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
                }));
                File.Move(launch + ".tmp", launch, overwrite: true);
            }
        });
        // Keep secret comparison material only in memory, zero it after the local scan.
        provider = new SecretCheckingProvider(provider, facts);
        var services = new RuntimeServices {
            CreateJob = () => { var job = new RuntimeJob(); network = new NetworkProbe(job); network.Start(); return job; },
            ObserveSnapshot = facts.Observe,
            ObserveExit = session => { network?.Stop(); facts.ScanLogs(session.Root, session.ProviderFile); }
        };
        var runtime = new ZcodeRuntime(options, _ => Task.FromResult(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "first-real-task.txt"), Encoding.UTF8)),
            Console.WriteLine, services);
        try
        {
            phase = "runtime";
            var result = await new DiagnosticRunner(vault, provider, runtime).RunAsync(credential, pin,
                Path.Combine(Repo, "Reports"), cancellationToken: token);
            phase = "profile-snapshot-after";
            var profileUnchanged = before.OrderBy(x => x.Key).SequenceEqual(Snapshot(profile).OrderBy(x => x.Key));
            phase = "report-scan";
            facts.ScanLogs(result.ReportPath, null);
            var unexpectedNetwork = network?.Unexpected(expectedAddresses) ?? 0;
            var allOperations = facts.PowerShell && facts.SystemQuery && facts.Created && facts.Verified && facts.Read && facts.Deleted && facts.Unicode && facts.CreatedOnDisk && facts.VerifiedOnDisk && facts.DeletedOnDisk;
            var success = result.Success && allOperations && runtime.JobMembersExited && profileUnchanged &&
                facts.KeyAbsent && network is { Failed: false } && unexpectedNetwork == 0;
            var evidence = new {
                status = success ? "PASS" : "PARTIAL", real_api_used = !fixture,
                credential = result.RuntimeStarted ? "PRESENT / VALID" : "NOT_VALIDATED",
                provider = "G", model = options.ModelId, api_endpoint_profile = options.EndpointProfile,
                api_auth = facts.ToolCall && !fixture ? "SUCCESS" : fixture ? "FIXTURE" : "NOT_PROVEN",
                tool_call = facts.ToolCall, powershell = facts.PowerShell, unicode = facts.Unicode,
                system_query = facts.SystemQuery, workspace_create = facts.Created && facts.CreatedOnDisk,
                workspace_read = facts.Verified && facts.Read && facts.VerifiedOnDisk,
                workspace_delete = facts.Deleted && facts.DeletedOnDisk,
                final_response = result.Success, yolo = result.RuntimeStarted && facts.Yolo,
                profile_unchanged = profileUnchanged, job_members_exited = runtime.JobMembersExited,
                session_cleanup = current != null && !Directory.Exists(current.Root), error = result.Error.ToString(),
                control_plane_network = unexpectedNetwork == 0 && network is { Failed: false } ? "NO_EXTRA_TCP_OBSERVED; FIXED_BUILTIN_REFRESH_PATH" : "REQUIRES_REVIEW",
                observed_tcp_addresses = network?.Observed ?? 0, unexpected_tcp_addresses = unexpectedNetwork,
                network_limitations = "25ms IPv4/IPv6 TCP Job-member polling can miss short connections; UDP/DNS/TLS payloads are not captured",
                api_key_in_argv = false, api_key_in_logs = !facts.KeyAbsent,
                report = Path.GetFileName(result.ReportPath)
            };
            File.WriteAllText(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Acceptance: {(success ? "PASS" : "PARTIAL")}. Safe evidence: artifacts/production-acceptance/{(fixture ? "fixture" : "real")}/evidence.json");
            return success ? 0 : 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pin);
            facts.Dispose(); network?.Dispose();
            if (fixture) File.Delete(credential);
        }
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>();
        if (!Directory.Exists(root)) return result;
        PrivateFiles.RejectLinks(root);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            PrivateFiles.RejectLinks(file);
            // Observe without denying existing writers/delete sharing in the real profile.
            // The acceptance never opens these files for writing or changes their ACLs.
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            result[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(input));
        }
        return result;
    }

    private sealed class ObservedProvider(IProviderAdapter provider, Action<DiagnosticSession> observe) : IProviderAdapter
    {
        public ProviderId Id => ProviderId.G;
        public void WriteTemporaryConfig(DiagnosticSession session, byte[] secret) { provider.WriteTemporaryConfig(session, secret); observe(session); }
    }

    private sealed class SecretCheckingProvider(IProviderAdapter provider, Facts facts) : IProviderAdapter
    {
        public ProviderId Id => ProviderId.G;
        public void WriteTemporaryConfig(DiagnosticSession session, byte[] secret)
        { facts.Secret = secret.ToArray(); provider.WriteTemporaryConfig(session, secret); }
    }

    private sealed class Facts : IDisposable
    {
        internal byte[] Secret = [];
        internal bool KeyAbsent = true;
        internal string Workspace = "";
        internal bool ToolCall, PowerShell, SystemQuery, Created, Verified, Read, Deleted, Unicode, Yolo;
        internal bool CreatedOnDisk, VerifiedOnDisk, DeletedOnDisk;
        internal void ScanLogs(string root, string? providerFile)
        {
            if (Secret.Length == 0 || !Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(path, providerFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(path, Path.Combine(root, ".lease"), StringComparison.OrdinalIgnoreCase)) continue;
                PrivateFiles.RejectLinks(path);
                // This acceptance scans local files in memory and emits only the boolean.
                var bytes = File.ReadAllBytes(path);
                KeyAbsent &= bytes.AsSpan().IndexOf(Secret) < 0;
            }
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
        internal void Observe(JsonElement snapshot)
        {
            Yolo = snapshot.GetProperty("settings").GetProperty("permission").GetProperty("mode").GetString() == "yolo";
            var path = Path.Combine(Workspace, "rdt-real-test.txt");
            if (File.Exists(path))
            {
                CreatedOnDisk = true;
                try { VerifiedOnDisk |= File.ReadAllText(path, new UTF8Encoding(false, true)) == Expected; } catch (IOException) { }
            }
            else if (CreatedOnDisk) DeletedOnDisk = true;
            foreach (var message in snapshot.GetProperty("messages").EnumerateArray())
            foreach (var part in message.GetProperty("parts").EnumerateArray())
            {
                if (part.GetProperty("type").GetString() != "tool" || !part.TryGetProperty("state", out var state) ||
                    !state.TryGetProperty("status", out var status) || status.GetString() != "completed") continue;
                ToolCall = true;
                var name = part.GetProperty("tool").GetString();
                var output = state.TryGetProperty("output", out var text) ? text.ToString() : "";
                if (name == "Bash")
                {
                    var input = state.GetProperty("input").GetProperty("command").GetString() ?? "";
                    PowerShell |= input.Contains("powershell", StringComparison.OrdinalIgnoreCase) || input.Contains("pwsh", StringComparison.OrdinalIgnoreCase);
                    SystemQuery |= output.Contains("RDT_REAL_SYSTEM_OK", StringComparison.Ordinal);
                    Created |= output.Contains("RDT_REAL_CREATED", StringComparison.Ordinal);
                    Verified |= output.Contains("RDT_REAL_VERIFIED:" + Expected, StringComparison.Ordinal);
                    Deleted |= output.Contains("RDT_REAL_DELETED", StringComparison.Ordinal);
                }
                if (name == "Read" && output.Contains(Expected, StringComparison.Ordinal)) Read = true;
                Unicode |= output.Contains(Expected, StringComparison.Ordinal);
            }
        }
    }
}
