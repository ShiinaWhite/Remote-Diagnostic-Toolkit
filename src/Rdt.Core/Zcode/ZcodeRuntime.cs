using System.Diagnostics;
using System.Text;
using Rdt.Common;
using Rdt.Providers;
using Rdt.Reporting;
using Rdt.Runtime;
using Rdt.Runtime.Windows;
using Rdt.Session;

namespace Rdt.Zcode;

public sealed class ZcodeRuntime : IZcodeRuntime
{
    public const string RuntimeContract = """
        [RDT runtime contract]
        When using Windows PowerShell where non-ASCII output may occur, initialize
        console input/output encoding as UTF-8 before collecting output:
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $OutputEncoding = [Console]::OutputEncoding
        Use explicit UTF-8 when reading or writing text files.
        [/RDT runtime contract]

        """;

    private readonly GProviderOptions options;
    private readonly Func<CancellationToken, Task<string>> readTask;
    private readonly Action<string> progress;
    private readonly RuntimeServices services;
    public bool IsMock => false;
    public bool JobMembersExited { get; private set; }

    public ZcodeRuntime() : this(new GProviderOptions("https://example.invalid/v1", "unconfigured"),
        _ => throw new RdtException("TASK_REQUIRED"), _ => { }) { }
    public ZcodeRuntime(GProviderOptions options, Func<CancellationToken, Task<string>> readTask, Action<string> progress)
        : this(options, readTask, progress, new RuntimeServices()) { }
    internal ZcodeRuntime(GProviderOptions options, Func<CancellationToken, Task<string>> readTask,
        Action<string> progress, RuntimeServices services)
    { this.options = options; this.readTask = readTask; this.progress = progress; this.services = services; }

