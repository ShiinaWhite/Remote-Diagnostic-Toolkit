using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Session;

namespace Rdt.Qualification;

// No production reference: fixed candidate, fixed synthetic cases, loopback only.
internal static class Program
{
    private static readonly HashSet<string> Cases = new(("normal detached-child detached-stop detached-wrapper-cancel " +
        "session-stop model-cancel tool-cancel wrapper-cancel tool-failure runtime-error wrapper-cleanup wrapper-killed " +
        "startup-race nested-job inject-create-failure inject-config-failure inject-assign-failure unicode-ps51 unicode-ps7 write edit").Split(' '));
    internal static string Repo
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Rdt.slnx"))) return d.FullName;
            throw new IOException("QUALIFICATION_REPO_REQUIRED");
        }
    }
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (args is ["self-test"]) return await NativeSelfTests.RunAsync();
        if (args is ["recover-killed-wrapper"]) return RecoverKilled();
        if (args.Length != 4 || !Cases.Contains(args[1])) return 2;
        using var identity = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Repo, "scripts", "runtime-qualification", "yolo-runtime-identity.json")));
        var pin = identity.RootElement;
        var candidate = Path.GetFullPath(args[0]); var caseName = args[1];
        if (!pin.GetProperty("qualification_only").GetBoolean() || pin.GetProperty("production_enabled").GetBoolean()
            || Path.GetFileName(candidate) != pin.GetProperty("runtime_filename").GetString()
            || Hash(candidate) != pin.GetProperty("runtime_sha256").GetString()
            || Hash(args[3]) != pin.GetProperty("builtin_sha256").GetString()) return 3;
        if (!Uri.TryCreate(args[2], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1"
            || endpoint.AbsolutePath != "/v1" || endpoint.Port <= 0 || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) return 4;
        var output = Path.Combine(Repo, "artifacts", "runtime-qualification", "lifecycle", "runs", caseName);
        Directory.CreateDirectory(output);
        using var session = DiagnosticSession.Create();
        var started = DateTime.UtcNow;
        LifetimeJob? job = null, outer = null;
        JobObservation? audit = null;
        using var process = new Process();
        var processStarted = false; var assigned = false; var createdJob = false; var rootMember = false;
        var wrapperInJob = LifetimeJob.InAnyJob(Process.GetCurrentProcess());
        var preassignInJob = false; var assignMs = 0d; int[] preassignChildren = [];
        var startupHandles = new List<Process>();
        object[] startupRecords = []; var startupExitedWithoutRecovery = true;
        object? protocolResult = null, afterClose = null, recovery = null;
        object[] liveBeforeClose = [];
        var stdout = ""; var stderr = ""; var errors = new List<string>(); var exit = -1;
        var protocolRequests = 0; var unassignedStopped = false; var jobClosed = false;
        int? nativeErrorCode = null;
        var cleanup = CleanupResult.Failed;
        try
        {
            Directory.CreateDirectory(Path.Combine(session.Home, ".zcode", "cli"));
            PrivateFiles.WriteNew(Path.Combine(session.Home, ".zcode", "cli", "config.json"), "{\"permission\":{\"mode\":\"yolo\"}}"u8);
            PrivateFiles.WriteNew(Path.Combine(session.Workspace, "fixture.txt"), "RDT_YOLO_SENTINEL\n"u8);
            PrivateFiles.WriteNew(Path.Combine(session.Workspace, "edit-target.txt"), "RDT_BEFORE_EDIT\n"u8);
            var builtin = Path.Combine(session.Data, "zcode-builtin.json");
            PrivateFiles.WriteNew(builtin, File.ReadAllBytes(args[3]));
            WriteProvider(session, endpoint.AbsoluteUri, "rdt-yolo-" + caseName);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var ps = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var ps7 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            var start = new ProcessStartInfo(candidate) { WorkingDirectory = session.Workspace, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
            foreach (var arg in new[] { "agent-server", "--stdio", "--cwd", session.Workspace }) start.ArgumentList.Add(arg);
            start.Environment.Clear();
            foreach (var (key, value) in session.EnvironmentVariables()) start.Environment[key] = value;
            start.Environment["PATH"] = string.Join(';', Path.Combine(windows, "System32"), windows, Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0"));
            start.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD"; start.Environment["ComSpec"] = Path.Combine(windows, "System32", "cmd.exe");
            start.Environment["ZCODE_BUILTIN_PROVIDER_CONFIG_FILE"] = builtin;
            if (caseName == "nested-job")
            {
                // A controlled existing Job around THIS developer wrapper; no kill/resource/UI limits.
                outer = new LifetimeJob(nestedTestParent: true); outer.Assign(Process.GetCurrentProcess());
                wrapperInJob = LifetimeJob.InAnyJob(Process.GetCurrentProcess());
            }
            var fault = caseName.StartsWith("inject-", StringComparison.Ordinal) ? caseName.Split('-')[1] : null;
            job = new LifetimeJob(fault); createdJob = true;
            process.StartInfo = start;
            var clock = Stopwatch.StartNew(); process.Start(); processStarted = true;
            preassignInJob = LifetimeJob.InAnyJob(process);
            if (caseName == "startup-race") await Task.Delay(2000); // Stress a much wider pre-protocol window.
            preassignChildren = JobObservation.DescendantsBeforeAssign(process.Id);
            foreach (var pid in preassignChildren)
            {
                try { var child = Process.GetProcessById(pid); _ = child.SafeHandle; startupHandles.Add(child); }
                catch (ArgumentException) { }
            }
            job.Assign(process, fault == "assign"); assigned = true;
            assignMs = clock.Elapsed.TotalMilliseconds; rootMember = job.Contains(process.SafeHandle);
            startupRecords = startupHandles.Select(p => (object)new { pid = p.Id, start_ticks = p.StartTime.ToUniversalTime().Ticks, name = p.ProcessName,
                windows_console_host = string.Equals(p.MainModule?.FileName, Path.Combine(windows, "System32", "conhost.exe"), StringComparison.OrdinalIgnoreCase),
                member_after_root_assign = job.Contains(p.SafeHandle) }).ToArray();
            Write(output, "startup-processes.json", startupRecords);
            foreach (var child in startupHandles)
                if (!job.Contains(child.SafeHandle) && !string.Equals(child.MainModule?.FileName, Path.Combine(windows, "System32", "conhost.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("UNKNOWN_PREASSIGN_DESCENDANT_REQUIRES_REVIEW");
            Write(output, "launch.json", new { session = session.Root, session_id = session.Id.ToString("N"), created_utc = new SessionPaths(Path.GetTempPath()).ReadMarker(session.Root)!.CreatedUtc,
                workspace = session.Workspace, powershell = ps, powershell7 = ps7, powershell7_exists = File.Exists(ps7),
                runtime_pid = process.Id, runtime_start_ticks = process.StartTime.ToUniversalTime().Ticks,
                args = start.ArgumentList.ToArray(), environment = start.Environment.ToDictionary(p => p.Key, p => p.Value) });
            audit = new JobObservation(job, session.Workspace, session.Id.ToString("N")); audit.Sample();
            var client = new ProtocolClient(process, caseName, session, endpoint);
            var capture = client.CaptureAsync(); var err = process.StandardError.ReadToEndAsync();
            using var wrapperCancellation = new CancellationTokenSource();
            JsonElement? initialState = null;
            var cancellationSignal = caseName is "wrapper-cancel" or "detached-wrapper-cancel" ? Task.Run(async () =>
            {
                var marker = Path.Combine(session.Workspace, caseName == "detached-wrapper-cancel" ? "hold.pid.json" : "active.pid.json");
                while (!wrapperCancellation.IsCancellationRequested && !File.Exists(marker)) await Task.Delay(30);
                wrapperCancellation.Cancel();
            }) : Task.CompletedTask;
            try
            {
                async Task<JsonElement> Send(string method, object parameters)
                {
                    if (!assigned || !rootMember) throw new IOException("PROTOCOL_REQUIRES_BOUND_JOB");
                    protocolRequests++; return await client.RequestAsync(method, parameters);
                }
                try
                {
                    protocolResult = await ExerciseAsync(caseName, session, endpoint, process, audit, Send,
                        state => initialState = state, wrapperCancellation.Token).WaitAsync(TimeSpan.FromSeconds(95));
                }
                catch (OperationCanceledException) when (wrapperCancellation.IsCancellationRequested && caseName is "wrapper-cancel" or "detached-wrapper-cancel")
                {
                    audit.Sample(); // Prove membership while the tool is still alive, before stopping it.
                    await Send("session/stop", new { sessionId = "rdt-lifecycle-" + session.Id.ToString("N") });
                    protocolResult = new { stop_ack = true, wrapper_token_cancelled = true, initial_state = initialState };
                }
                audit.Sample(); liveBeforeClose = audit.LiveBeforeClose();
                if (caseName == "wrapper-killed")
                {
                    Write(output, "wrapper-kill-ready.json", new { memberships = audit.Memberships(), live = liveBeforeClose,
                        created_job = createdJob, assigned, runtime_member = rootMember, flags = job.Flags, inheritable = job.Inheritable,
                        startup_processes = startupRecords, preassign_descendants = preassignChildren, errors = audit.Errors,
                        protocol_result = protocolResult });
                    await Task.Delay(TimeSpan.FromSeconds(60)); throw new TimeoutException("DRIVER_DID_NOT_KILL_WRAPPER");
                }
            }
            finally
            {
                wrapperCancellation.Cancel(); await cancellationSignal;
                if (caseName == "wrapper-cleanup") { audit.Sample(); liveBeforeClose = audit.LiveBeforeClose(); job.Dispose(); jobClosed = true; }
                try { process.StandardInput.Close(); } catch (IOException) when (process.HasExited) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (TimeoutException) { errors.Add("EOF_TIMEOUT"); job.Terminate(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                if (!jobClosed) { audit.Sample(); liveBeforeClose = audit.LiveBeforeClose(); job.Dispose(); jobClosed = true; }
                afterClose = await audit.WaitAfterCloseAsync();
                try { await capture.WaitAsync(TimeSpan.FromSeconds(5)); stdout = client.RawOutput; stderr = await err.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception e) { errors.Add("CAPTURE:" + e.GetType().Name); }
                exit = process.ExitCode;
            }
            Write(output, "workspace-after.json", Snapshot(session.Workspace));
        }
        catch (Exception e)
        {
            if (e is System.ComponentModel.Win32Exception native) nativeErrorCode = native.NativeErrorCode;
            errors.Add(e.Message.StartsWith("INJECTED_", StringComparison.Ordinal) ? e.Message : e.GetType().Name);
            File.WriteAllText(Path.Combine(output, "exception.txt"), e.ToString());
            if (processStarted && !assigned && !process.HasExited)
            {
                process.Kill(entireProcessTree: true); // Failed admission only; never a successful lifecycle guarantee.
                await process.WaitForExitAsync(); unassignedStopped = true;
            }
        }
        finally
        {
            if (job != null && !jobClosed) { job.Dispose(); jobClosed = true; }
            if (processStarted && assigned && !process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (audit != null)
            {
                afterClose ??= await audit.WaitAfterCloseAsync(); errors.AddRange(audit.Errors);
            }
            recovery = await ChildLifetime.CheckAndRecoverAsync(session.Workspace, session.Id.ToString("N"), started);
            var startupResults = new List<object>();
            foreach (var p in startupHandles)
            {
                var alive = false;
                try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { alive = true; startupExitedWithoutRecovery = false; errors.Add("PREASSIGN_CHILD_SURVIVED_JOB_CLOSE"); p.Kill(); await p.WaitForExitAsync(); }
                startupResults.Add(new { pid = p.Id, escaped = alive }); p.Dispose();
            }
            Write(output, "startup-after-close.json", startupResults);
            File.WriteAllText(Path.Combine(output, "stdout.txt"), stdout); File.WriteAllText(Path.Combine(output, "stderr.txt"), stderr);
            session.Dispose(); cleanup = new SessionCleaner().Clean(session.Root);
            outer?.Dispose();
        }
        Write(output, "result.json", new { case_name = caseName, created_job = createdJob, assigned, root_member = rootMember,
            wrapper_in_job = wrapperInJob, runtime_preassign_in_job = preassignInJob, assign_ms = assignMs,
            preassign_descendants = preassignChildren, startup_processes = startupRecords, startup_exited_without_recovery = startupExitedWithoutRecovery,
            protocol_requests = protocolRequests, unassigned_stopped = unassignedStopped,
            job_closed = jobClosed, limit_flags = job?.Flags, handle_inheritable = job?.Inheritable,
            memberships = audit?.Memberships(), live_before_close = liveBeforeClose, after_close = afterClose, recovery,
            protocol_result = protocolResult, errors, native_error_code = nativeErrorCode, runtime_exit = exit, cleanup = cleanup.ToString(), session_absent = !Directory.Exists(session.Root) });
        audit?.Dispose();
        Console.WriteLine(JsonSerializer.Serialize(new { case_name = caseName, cleanup = cleanup.ToString(), errors = errors.Count }));
        return errors.Count == 0 && cleanup == CleanupResult.Complete ? 0 : 1;
    }

    private static async Task<object> ExerciseAsync(string name, DiagnosticSession session, Uri fixture, Process process,
        JobObservation audit, Func<string, object, Task<JsonElement>> send, Action<JsonElement> initialSeen, CancellationToken cancellationToken)
    {
        if (name == "startup-race") return new { startup_without_protocol = true };
        var id = "rdt-lifecycle-" + session.Id.ToString("N");
        await send("session/create", new { sessionId = id, workspace = new { workspacePath = session.Workspace, workspaceKey = id },
            mode = "yolo", dynamicWorkflowEnabled = false, offPeakToolEnabled = false, titleGenerationEnabled = false });
        var initial = await send("session/read", new { sessionId = id });
        initialSeen(initial);
        await send("session/send", new { sessionId = id, content = "Controlled RDT lifecycle/UTF-8 qualification fixture " + name });
        using var http = new HttpClient();
        for (var i = 0; i < 450; i++)
        {
            audit.Sample();
            cancellationToken.ThrowIfCancellationRequested();
            if (name == "runtime-error" && audit.HasMarker("active.pid.json") && audit.HasMarker("detached.pid.json"))
            {
                process.Kill(); // Controlled root-only fault injection, NOT descendant cleanup.
                await process.WaitForExitAsync();
                return new { expected_runtime_fault = true, runtime_fault_injected = true, initial_state = initial };
            }
            if (name == "runtime-error" && process.HasExited) return new { expected_runtime_fault = true, initial_state = initial };
            var target = name == "model-cancel"
                ? JsonDocument.Parse(await http.GetStringAsync(fixture.GetLeftPart(UriPartial.Authority) + "/status/" + name)).RootElement.GetProperty("requests").GetInt32() > 0
                : audit.HasMarker("active.pid.json");
            if (target && name is "tool-cancel" or "wrapper-cleanup" or "model-cancel")
            {
                if (name == "wrapper-cleanup") return new { explicit_wrapper_cleanup = true, initial_state = initial };
                await send("session/stop", new { sessionId = id });
                return new { stop_ack = true, initial_state = initial };
            }
            var readTask = send("session/read", new { sessionId = id });
            if (name == "runtime-error" && await Task.WhenAny(readTask, process.WaitForExitAsync()) != readTask)
                return new { expected_runtime_fault = true, initial_state = initial };
            var state = await readTask.WaitAsync(cancellationToken);
            if (state.GetRawText().Contains("RDT_YOLO_DONE", StringComparison.Ordinal))
            {
                var stop = name is "session-stop" or "detached-stop";
                if (stop) await send("session/stop", new { sessionId = id });
                return new { completed = true, stop_ack = stop, initial_state = initial, final_state = state };
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("LIFECYCLE_TURN_TIMEOUT");
    }
    private static int RecoverKilled()
    {
        var output = Path.Combine(Repo, "artifacts", "runtime-qualification", "lifecycle", "runs", "wrapper-killed");
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "launch.json")));
        var root = json.RootElement.GetProperty("session").GetString()!;
        var marker = new SessionPaths(Path.GetTempPath()).ReadMarker(root);
        if (marker == null || marker.Id.ToString("N") != json.RootElement.GetProperty("session_id").GetString()
            || marker.CreatedUtc != json.RootElement.GetProperty("created_utc").GetDateTimeOffset()) return 3;
        var result = new SessionCleaner().Clean(root);
        Write(output, "parent-cleanup.json", new { cleanup = result.ToString(), session_absent = !Directory.Exists(root) });
        return result == CleanupResult.Complete ? 0 : 1;
    }
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    internal static void Write(string output, string name, object value)
    {
        var file = Path.Combine(output, name);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(value));
        File.Move(file + ".tmp", file, overwrite: true); // Driver never reads a partial readiness manifest.
    }
    private static Dictionary<string, string> Snapshot(string root)
    {
        SessionPaths.ValidateTree(root);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(root, p), Hash);
    }
    private static void WriteProvider(DiagnosticSession s, string endpoint, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(s.ProviderFile)!);
        PrivateFiles.WriteNew(s.ProviderFile, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, config = new {
            providerConfigRules = new { providerRules = new[] { new { providerId = "rdt-fixture", providerName = "RDT Fixture", config = new {
                group = "standard-personal", access = new { type = "api-key", apiKey = "rdt-qualification-invalid-local-fixture" },
                api = new { type = "openai-chat-completions", baseUrl = endpoint }, personalModelIds = new[] { model } } } } },
            modelConfigRules = new { providerModelRules = Array.Empty<object>(), manualProviderModelRules = Array.Empty<object>() },
            defaultModelSelection = new { providerId = "rdt-fixture", modelId = model } } }));
    }
}
