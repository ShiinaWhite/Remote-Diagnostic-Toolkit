using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Rdt.Qualification;

// Retained process handles avoid PID-reuse ambiguity when waiting after Job close.
internal sealed class JobObservation(LifetimeJob job, string workspace, string sessionId) : IDisposable
{
    private readonly Dictionary<int, Process> processes = new();
    private readonly Dictionary<string, object> markers = new();
    internal List<string> Errors { get; } = [];
    internal void Sample()
    {
        foreach (var pid in job.Members())
        {
            if (processes.ContainsKey(pid)) continue;
            try { var p = Process.GetProcessById(pid); _ = p.SafeHandle; processes.Add(pid, p); }
            catch (ArgumentException) { } // Already exited; the kernel still owns the Job lifetime.
        }
        foreach (var file in Directory.EnumerateFiles(workspace, "*.pid.json"))
        {
            var name = Path.GetFileName(file);
            if (markers.ContainsKey(name)) continue;
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("PID_MARKER_REPARSE");
                using var json = JsonDocument.Parse(File.ReadAllBytes(file));
                var value = json.RootElement;
                if (value.GetProperty("session_id").GetString() != sessionId) throw new IOException("PID_MARKER_OWNER");
                var pid = value.GetProperty("pid").GetInt32();
                using var p = Process.GetProcessById(pid);
                var birth = value.GetProperty("start_ticks").GetInt64();
                if (p.StartTime.ToUniversalTime().Ticks != birth) throw new IOException("PID_MARKER_REUSED");
                var member = job.Contains(p.SafeHandle); // Actual Job API, never inferred from ancestry.
                markers.Add(name, new { pid, start_ticks = birth, member });
                if (!member) Errors.Add("MARKED_PROCESS_OUTSIDE_JOB:" + name);
            }
            catch (JsonException) { } // Atomic writer can still be in progress; retry.
            catch (IOException e) when (e.HResult == unchecked((int)0x80070020)) { }
            catch (ArgumentException) { Errors.Add("MARKER_EXITED_BEFORE_MEMBERSHIP:" + name); }
        }
    }
    internal object Memberships() => markers;
    internal bool HasMarker(string name) => markers.ContainsKey(name);
    internal object[] LiveBeforeClose()
    {
        var live = new List<object>();
        foreach (var p in processes.Values)
        {
            try { if (!p.HasExited) live.Add(new { pid = p.Id, start_ticks = p.StartTime.ToUniversalTime().Ticks }); }
            catch (InvalidOperationException) { } // Exited during this observation.
        }
        return live.ToArray();
    }
    internal async Task<object> WaitAfterCloseAsync()
    {
        var live = new List<int>();
        foreach (var p in processes.Values)
        {
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { live.Add(p.Id); }
        }
        return new { observed_handles = processes.Count, alive_after_close = live, all_exited = live.Count == 0 };
    }
    public void Dispose() { foreach (var p in processes.Values) p.Dispose(); }

    internal static int[] DescendantsBeforeAssign(int rootPid)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var rows = new List<(int Pid, int Parent)>();
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
            if (Process32FirstW(snapshot, ref entry))
                do { rows.Add(((int)entry.Pid, (int)entry.Parent)); } while (Process32NextW(snapshot, ref entry));
            var seen = new HashSet<int> { rootPid };
            for (var i = 0; i < 32; i++)
            {
                var changed = false;
                foreach (var row in rows) if (seen.Contains(row.Parent)) changed |= seen.Add(row.Pid);
                if (!changed) break;
            }
            return seen.Where(p => p != rootPid).ToArray();
        }
        finally { CloseHandle(snapshot); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry
    {
        public uint Size, Usage, Pid; public UIntPtr Heap;
        public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