    public Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken) =>
        RunWithSecretAsync(session, [], started, cancellationToken);

    internal async Task<RuntimeResult> RunWithSecretAsync(DiagnosticSession session, byte[] secret,
        Action started, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new RdtException("WINDOWS_REQUIRED");
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        using var image = services.VerifyImage();
        services.StageBuiltin(session);
        using var builtin = RuntimeIdentity.OpenVerified(session.BuiltinFile, RuntimeIdentity.BuiltinSha256,
            "BUILTIN_MISSING", "BUILTIN_HASH_MISMATCH");
        using var redactor = new SecretRedactor(secret);
        void Show(string text) => progress(redactor.Redact(text));
        using var job = services.CreateJob();
        using var process = new Process { StartInfo = services.StartInfo(session) };
        var processStarted = false;
        var assigned = false;
        ZcodeProtocolClient? protocol = null;
        StreamReader? stdoutReader = null, stderrReader = null;
        Task? stderr = null;
        var id = "rdt-" + session.Id.ToString("N");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(services.SessionTimeout);
        try
        {
            process.Start(); processStarted = true;
            job.Assign(process); assigned = true;
            // Assignment verifies IsProcessInJob before the protocol reader or requests start.
            started();
            // Process's convenience readers detect BOMs and may silently switch encodings.
            // Wrap the raw streams explicitly to enforce the strict UTF-8 wire contract.
            stdoutReader = new StreamReader(process.StandardOutput.BaseStream, new UTF8Encoding(false, true), false);
            stderrReader = new StreamReader(process.StandardError.BaseStream, new UTF8Encoding(false, true), false);
            stderr = DrainAsync(stderrReader, deadline.Token);
            protocol = new ZcodeProtocolClient(stdoutReader, process.StandardInput, id,
                method => Show("[Protocol] Unsupported host method: " + SafeMethodName(method)));
            protocol.Start();
            await protocol.CreateSessionAsync(session.Workspace, deadline.Token);
            ZcodeSessionState.VerifySelection(await protocol.ReadSessionAsync(deadline.Token), options.ModelId);
            Show("Provider: G"); Show("Mode: YOLO"); Show("Runtime: READY"); Show("Session: TEMP");
            var task = await readTask(deadline.Token);
            if (string.IsNullOrWhiteSpace(task)) throw new RdtException("TASK_REQUIRED");
            Show("[Agent] Starting...");
            await protocol.SendTaskAsync(RuntimeContract + "\n" + task, deadline.Token);
            var seenTools = new HashSet<string>(StringComparer.Ordinal);
            var heartbeat = Stopwatch.StartNew();
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (stderr.IsFaulted) await stderr;
                var snapshot = await protocol.ReadSessionAsync(deadline.Token);
                ZcodeSessionState.VerifySelection(snapshot, options.ModelId);
                services.ObserveSnapshot?.Invoke(snapshot);
                var final = ZcodeSessionState.Observe(snapshot, seenTools, Show);
                if (final != null)
                {
                    Show("[Agent] Completed"); Show(final);
                    return new RuntimeResult(0, false);
                }
                if (heartbeat.Elapsed > TimeSpan.FromSeconds(10)) { Show("[Agent] Working..."); heartbeat.Restart(); }
                await Task.Delay(400, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new RdtException("RUNTIME_TIMEOUT"); }
        catch (DecoderFallbackException) { throw new RdtException("PROTOCOL_MALFORMED"); }
        finally
        {
            try
            {
                if (processStarted)
                {
                    if (protocol != null && !process.HasExited)
                    {
                        using var stop = new CancellationTokenSource(services.StopTimeout);
                        try { await protocol.StopSessionAsync(stop.Token); } catch { /* Job remains owner. */ }
                    }
                    try { process.StandardInput.Close(); } catch (IOException) { }
                    if (assigned)
                    {
                        try { await process.WaitForExitAsync().WaitAsync(services.StopTimeout); }
                        catch (TimeoutException) { job.Terminate(); }
                        if (job.Members().Length != 0) job.Terminate();
                        var wait = Stopwatch.StartNew();
                        while (job.Members().Length != 0 && wait.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(30);
                        JobMembersExited = job.Members().Length == 0;
                        if (!JobMembersExited) throw new RdtException("JOB_CLEANUP_FAILED");
                    }
                    else if (!process.HasExited) process.Kill(); // Pre-protocol, unassigned root only.
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                }
            }
            finally
            {
                try { services.ObserveExit?.Invoke(session); }
                finally { job.Dispose(); } // Last handle is the kernel backstop even on observation failure.
                if (protocol != null) await protocol.DisposeAsync();
                if (stderr != null)
                {
                    var invalid = false;
                    try { await stderr; }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                    catch (IOException) { }
                    catch (DecoderFallbackException) { invalid = true; }
                    if (invalid)
                    {
                        stdoutReader?.Dispose(); stderrReader?.Dispose();
                        throw new RdtException("PROTOCOL_MALFORMED");
                    }
                }
                stdoutReader?.Dispose(); stderrReader?.Dispose();
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(DiagnosticSession session)
    {
        var start = new ProcessStartInfo(RuntimeIdentity.Executable) {
            WorkingDirectory = session.Workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        foreach (var arg in new[] { "agent-server", "--stdio", "--cwd", session.Workspace }) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var (key, value) in session.ProductionEnvironment()) start.Environment[key] = value;
        return start;
    }

    private static string SafeMethodName(string method) =>
        method.Length <= 128 && method.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.') ? method : "<invalid-method-name>";

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { } }
        finally { Array.Clear(buffer); }
    }
}

// Internal test dependencies cannot be selected through arguments, config or env.
internal sealed class RuntimeServices
{
    internal Func<FileStream> VerifyImage { get; init; } = () => RuntimeIdentity.OpenVerified(RuntimeIdentity.Executable,
        RuntimeIdentity.Sha256, "RUNTIME_MISSING", "RUNTIME_HASH_MISMATCH");
    internal Action<DiagnosticSession> StageBuiltin { get; init; } = RuntimeIdentity.StageBuiltin;
    internal Func<RuntimeJob> CreateJob { get; init; } = () => new RuntimeJob();
    internal Func<DiagnosticSession, ProcessStartInfo> StartInfo { get; init; } = ZcodeRuntime.CreateStartInfo;
    internal TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(15);
    internal TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(4);
    internal Action<System.Text.Json.JsonElement>? ObserveSnapshot { get; init; }
    internal Action<DiagnosticSession>? ObserveExit { get; init; }
}
