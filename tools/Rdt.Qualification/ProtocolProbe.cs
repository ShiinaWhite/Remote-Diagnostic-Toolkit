using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rdt.Session;
using Rdt.Zcode;

namespace Rdt.Qualification;

// Official fixed-version NDJSON methods only. No caller-supplied protocol commands.
internal sealed class ProtocolProbe(Process process, Uri fixtureEndpoint)
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);

    internal async Task<string> CaptureAsync()
    {
        var text = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            text.AppendLine(line);
            if (text.Length > 8_000_000) throw new IOException("PROTOCOL_OUTPUT_LIMIT");
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line); // Strict protocol stdout, never ignore non-JSON.
            var frame = json.RootElement;
            if (!frame.TryGetProperty("id", out var id)) continue;
            if (frame.TryGetProperty("method", out _))
            {
                await WriteAsync(new { id = id.Clone(), error = new { code = -32601,
                    message = "Qualification host grants no interactive approvals" } });
            }
            else if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number)
                && pending.TryRemove(number, out var completion)) completion.TrySetResult(frame.Clone());
        }
        return text.ToString();
    }

    private async Task WriteAsync(object frame)
    {
        await writer.WaitAsync();
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(frame)); await process.StandardInput.FlushAsync(); }
        finally { writer.Release(); }
    }

    private async Task<JsonElement> RequestAsync(int id, string method, object parameters)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        await WriteAsync(new { id, method, @params = parameters });
        var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (response.TryGetProperty("error", out _)) throw new IOException("PROTOCOL_REQUEST_REJECTED:" + method);
        return response;
    }

    internal async Task<object> ExerciseAsync(DiagnosticSession session, string caseName)
    {
        var id = "rdt-qualification-" + session.Id.ToString("N");
        var created = await RequestAsync(1, "session/create", new
        {
            sessionId = id,
            workspace = new { workspacePath = session.Workspace, workspaceKey = "rdt-fixture-" + session.Id.ToString("N") },
            mode = "plan", toolAllowlist = new[] { "Read", "Glob", "Grep" },
            toolDenylist = ReadOnlyPolicy.DisallowedTools.Split(' '),
            dynamicWorkflowEnabled = false, offPeakToolEnabled = false, titleGenerationEnabled = false
        });
        var sent = await RequestAsync(2, "session/send", new { sessionId = id,
            content = "RDT protocol synthetic fixture. Read fixture.txt only." });
        if (caseName == "protocol-cancel")
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var started = false;
            for (var i = 0; i < 20; i++)
            {
                var status = await client.GetStringAsync(fixtureEndpoint.GetLeftPart(UriPartial.Authority)
                    + "/qualification/status/protocol-cancel");
                using var json = JsonDocument.Parse(status);
                if (json.RootElement.GetProperty("requests").GetInt32() > 0) { started = true; break; }
                await Task.Delay(100);
            }
            if (!started) throw new IOException("IN_FLIGHT_FIXTURE_REQUEST_NOT_OBSERVED");
            var watch = Stopwatch.StartNew();
            var stopped = await RequestAsync(3, "session/stop", new { sessionId = id });
            return new { created = true, accepted = true, stop_acknowledged = true, in_flight_provider_seen = true,
                stop_ms = watch.ElapsedMilliseconds, stop_result = stopped, completed = false,
                prompt_in_command_line = false, runtime_mode = "plan" };
        }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(250);
            var read = await RequestAsync(3 + attempt, "session/read", new { sessionId = id });
            if (read.GetRawText().Contains("RDT_FIXTURE_DONE", StringComparison.Ordinal))
                return new { created = true, accepted = true, completed = true,
                    transport = caseName, prompt_in_command_line = false, runtime_mode = "plan" };
        }
        return new { created = true, accepted = true, completed = false,
            transport = caseName, prompt_in_command_line = false, runtime_mode = "plan" };
    }
}
