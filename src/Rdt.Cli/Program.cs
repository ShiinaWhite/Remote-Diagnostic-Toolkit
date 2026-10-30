using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rdt.Auth;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Credentials;
using Rdt.Providers;
using Rdt.Runtime;
using Rdt.Zcode;

namespace Rdt.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.FirstOrDefault() == "__mock-runtime") return await MockChild.RunAsync(args[1..]);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await RunAsync(args, new ConsoleSecretInput(), Console.Out, cancel.Token); }
        catch (OperationCanceledException) { Console.WriteLine("Session cancelled."); return 130; }
        catch (RdtException) { Console.WriteLine("Operation unavailable or input invalid. See README."); return 1; }
        catch (Exception) { Console.WriteLine("Operation failed. No secret details are displayed."); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }

    public static async Task<int> RunAsync(string[] args, ISecretInput input, TextWriter output, CancellationToken cancellationToken = default)
    {
        await output.WriteLineAsync("Remote Diagnostic Toolkit");
        await output.WriteLineAsync($"RDT {RdtInfo.Version}");
        var stale = new SessionCleaner().FindStale(DateTimeOffset.UtcNow);
        if (stale.Count > 0) await output.WriteLineAsync($"Inactive temporary sessions: {stale.Count}. Use cleanup-stale to clean owned sessions.");
        if (args.Length == 0 || args is ["help"] or ["--help"])
        {
            await output.WriteLineAsync("Commands: run <credential-file> | credential-init <file> | demo | run --mock <credential-file> | cleanup-stale");
            await output.WriteLineAsync("Provider: G | Mode: YOLO | Runtime: fixed Windows x64 stdio");
            return 0;
        }
        if (args is ["cleanup-stale"])
        {
            var ok = true;
            foreach (var root in stale)
            {
                var result = new SessionCleaner().Clean(root);
                await output.WriteLineAsync($"Temporary session cleanup: {result}");
                ok &= result is CleanupResult.Complete or CleanupResult.AlreadyAbsent;
            }
            return ok ? 0 : 1;
        }
        if (args is ["credential-init", var target])
        {
            var pin = input.Read("New operator PIN (8+ characters): ", cancellationToken);
            byte[]? confirm = null;
            byte[]? secret = null;
            try
            {
                confirm = input.Read("Confirm operator PIN: ", cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(pin, confirm)) throw new RdtException("PIN_MISMATCH");
                secret = input.Read("Provider G API Key: ", cancellationToken);
                new CredentialVault().Create(Path.GetFullPath(target), pin, secret);
                await output.WriteLineAsync("Encrypted credential created. Provider: G");
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pin);
                if (confirm != null) CryptographicOperations.ZeroMemory(confirm);
                if (secret != null) CryptographicOperations.ZeroMemory(secret);
            }
        }
        if (args is ["run", var productionCredential])
        {
            var options = GProviderOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "provider-g.json"));
            var pin = input.Read("Operator PIN: ", cancellationToken);
            try
            {
                var runtime = new ZcodeRuntime(options, token => ReadTaskAsync(Console.In, output, token), output.WriteLine);
                var runner = new DiagnosticRunner(new CredentialVault(), new GProviderAdapter(options), runtime);
                var result = await runner.RunAsync(Path.GetFullPath(productionCredential), pin,
                    Path.Combine(Environment.CurrentDirectory, "Reports"), cancellationToken: cancellationToken,
                    unlocked: () => output.WriteLine("Credential unlocked."));
                await output.WriteLineAsync($"Cleanup: {(result.CleanupComplete ? "complete" : "incomplete")}");
                await output.WriteLineAsync($"Report: {result.ReportPath}");
                await output.WriteLineAsync($"Result: {result.Error}");
                return result.Success ? 0 : result.Error == Rdt.Reporting.FailureCode.Cancelled ? 130 : 1;
            }
            finally { CryptographicOperations.ZeroMemory(pin); }
        }
        string credential;
        string? demoRoot = null;
        if (args is ["demo"])
        {
            await output.WriteLineAsync("MOCK acceptance: temporary test credential; no API request or system diagnostics.");
            demoRoot = Path.Combine(Path.GetTempPath(), "RdtDemo-" + Guid.NewGuid().ToString("N"));
            PrivateFiles.CreateDirectory(demoRoot);
            credential = Path.Combine(demoRoot, "test.rdt");
        }
        else if (args is ["run", "--mock", var file]) credential = Path.GetFullPath(file);
        else throw new RdtException("CLI_INPUT_INVALID");
        byte[]? operatorPin = null;
        try
        {
            operatorPin = input.Read("Operator PIN: ", cancellationToken);
            if (demoRoot != null)
            {
                var fakeKey = Encoding.UTF8.GetBytes("rdt-fixture-not-a-valid-api-key");
                try { new CredentialVault().Create(credential, operatorPin, fakeKey); }
                finally { CryptographicOperations.ZeroMemory(fakeKey); }
            }
            var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "rdt.exe" : "rdt");
            var runner = new DiagnosticRunner(new CredentialVault(),
                new GProviderAdapter(new GProviderOptions("https://example.invalid/v1", "rdt-fixture")), new MockProcessRuntime(executable));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await runner.RunAsync(credential, operatorPin, Path.Combine(Environment.CurrentDirectory, "Reports"),
                cancellationToken: timeout.Token, unlocked: () =>
                {
                    output.WriteLine("Credential unlocked."); output.WriteLine("Provider: G");
                    output.WriteLine("Mode: READ ONLY"); output.WriteLine("Session: TEMP"); output.WriteLine("Runtime: MOCK");
                });
            await output.WriteLineAsync($"Cleanup: {(result.CleanupComplete ? "complete" : "incomplete")}");
            await output.WriteLineAsync($"Report: {result.ReportPath}");
            await output.WriteLineAsync($"Result: {result.Error}");
            return result.Success ? 0 : result.Error == Rdt.Reporting.FailureCode.Cancelled ? 130 : 1;
        }
        finally
        {
            if (operatorPin != null) CryptographicOperations.ZeroMemory(operatorPin);
            if (demoRoot != null)
            {
                // Only the two known demo artifacts are removed; no recursive user-path delete.
                File.Delete(credential);
                Directory.Delete(demoRoot);
            }
        }
    }

    internal static async Task<string> ReadTaskAsync(TextReader reader, TextWriter output, CancellationToken token)
    {
        await output.WriteLineAsync("\nEnter diagnostic task.\nFinish with a line containing only:\n.run\n");
        await output.WriteAsync("RDT > "); await output.FlushAsync(token);
        var task = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(token).AsTask().WaitAsync(token);
            if (line == null) throw new RdtException("TASK_REQUIRED");
            if (line == ".run") return task.ToString();
            if (task.Length + line.Length > 1_000_000) throw new RdtException("CLI_INPUT_INVALID");
            task.AppendLine(line);
        }
    }
}

