using System.Diagnostics;
using System.Text.Json;

namespace Rdt.Qualification;

internal static class ChildLifetime
{
    // Synthetic PowerShell writes its own PID + birth time + Session ID. Check all
    // three before observing or cleaning an orphan; never kill by a reused PID alone.
    internal static async Task<object> CheckAndRecoverAsync(string workspace, string sessionId, DateTime started)
    {
        var records = new List<(int Pid, long Ticks, string Marker)>();
        foreach (var file in Directory.EnumerateFiles(workspace, "*.pid.json"))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("UNSAFE_PID_MARKER");
            using var json = JsonDocument.Parse(File.ReadAllBytes(file));
            var data = json.RootElement;
            if (data.GetProperty("session_id").GetString() != sessionId) throw new IOException("PID_MARKER_OWNER_MISMATCH");
            records.Add((data.GetProperty("pid").GetInt32(), data.GetProperty("start_ticks").GetInt64(), Path.GetFileName(file)));
        }
        bool Alive((int Pid, long Ticks, string Marker) record)
        {
            try
            {
                using var child = Process.GetProcessById(record.Pid);
                return !child.HasExited && child.StartTime.ToUniversalTime().Ticks == record.Ticks
                    && child.StartTime.ToUniversalTime() >= started.AddSeconds(-2);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; } // Exited between HasExited and StartTime.
        }
        for (var i = 0; i < 30 && records.Any(Alive); i++) await Task.Delay(100);
        var escaped = records.Where(Alive).ToArray();
        foreach (var record in escaped)
        {
            using var child = Process.GetProcessById(record.Pid);
            if (child.StartTime.ToUniversalTime().Ticks != record.Ticks) continue;
            child.Kill(entireProcessTree: true); // Test-owned recovery, AFTER recording failure.
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        return new { markers = records.Select(r => r.Marker).ToArray(),
            live_after_runtime_exit = escaped.Select(r => new { pid = r.Pid, marker = r.Marker }).ToArray(),
            recovery_required = escaped.Length > 0, recovered = records.All(r => !Alive(r)),
            limitation = "PID markers cover fixture-owned long-lived processes; arbitrary unobserved descendants are not proven absent" };
    }
}
