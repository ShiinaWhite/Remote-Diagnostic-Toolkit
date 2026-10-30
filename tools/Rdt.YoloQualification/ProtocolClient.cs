using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rdt.Session;

namespace Rdt.Qualification;

// Fixed official NDJSON methods. Tasks are sent in frames, never argv.
internal sealed class ProtocolClient(Process process, string caseName, DiagnosticSession session, Uri fixture)
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private int nextId;
    private readonly StringBuilder output = new();
    internal string RawOutput => output.ToString();
    internal int ReverseRequestsRejected { get; private set; }

    internal async Task CaptureAsync()
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            if (output.Length > 24_000_000) throw new IOException("PROTOCOL_OUTPUT_LIMIT");
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var frame = json.RootElement;
            if (!frame.TryGetProperty("id", out var id)) continue;
            if (frame.TryGetProperty("method", out _))
            {
                ReverseRequestsRejected++;
                await WriteAsync(new { id = id.Clone(), error = new { code = -32601,
                    message = "Qualification does not grant interactive approvals or external host services" } });
            }
            else if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && pending.TryRemove(number, out var completion))
                completion.TrySetResult(frame.Clone());
        }
    }

    private async Task WriteAsync(object frame)
    {
        await writer.WaitAsync();
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(frame)); await process.StandardInput.FlushAsync(); }
        finally { writer.Release(); }
    }

    internal async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        await WriteAsync(new { id, method, @params = parameters });
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (result.TryGetProperty("error", out var error)) throw new IOException("PROTOCOL_REJECTED:" + method + ":" + error.GetRawText());
        return result;
    }

    internal async Task<object> StopAfterWrapperCancellationAsync()
    {
        var id = "rdt-yolo-" + session.Id.ToString("N");
        var watch = Stopwatch.StartNew();
        await RequestAsync("session/stop", new { sessionId = id });
        return new { completed = false, cancellation = "wrapper-cancel", wrapper_token_cancelled = true,
            target_observed = File.Exists(Path.Combine(session.Workspace, "active.pid.json")), stop_ack = true,
            stop_ms = watch.ElapsedMilliseconds, state = await RequestAsync("session/read", new { sessionId = id }) };
    }

    internal async Task<object> RunAsync(CancellationToken wrapperCancel)
    {
        var id = "rdt-yolo-" + session.Id.ToString("N");
        var created = await RequestAsync("session/create", new
        {
            sessionId = id, workspace = new { workspacePath = session.Workspace, workspaceKey = id },
            mode = "yolo", dynamicWorkflowEnabled = false, offPeakToolEnabled = false, titleGenerationEnabled = false
            // No toolAllowlist / toolDenylist. Normal YOLO core capabilities stay registered.
        });
        var initial = await RequestAsync("session/read", new { sessionId = id });
        var sent = await RequestAsync("session/send", new { sessionId = id,
            content = "RDT YOLO qualification synthetic scenario " + caseName + ". Execute the controlled fixture tool requests." });
        if (caseName is "model-cancel" or "tool-stop" or "wrapper-cancel")
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var ready = false;
            for (var i = 0; i < 200; i++)
            {
                if (caseName == "model-cancel")
                {
                    using var response = JsonDocument.Parse(await client.GetStringAsync(
                        fixture.GetLeftPart(UriPartial.Authority) + "/status/" + caseName));
                    ready = response.RootElement.GetProperty("requests").GetInt32() > 0;
                }
                else ready = File.Exists(Path.Combine(session.Workspace, "active.pid.json"));
                if (ready) break;
                await Task.Delay(100);
            }
            if (!ready) throw new IOException("CANCELLATION_TARGET_NOT_OBSERVED");
            if (caseName == "wrapper-cancel")
                await Task.Delay(Timeout.Infinite, wrapperCancel); // Wrapper signal handled by its finally/stop path.
            var watch = Stopwatch.StartNew();
            var stopped = await RequestAsync("session/stop", new { sessionId = id });
            var read = await RequestAsync("session/read", new { sessionId = id });
            return new { completed = false, cancellation = caseName, target_observed = true,
                stop_ack = true, stop_ms = watch.ElapsedMilliseconds, state = read,
                create_response = created, initial_state = initial, rejected_reverse_requests = ReverseRequestsRejected };
        }
        for (var i = 0; i < 240; i++)
        {
            await Task.Delay(250);
            var read = await RequestAsync("session/read", new { sessionId = id });
            if (read.GetRawText().Contains("RDT_YOLO_DONE", StringComparison.Ordinal))
            {
                if (caseName == "local-builtin-idle") await Task.Delay(TimeSpan.FromSeconds(66));
                return new { completed = true, create_response = created, initial_state = initial, final_state = read,
                    rejected_reverse_requests = ReverseRequestsRejected };
            }
        }
        throw new TimeoutException("YOLO_TURN_DID_NOT_COMPLETE");
    }
}
