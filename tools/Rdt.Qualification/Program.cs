using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Session;
using Rdt.Zcode;
using Microsoft.Win32;

namespace Rdt.Qualification;

// Development-only executable, deliberately absent from Rdt.slnx and the CLI publish graph.
internal static class Program
{
    private static readonly HashSet<string> Cases = new(StringComparer.Ordinal)
    {
        "version", "help", "doctor", "surface", "contract", "read", "glob", "grep", "system-info",
        "write", "edit", "patch", "delete", "cmd", "powershell", "bash", "registry",
        "agent", "task", "skill", "mcp", "plugin", "workflow", "cron", "mode", "exit-plan",
        "webfetch", "websearch", "node-repl", "hook", "stdin", "protocol", "agent-server", "protocol-cancel", "dotenv"
    };

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 3 || !Cases.Contains(args[1])) return 2;
        var repo = FindRepo();
        var identityFile = Path.Combine(repo, "scripts", "runtime-qualification", "runtime-identity.json");
        using var identity = JsonDocument.Parse(File.ReadAllBytes(identityFile));
        var data = identity.RootElement;
        var candidate = Path.GetFullPath(args[0]);
        var expectedHash = data.GetProperty("runtime_sha256").GetString();
        if (!data.GetProperty("qualification_only").GetBoolean() || data.GetProperty("production_enabled").GetBoolean()
            || expectedHash == null || !Regex.IsMatch(expectedHash, "^[a-f0-9]{64}$")
            || Path.GetFileName(candidate) != data.GetProperty("runtime_filename").GetString()) return 3;
        using (var binary = File.OpenRead(candidate))
            if (!Convert.ToHexString(SHA256.HashData(binary)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return 3;
        if (!Uri.TryCreate(args[2], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http"
            || endpoint.Host != "127.0.0.1" || endpoint.Port <= 0 || endpoint.AbsolutePath != "/v1"
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) return 4;

        var caseName = args[1];
        var output = Path.Combine(repo, "artifacts", "runtime-qualification", "runs", caseName);
        Directory.CreateDirectory(output);
        string? controlledParent = null;
        var tempBase = Path.GetTempPath();
        if (caseName == "dotenv")
        {
            controlledParent = Path.Combine(tempBase, "RdtQualificationParent-" + Guid.NewGuid().ToString("N"));
            PrivateFiles.CreateDirectory(controlledParent);
            PrivateFiles.WriteNew(Path.Combine(controlledParent, ".env"), System.Text.Encoding.UTF8.GetBytes(
                "ZCODE_BASE_URL=" + endpoint.GetLeftPart(UriPartial.Authority) + "\n"));
            tempBase = controlledParent;
        }
        using var session = DiagnosticSession.Create(tempBase);
        var sessionRoot = session.Root;
        var before = new Dictionary<string, string>();
        Dictionary<string, string>? after = null;
        CleanupResult cleanup = CleanupResult.Failed;
        var timedOut = false;
        var exit = -1;
        var stdout = "";
        var stderr = "";
        object? protocolResult = null;
        var observerErrors = new List<string>();
        var registryCanary = "Software\\RdtQualification-" + session.Id.ToString("N");
        using (var key = Registry.CurrentUser.OpenSubKey(registryCanary))
            if (key != null) throw new IOException("CANARY_COLLISION");
        var registrySideEffect = false;
        using var observation = new WindowsObservation(sessionRoot, observerErrors);
        try
        {
            session.WriteRuntimeConfig();
            if (caseName is "mcp" or "plugin" or "skill" or "hook") PrepareExtensionCanaries(session, caseName);
            var fixtureFile = Path.Combine(session.Workspace, "fixture.txt");
            PrivateFiles.WriteNew(fixtureFile, "RDT_FIXTURE_SENTINEL\nalpha diagnostic fixture\n"u8);
            PrivateFiles.WriteNew(Path.Combine(session.Workspace, "delete-target.txt"), "KEEP_DELETE_TARGET"u8);
            Directory.CreateDirectory(Path.Combine(session.Workspace, "nested"));
            PrivateFiles.WriteNew(Path.Combine(session.Workspace, "nested", "sample.txt"), "RDT_NESTED_SENTINEL"u8);
            WriteProvider(session, endpoint.AbsoluteUri.TrimEnd('/'), "rdt-case-" + caseName);
            before = Snapshot(session.Workspace);
            // Do not read an operator's ambient .env while qualifying a synthetic fixture.
            // A dedicated, controlled ancestor experiment is separate from this preflight.
            for (var parent = new DirectoryInfo(session.Workspace); parent != null; parent = parent.Parent)
                if (File.Exists(Path.Combine(parent.FullName, ".env")))
                {
                    if (parent.FullName == controlledParent) break;
                    throw new IOException("UNCONTROLLED_DOTENV_ANCESTOR");
                }
            var prompt = "RDT qualification fixture case " + caseName + ". Use only synthetic fixture data.";
            var start = ReadOnlyPolicy.CreateStartInfo(candidate, session, prompt);
            if (caseName is "version" or "help" or "doctor")
            {
                start.ArgumentList.Clear();
                start.ArgumentList.Add(caseName == "version" ? "--version" : caseName == "help" ? "--help" : "doctor");
                if (caseName == "doctor") start.ArgumentList.Add("--json");
            }
            else if (caseName is "protocol" or "agent-server" or "protocol-cancel")
            {
                start.ArgumentList.Clear();
                start.ArgumentList.Add(caseName == "agent-server" ? "agent-server" : "app-server");
                start.ArgumentList.Add("--stdio");
                start.ArgumentList.Add("--cwd");
                start.ArgumentList.Add(session.Workspace);
            }
            else if (caseName == "stdin")
            {
                start.ArgumentList.Clear();
                start.ArgumentList.Add("--mode"); start.ArgumentList.Add("plan");
                start.ArgumentList.Add("--json");
            }
            else if (caseName != "contract")
            {
                // Official event format is used for observations only; production is unchanged.
                start.ArgumentList.Add("--output-format"); start.ArgumentList.Add("stream-json");
            }
            File.WriteAllText(Path.Combine(output, "launch.json"), JsonSerializer.Serialize(new
            {
                runtime_sha256 = expectedHash, args = start.ArgumentList.ToArray(),
                environment = start.Environment.ToDictionary(p => p.Key, p => p.Value),
                fixture_file = fixtureFile, session = sessionRoot
            }));
            using var process = new Process { StartInfo = start };
            process.Start();
            observation.Start(process.Id);
            var protocol = caseName is "protocol" or "agent-server" or "protocol-cancel" ? new ProtocolProbe(process, endpoint) : null;
            var outTask = protocol?.CaptureAsync() ?? process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            try
            {
                if (protocol != null) protocolResult = await protocol.ExerciseAsync(session, caseName).WaitAsync(TimeSpan.FromSeconds(45));
                else if (caseName == "stdin") await process.StandardInput.WriteLineAsync(prompt);
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (TimeoutException) { timedOut = true; }
            catch (OperationCanceledException) { timedOut = true; }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                stdout = await outTask.WaitAsync(TimeSpan.FromSeconds(5));
                stderr = await errTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            exit = process.ExitCode;
            after = Snapshot(session.Workspace);
            File.WriteAllText(Path.Combine(output, "session-files.json"), JsonSerializer.Serialize(Snapshot(session.Root)));
        }
        catch (Exception e) { observerErrors.Add(e.GetType().Name); }
        finally
        {
            observation.Stop();
            File.WriteAllText(Path.Combine(output, "stdout.txt"), stdout);
            File.WriteAllText(Path.Combine(output, "stderr.txt"), stderr);
            if (after == null && Directory.Exists(session.Workspace))
            {
                try { after = Snapshot(session.Workspace); }
                catch (Exception e) { observerErrors.Add("SNAPSHOT:" + e.GetType().Name); }
            }
            using (var key = Registry.CurrentUser.OpenSubKey(registryCanary)) registrySideEffect = key != null;
            if (registrySideEffect) Registry.CurrentUser.DeleteSubKeyTree(registryCanary, throwOnMissingSubKey: false);
            session.Dispose();
            cleanup = new SessionCleaner(tempBase).Clean(sessionRoot);
            if (controlledParent != null)
            {
                File.Delete(Path.Combine(controlledParent, ".env"));
                try { Directory.Delete(controlledParent); }
                catch (IOException) { observerErrors.Add("CONTROLLED_PARENT_HAS_RUNTIME_RESIDUE"); }
            }
        }
        var result = new
        {
            case_name = caseName, exit_code = exit, timed_out = timedOut,
            workspace_before = before, workspace_after = after,
            cleanup = cleanup.ToString(), session_absent = !Directory.Exists(sessionRoot),
            observer_errors = observerErrors, observation = observation.Result(),
            registry_canary_created = registrySideEffect,
            protocol_result = protocolResult,
            stdout_bytes = stdout.Length, stderr_bytes = stderr.Length,
            runtime_sha256 = expectedHash, real_api_used = false, production_enabled = false
        };
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(new { case_name = caseName, exit_code = exit, timed_out = timedOut,
            cleanup = cleanup.ToString(), observer_errors = observerErrors.Count }));
        return cleanup is CleanupResult.Complete && !timedOut && observerErrors.Count == 0 ? 0 : 1;
    }

    private static void WriteProvider(DiagnosticSession session, string endpoint, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(session.ProviderFile)!);
        var config = new
        {
            schemaVersion = 1,
            config = new
            {
                providerConfigRules = new { providerRules = new[] { new { providerId = "rdt-fixture", providerName = "RDT Fixture",
                    config = new { group = "standard-personal", access = new { type = "api-key", apiKey = "rdt-qualification-invalid-local-fixture" },
                        api = new { type = "openai-chat-completions", baseUrl = endpoint }, personalModelIds = new[] { model } } } } },
                modelConfigRules = new { providerModelRules = Array.Empty<object>(), manualProviderModelRules = Array.Empty<object>() },
                defaultModelSelection = new { providerId = "rdt-fixture", modelId = model }
            }
        };
        PrivateFiles.WriteNew(session.ProviderFile, JsonSerializer.SerializeToUtf8Bytes(config));
    }

    private static void PrepareExtensionCanaries(DiagnosticSession session, string caseName)
    {
        var configFile = Path.Combine(session.Home, ".zcode", "cli", "config.json");
        var config = JsonNode.Parse(File.ReadAllText(configFile))!;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var canary = Path.Combine(session.Workspace, "EXTENSION_MUST_NOT_EXIST.txt").Replace("'", "''", StringComparison.Ordinal);
        var arguments = new[] { "-NoProfile", "-NonInteractive", "-Command", "Set-Content -LiteralPath '" + canary + "' -Value RDT_EXTENSION_CANARY" };
        var server = new { type = "stdio", command = powershell, args = arguments, enabled = true, alwaysLoad = true };
        if (caseName == "mcp")
            config["mcp"] = JsonSerializer.SerializeToNode(new { servers = new Dictionary<string, object> { ["rdt_fixture"] = server } });
        if (caseName == "hook")
            config["hooks"] = JsonSerializer.SerializeToNode(new { enabled = false,
                events = new Dictionary<string, object> { ["PreToolUse"] = new[] { new { matcher = "Read",
                    hooks = new[] { new { type = "process", command = powershell, args = arguments, timeoutMs = 5000 } } } } } });
        if (caseName is "plugin" or "skill")
        {
            var plugin = Path.Combine(session.Data, "canary-plugin");
            var skills = Path.Combine(plugin, "skills", "rdt-fixture");
            Directory.CreateDirectory(Path.Combine(plugin, ".zcode-plugin"));
            Directory.CreateDirectory(skills);
            PrivateFiles.WriteNew(Path.Combine(plugin, ".zcode-plugin", "plugin.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { name = "rdt-fixture-plugin", version = "0.0.0", description = "Synthetic disabled qualification canary" }));
            PrivateFiles.WriteNew(Path.Combine(plugin, ".mcp.json"), JsonSerializer.SerializeToUtf8Bytes(
                new { mcpServers = new Dictionary<string, object> { ["rdt_fixture_plugin"] = server } }));
            PrivateFiles.WriteNew(Path.Combine(skills, "SKILL.md"), System.Text.Encoding.UTF8.GetBytes(
                "---\nname: rdt-fixture\ndescription: Synthetic disabled skill\n---\nRDT_DISABLED_SKILL_MARKER\n"));
            config["plugins"] = JsonSerializer.SerializeToNode(new { enabled = false, dirs = new[] { plugin },
                enabledPlugins = new Dictionary<string, bool> { ["rdt-fixture-plugin"] = true } });
            config["skills"] = JsonSerializer.SerializeToNode(new { enabled = false, roots = new[] { Path.GetDirectoryName(skills)! } });
        }
        File.WriteAllText(configFile, config.ToJsonString()); // This invocation owns this new private fixture config.
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var files = new Dictionary<string, string>();
        void Visit(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("REPARSE_POINT");
            if (Directory.Exists(path))
                foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Visit(entry);
            else
            {
                if (Path.GetFileName(path) == ".lease") return;
                using var stream = File.OpenRead(path);
                files[Path.GetRelativePath(root, path)] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
        }
        Visit(root);
        return files;
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Rdt.slnx"))) return dir.FullName;
        throw new IOException("QUALIFICATION_REPO_REQUIRED");
    }
}
