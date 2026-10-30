using System.Collections.Concurrent;
using System.Text.Json;
using Rdt.Common;

namespace Rdt.Zcode;

// Fixed upstream packages/shared/src/zcode-protocol/index.ts at RdtInfo.UpstreamCommit.
// This class owns the reader and request correlation, never a transcript or logger.
public sealed class ZcodeProtocolClient : IAsyncDisposable
{
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly string sessionId;
    private readonly Action<string>? unknownMethod;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private Task? reader;
    private Exception? terminalError;
    private int nextId;
    private readonly TimeSpan requestTimeout;
    public Task Completion => reader ?? Task.CompletedTask;

    public ZcodeProtocolClient(TextReader input, TextWriter output, string sessionId,
        Action<string>? unknownMethod = null) : this(input, output, sessionId, unknownMethod, TimeSpan.FromSeconds(30)) { }

    internal ZcodeProtocolClient(TextReader input, TextWriter output, string sessionId,
        Action<string>? unknownMethod, TimeSpan requestTimeout)
    {
        this.input = input; this.output = output; this.sessionId = sessionId;
        this.unknownMethod = unknownMethod; this.requestTimeout = requestTimeout;
    }

    public void Start()
    {
        if (reader != null) throw new InvalidOperationException();
        reader = CaptureAsync();
    }

    private async Task CaptureAsync()
    {
        try
        {
            // Bound one frame without accumulating a lifetime transcript. Full snapshots
            // in this upstream can exceed 15 MB, so do not use a tiny NDJSON line limit.
            var buffer = new char[8192];
            var line = new System.Text.StringBuilder();
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), shutdown.Token);
                if (count == 0)
                {
                    if (line.Length != 0) throw new RdtException("PROTOCOL_MALFORMED");
                    throw new RdtException("RUNTIME_EOF");
                }
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (line.Length == 0) throw new RdtException("PROTOCOL_MALFORMED");
                        using var json = JsonDocument.Parse(line.ToString());
                        line.Clear();
                        await HandleFrameAsync(json.RootElement);
                    }
                    else
                    {
                        if (line.Length >= 32_000_000) throw new RdtException("PROTOCOL_FRAME_TOO_LARGE");
                        line.Append(buffer[i]);
                    }
                }
            }
        }
        catch (Exception error)
        {
            // Never propagate JSON snippets, decoder bytes or upstream error messages.
            var safe = error is RdtException ? error : new RdtException("PROTOCOL_MALFORMED");
            Interlocked.CompareExchange(ref terminalError, safe, null);
            foreach (var (id, completion) in pending)
                if (pending.TryRemove(id, out _)) completion.TrySetException(safe);
        }
    }

    private async Task HandleFrameAsync(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object) throw new RdtException("PROTOCOL_MALFORMED");
        if (frame.TryGetProperty("method", out var method))
        {
            if (method.ValueKind != JsonValueKind.String) throw new RdtException("PROTOCOL_MALFORMED");
            if (!frame.TryGetProperty("id", out var reverseId)) return; // Unsubscribed notifications; snapshots own turn state.
            if (reverseId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                throw new RdtException("PROTOCOL_MALFORMED");
            var name = method.GetString()!;
            if (name == "session/requestRuntimePreferences")
            {
                if (!ValidRuntimePreferences(frame))
                {
                    await WriteAsync(new { id = reverseId.Clone(), error = new { code = -32602, message = "Invalid runtime preference scope" } }, shutdown.Token);
                    return;
                }
                await WriteAsync(new { id = reverseId.Clone(), result = new {
                    nativeSearchEnhancementsEnabled = false, toolSearchMode = "off", memoryEnabled = false
                } }, shutdown.Token);
            }
            else
            {
                // Only the method name reaches the terminal callback; never params.
                unknownMethod?.Invoke(name);
                await WriteAsync(new { id = reverseId.Clone(), error = new { code = -32601, message = "Unsupported host method" } }, shutdown.Token);
            }
            return;
        }
        if (!frame.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) ||
            (frame.TryGetProperty("result", out _) == frame.TryGetProperty("error", out _)))
            throw new RdtException("PROTOCOL_MALFORMED");
        if (pending.TryRemove(number, out var completion)) completion.TrySetResult(frame.Clone());
        // A late response to a cancelled/timed-out request is harmless.
    }

    private bool ValidRuntimePreferences(JsonElement frame)
    {
        if (!frame.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object ||
            p.EnumerateObject().Count() != 2 || !p.TryGetProperty("sessionId", out var id) ||
            id.ValueKind != JsonValueKind.String || id.GetString() != sessionId ||
            !p.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String) return false;
        return scope.GetString() is "runtime-materialization" or "user-execution";
    }

    private async Task WriteAsync(object frame, CancellationToken token)
    {
        await writer.WaitAsync(token);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(frame).AsMemory(), token);
            await output.FlushAsync(token);
        }
        finally { writer.Release(); }
    }

    internal async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token = default)
    {
        if (reader == null) throw new InvalidOperationException("Protocol not started");
        if (terminalError is { } failure) throw failure;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        limit.CancelAfter(requestTimeout);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            if (terminalError is { } stopped) throw stopped;
            await WriteAsync(new { id, method, @params = parameters }, limit.Token);
            var frame = await completion.Task.WaitAsync(limit.Token);
            if (frame.TryGetProperty("error", out _)) throw new RdtException(method switch {
                "session/create" => "SESSION_CREATE_FAILED", "session/send" => "SESSION_SEND_FAILED", _ => "PROTOCOL_ERROR"
            });
            return frame.GetProperty("result").Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested)
        { throw new RdtException("PROTOCOL_TIMEOUT"); }
        catch (IOException) { throw terminalError ?? new RdtException("RUNTIME_EOF"); }
        finally { pending.TryRemove(id, out _); }
    }

    public Task<JsonElement> CreateSessionAsync(string workspace, CancellationToken token = default) => RequestAsync("session/create", new {
        sessionId, workspace = new { workspacePath = workspace, workspaceKey = sessionId }, mode = "yolo",
        dynamicWorkflowEnabled = false, offPeakToolEnabled = false, titleGenerationEnabled = false
    }, token);
    public Task<JsonElement> SendTaskAsync(string content, CancellationToken token = default) =>
        RequestAsync("session/send", new { sessionId, content }, token);
    public Task<JsonElement> ReadSessionAsync(CancellationToken token = default) => RequestAsync("session/read", new { sessionId }, token);
    public Task<JsonElement> StopSessionAsync(CancellationToken token = default) => RequestAsync("session/stop", new { sessionId }, token);

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        if (reader != null) await reader;
        shutdown.Dispose(); writer.Dispose();
    }
}