internal static class MockChild
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length < 2 || !Enum.TryParse<MockBehavior>(args[0], out var behavior)) return 31;
            string? Value(string option)
            {
                var index = Array.IndexOf(args, option);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }
            if (Value("--mode") != "plan" || Value("--workflow-mode") != "disabled" ||
                Value("--disallowed-tools") != ReadOnlyPolicy.DisallowedTools) return 32;
            var home = Environment.GetEnvironmentVariable("HOME")!;
            var data = Environment.GetEnvironmentVariable("ZCODE_DATA_BASE_DIR")!;
            var root = Directory.GetParent(home)!.FullName;
            if (Environment.GetEnvironmentVariable("USERPROFILE") != home ||
                Environment.GetEnvironmentVariable("RDT_SESSION_ID") != Path.GetFileName(root)[4..] ||
                Value("--cwd") != Path.Combine(root, "workspace") || Environment.CurrentDirectory != Value("--cwd") ||
                data != Path.Combine(root, "data")) return 33;
            using var provider = JsonDocument.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable("ZCODE_PERSONAL_PROVIDER_CONFIG_FILE")!));
            var config = provider.RootElement.GetProperty("config");
            var access = config.GetProperty("providerConfigRules").GetProperty("providerRules")[0].GetProperty("config").GetProperty("access");
            if (provider.RootElement.GetProperty("schemaVersion").GetInt32() != 1 || access.GetProperty("type").GetString() != "api-key" ||
                string.IsNullOrEmpty(access.GetProperty("apiKey").GetString())) return 34;
            using var runtime = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(home, ".zcode", "cli", "config.json")));
            if (runtime.RootElement.GetProperty("permission").GetProperty("mode").GetString() != "plan" ||
                runtime.RootElement.GetProperty("hooks").GetProperty("enabled").GetBoolean()) return 35;
            if (behavior == MockBehavior.Hang) await Task.Delay(Timeout.InfiniteTimeSpan);
            if (behavior == MockBehavior.Crash) return 27;
            Console.WriteLine("{\"mock\":true,\"result\":\"fixture-validated\"}");
            return 0;
        }
        catch { return 36; }
    }
}
