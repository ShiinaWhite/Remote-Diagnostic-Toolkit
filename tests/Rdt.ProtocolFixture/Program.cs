using System.Diagnostics;
using System.Text;
using System.Text.Json;

// A standalone test child, outside the production dependency graph. No real
// provider, credentials, arbitrary command entry or operator task in argv.
var scenario = args.Single();
var utf8 = new UTF8Encoding(false, true);
using var input = new StreamReader(Console.OpenStandardInput(), utf8, false);
using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var runtime = Environment.GetEnvironmentVariable("TEMP")!;
void Mark(string name, string content) => File.WriteAllText(Path.Combine(runtime, name), content, new UTF8Encoding(false));
async Task Reply(object frame) => await output.WriteLineAsync(JsonSerializer.Serialize(frame));
if (scenario == "early-exit") return 27;
if (scenario == "malformed") { await output.WriteLineAsync("{broken"); return 28; }
if (scenario == "stderr-invalid") { using var raw = Console.OpenStandardError(); raw.WriteByte(255); raw.Flush(); }
if (scenario == "stdout-invalid") { using var raw = Console.OpenStandardOutput(); raw.WriteByte(255); raw.WriteByte(254); raw.Flush(); return 28; }
var sent = false;
var stopped = false;
while (await input.ReadLineAsync() is { } line)
{
    using var json = JsonDocument.Parse(line);
    var frame = json.RootElement;
    if (!frame.TryGetProperty("method", out var methodValue)) continue;
    var method = methodValue.GetString();
    var id = frame.GetProperty("id").Clone();
    if (method == "session/create")
    {
        Mark("create-seen", "created");
        if (frame.GetProperty("params").GetProperty("mode").GetString() != "yolo") return 29;
        if (scenario == "create-failure")
        { await Reply(new { id, error = new { code = -1, message = "fixture-secret-must-not-persist" } }); continue; }
    }
    if (method == "session/send")
    {
        sent = true;
        if (scenario == "send-failure")
        { await Reply(new { id, error = new { code = -2, message = "fixture-secret-must-not-persist" } }); continue; }
        if (scenario == "detached")
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")) {
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!; Mark("child-pid", child.Id.ToString());
        }
    }
    if (method == "session/stop") { stopped = true; Mark("stop-seen", "stopped"); }
    var projection = new Dictionary<string, object> { ["status"] = "idle", ["activeToolCalls"] = Array.Empty<object>(), ["pendingPermissions"] = Array.Empty<object>() };
    object[] messages = [];
    if (sent && !stopped)
    {
        if (scenario is "hang" or "detached") projection["status"] = "busy";
        else if (scenario is "auth-error" or "network-error") projection["lastError"] = new {
            code = scenario == "auth-error" ? "401" : "ECONNREFUSED",
            message = scenario == "auth-error" ? "Unauthorized fixture-secret-must-not-persist" : "network fixture-secret-must-not-persist"
        };
        else messages = [new {
            info = new { role = "assistant", finish = "stop", time = new { completed = 1 } },
            parts = new object[] { new { type = "tool", tool = "Bash", callId = "fixture-call" },
                new { type = "text", text = "山西晋铁源 fixture-secret-must-not-persist" } }
        }];
    }
    var state = new {
        settings = new { mode = new { current = "yolo" }, permission = new { mode = "yolo" },
            model = new { current = new { providerId = "rdt-g", modelId = "fixture-model" } } }, projection, messages
    };
    object result = method switch { "session/send" => new { accepted = true }, "session/stop" => new { stopped = true }, _ => state };
    await Reply(new { id, result });
}
if (scenario == "ignore-eof") await Task.Delay(TimeSpan.FromSeconds(60));
return 0;
