using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Session;

namespace Rdt.Qualification;

// Developer-only. Absent from Rdt.slnx, customer publish graph and production adapter.
internal static class Program
{
    private static readonly HashSet<string> Cases = new(("surface read write edit delete mkdir subprocess " +
        "services processes eventlog os network scheduled registry-read registry-lifecycle privilege " +
        "parent-dotenv local-builtin-idle glob grep patch tool-failure model-cancel tool-stop wrapper-cancel detached-child").Split(' '));

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4 || !Cases.Contains(args[1])) return 2;
        var repo = FindRepo();
        using var identity = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repo, "scripts", "runtime-qualification", "yolo-runtime-identity.json")));
        var pin = identity.RootElement;
        var candidate = Path.GetFullPath(args[0]);
        if (!pin.GetProperty("qualification_only").GetBoolean() || pin.GetProperty("production_enabled").GetBoolean()
            || Path.GetFileName(candidate) != pin.GetProperty("runtime_filename").GetString()
            || Hash(candidate) != pin.GetProperty("runtime_sha256").GetString()
            || Hash(args[3]) != pin.GetProperty("builtin_sha256").GetString()) return 3;
        if (!Uri.TryCreate(args[2], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http"
            || endpoint.Host != "127.0.0.1" || endpoint.Port <= 0 || endpoint.AbsolutePath != "/v1"
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) return 4;
        var caseName = args[1];
        var output = Path.Combine(repo, "artifacts", "runtime-qualification", "yolo", "runs", caseName);
        Directory.CreateDirectory(output);
        var tempBase = Path.GetTempPath();
        string? parent = null;
        if (caseName == "parent-dotenv")
        {
            parent = Path.Combine(tempBase, "RdtYoloParent-" + Guid.NewGuid().ToString("N"));
            PrivateFiles.CreateDirectory(parent);
            PrivateFiles.WriteNew(Path.Combine(parent, ".env"), System.Text.Encoding.UTF8.GetBytes(
                "RDT_DOTENV_CANARY=SHOULD_NOT_LOAD\nZCODE_BASE_URL=" + endpoint.GetLeftPart(UriPartial.Authority) + "\n"));
            tempBase = Path.Combine(parent, "child");
            PrivateFiles.CreateDirectory(tempBase);
        }
        DiagnosticSession createdSession;
        try { createdSession = DiagnosticSession.Create(tempBase); }
        catch
        {
            if (parent != null)
            {
                File.Delete(Path.Combine(parent, ".env"));
                Directory.Delete(tempBase); Directory.Delete(parent); // Only known empty containers; never recurse.
            }
            throw;
        }
        using var session = createdSession;
        var started = DateTime.UtcNow;
        var registryPath = "Software\\RdtQualification-" + session.Id.ToString("N");
        using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
            if (key != null) throw new IOException("CANARY_COLLISION");
        var errors = new List<string>();
        using var observation = new WindowsObservation(session.Root, errors);
        object? protocolResult = null, children = null;
        var stdout = ""; var stderr = ""; var exit = -1; var forcedKill = false;
        var registryAbsentBeforeRecovery = false;
        CleanupResult cleanup = CleanupResult.Failed;
        try
        {
            // Fresh normal YOLO config, no Phase 1 feature disabling or tool filters.
            var configDir = Path.Combine(session.Home, ".zcode", "cli");
            Directory.CreateDirectory(configDir);
            PrivateFiles.WriteNew(Path.Combine(configDir, "config.json"), "{\"permission\":{\"mode\":\"yolo\"}}"u8);
            PrivateFiles.WriteNew(Path.Combine(session.Workspace, "fixture.txt"), "RDT_YOLO_SENTINEL\nalpha fixture\n"u8);
            PrivateFiles.WriteNew(Path.Combine(session.Workspace, "edit-target.txt"), "RDT_BEFORE_EDIT\n"u8);
            PrivateFiles.WriteNew(Path.Combine(session.Workspace, "delete-target.txt"), "RDT_DELETE_ME\n"u8);
            if (caseName == "registry-read")
            {
                using var key = Registry.CurrentUser.CreateSubKey(registryPath);
                key.SetValue("Fixture", "RDT_REGISTRY_SENTINEL");
            }
            WriteProvider(session, endpoint.AbsoluteUri, "rdt-yolo-" + caseName);
            var builtin = Path.Combine(session.Data, "zcode-builtin.json");
            PrivateFiles.WriteNew(builtin, File.ReadAllBytes(args[3]));
            var start = new ProcessStartInfo(candidate) { WorkingDirectory = session.Workspace,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "agent-server", "--stdio", "--cwd", session.Workspace }) start.ArgumentList.Add(arg);
            start.Environment.Clear();
            foreach (var (name, value) in session.EnvironmentVariables()) start.Environment[name] = value;
            // Phase 2 candidate environment, not a change to the frozen production contract.
            // No inherited PATH. Windows PowerShell native command discovery needs PATHEXT;
            // upstream cancellation invokes bare taskkill, which needs a system-only PATH.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            start.Environment["PATH"] = string.Join(';', Path.Combine(windows, "System32"), windows,
                Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0"));
            start.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            start.Environment["ComSpec"] = Path.Combine(windows, "System32", "cmd.exe");
            start.Environment["ZCODE_BUILTIN_PROVIDER_CONFIG_FILE"] = builtin;
            if (caseName == "local-builtin-idle") start.Environment["ZCODE_BASE_URL"] = endpoint.GetLeftPart(UriPartial.Authority);
            var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            using var principalIdentity = WindowsIdentity.GetCurrent();
            Write(output, "launch.json", new { runtime_sha256 = pin.GetProperty("runtime_sha256").GetString(),
                args = start.ArgumentList.ToArray(), environment = start.Environment.ToDictionary(p => p.Key, p => p.Value),
                workspace = session.Workspace, session = session.Root, session_id = session.Id.ToString("N"),
                registry_path = registryPath, powershell = ps,
                administrator = new WindowsPrincipal(principalIdentity).IsInRole(WindowsBuiltInRole.Administrator) });
            Write(output, "workspace-before.json", Snapshot(session.Workspace));
            using var process = new Process { StartInfo = start };
            process.Start(); observation.Start(process.Id);
            var protocol = new ProtocolClient(process, caseName, session, endpoint);
            var outTask = protocol.CaptureAsync(); var errTask = process.StandardError.ReadToEndAsync();
            using var wrapperCancel = new CancellationTokenSource();
            var signal = caseName == "wrapper-cancel" ? Task.Run(async () =>
            {
                while (!wrapperCancel.IsCancellationRequested && !File.Exists(Path.Combine(session.Workspace, "active.pid.json")))
                    await Task.Delay(50);
                wrapperCancel.Cancel();
            }) : Task.CompletedTask;
            try { protocolResult = await protocol.RunAsync(wrapperCancel.Token).WaitAsync(TimeSpan.FromSeconds(110)); }
            catch (OperationCanceledException) when (caseName == "wrapper-cancel" && wrapperCancel.IsCancellationRequested)
            {
                protocolResult = await protocol.StopAfterWrapperCancellationAsync();
            }
            catch (Exception e) { errors.Add("PROTOCOL:" + e.GetType().Name); File.WriteAllText(Path.Combine(output, "exception.txt"), e.ToString()); }
            finally
            {
                wrapperCancel.Cancel(); await signal;
                process.StandardInput.Close(); // Official EOF shutdown, before fallback kill.
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (TimeoutException) { forcedKill = true; process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                try { await outTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) { errors.Add("CAPTURE:" + e.GetType().Name); }
                try { stderr = await errTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { errors.Add("STDERR_OPEN_AFTER_EXIT"); }
                stdout = protocol.RawOutput;
            }
            exit = process.ExitCode;
            children = await ChildLifetime.CheckAndRecoverAsync(session.Workspace, session.Id.ToString("N"), started);
            Write(output, "workspace-after.json", Snapshot(session.Workspace));
            Write(output, "session-files.json", Snapshot(session.Root));
        }
        catch (Exception e) { errors.Add(e.GetType().Name); File.WriteAllText(Path.Combine(output, "exception.txt"), e.ToString()); }
        finally
        {
            if (children == null)
            {
                try { children = await ChildLifetime.CheckAndRecoverAsync(session.Workspace, session.Id.ToString("N"), started); }
                catch (Exception e) { errors.Add("CHILD_RECOVERY:" + e.GetType().Name); }
            }
            observation.Stop();
            File.WriteAllText(Path.Combine(output, "stdout.txt"), stdout); File.WriteAllText(Path.Combine(output, "stderr.txt"), stderr);
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath)) registryAbsentBeforeRecovery = key == null;
            // Only this invocation's GUID canary. Recovery does not count as tool success.
            if (!registryAbsentBeforeRecovery) Registry.CurrentUser.DeleteSubKeyTree(registryPath, false);
            session.Dispose(); cleanup = new SessionCleaner(tempBase).Clean(session.Root);
            if (parent != null)
            {
                File.Delete(Path.Combine(parent, ".env"));
                try { Directory.Delete(tempBase); Directory.Delete(parent); } catch (IOException) { errors.Add("PARENT_RESIDUE"); }
            }
        }
        using var finalKey = Registry.CurrentUser.OpenSubKey(registryPath);
        Write(output, "result.json", new { case_name = caseName, runtime_exit = exit, forced_kill = forcedKill,
            protocol_result = protocolResult, children, errors, cleanup = cleanup.ToString(),
            session_absent = !Directory.Exists(session.Root), registry_absent_before_recovery = registryAbsentBeforeRecovery,
            registry_absent_final = finalKey == null, observation = observation.Result(), real_api_used = false, production_enabled = false });
        Console.WriteLine(JsonSerializer.Serialize(new { case_name = caseName, runtime_exit = exit, cleanup = cleanup.ToString(), errors = errors.Count }));
        return cleanup == CleanupResult.Complete && errors.Count == 0 ? 0 : 1;
    }

    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static void Write(string output, string file, object value) => File.WriteAllText(Path.Combine(output, file), JsonSerializer.Serialize(value));
    private static void WriteProvider(DiagnosticSession session, string endpoint, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(session.ProviderFile)!);
        PrivateFiles.WriteNew(session.ProviderFile, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, config = new {
            providerConfigRules = new { providerRules = new[] { new { providerId = "rdt-fixture", providerName = "RDT Fixture", config = new {
                group = "standard-personal", access = new { type = "api-key", apiKey = "rdt-qualification-invalid-local-fixture" },
                api = new { type = "openai-chat-completions", baseUrl = endpoint }, personalModelIds = new[] { model } } } } },
            modelConfigRules = new { providerModelRules = Array.Empty<object>(), manualProviderModelRules = Array.Empty<object>() },
            defaultModelSelection = new { providerId = "rdt-fixture", modelId = model } } }));
    }
    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>();
        void Visit(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("SNAPSHOT_REPARSE");
            if (Directory.Exists(path)) foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Visit(entry);
            else if (Path.GetFileName(path) != ".lease") result[Path.GetRelativePath(root, path)] = Hash(path);
        }
        Visit(root); return result;
    }
    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Rdt.slnx"))) return dir.FullName;
        throw new IOException("QUALIFICATION_REPO_REQUIRED");
    }
}
