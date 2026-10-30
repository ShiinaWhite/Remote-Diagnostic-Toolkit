using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

namespace Rdt.Qualification;

// Non-admin observations, not an OS sandbox or a complete packet/process audit.
internal sealed class WindowsObservation : IDisposable
{
    private readonly string sessionRoot;
    private readonly List<string> errors;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly ConcurrentQueue<object> fileEvents = new();
    private readonly ConcurrentDictionary<string, byte> connections = new();
    private readonly ConcurrentDictionary<int, string> children = new();
    private readonly CancellationTokenSource stop = new();
    private Task? sampling;
    private int dropped;

    internal WindowsObservation(string sessionRoot, List<string> errors)
    {
        this.sessionRoot = sessionRoot;
        this.errors = errors;
        var roots = new Dictionary<string, string>
        {
            ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["TEMP"] = Path.GetTempPath(),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        foreach (var (label, path) in roots)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { AddError("WATCH_ROOT_UNAVAILABLE:" + label); continue; }
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 65536,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                void Record(string kind, string changed)
                {
                    if (changed.Equals(sessionRoot, StringComparison.OrdinalIgnoreCase)
                        || changed.StartsWith(sessionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
                    if (fileEvents.Count >= 10000) { Interlocked.Increment(ref dropped); return; }
                    // Local-only raw paths. Public evidence uses counts and placeholders.
                    fileEvents.Enqueue(new { root = label, kind, relative_path = Path.GetRelativePath(path, changed) });
                }
                watcher.Created += (_, e) => Record("created", e.FullPath);
                watcher.Changed += (_, e) => Record("changed", e.FullPath);
                watcher.Deleted += (_, e) => Record("deleted", e.FullPath);
                watcher.Renamed += (_, e) => Record("renamed", e.FullPath);
                watcher.Error += (_, _) => AddError("WATCHER_OVERFLOW_OR_ERROR:" + label);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception e) { AddError("WATCHER:" + label + ":" + e.GetType().Name); }
        }
    }

    private void AddError(string error) { lock (errors) errors.Add(error); }

    internal void Start(int pid)
    {
        children[pid] = "candidate";
        sampling = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { SampleProcesses(); SampleTcp(); }
                catch (Exception e) { AddError("SAMPLING:" + e.GetType().Name); break; }
                try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });
    }

    internal void Stop()
    {
        stop.Cancel();
        sampling?.GetAwaiter().GetResult();
        foreach (var watcher in watchers) watcher.EnableRaisingEvents = false;
    }

    internal object Result() => new
    {
        file_events = fileEvents.ToArray(), dropped_events = dropped,
        observed_processes = children.OrderBy(p => p.Key).Select(p => new { pid = p.Key, name = p.Value }).ToArray(),
        tcp_ipv4 = connections.Keys.Order().ToArray(), interval_ms = 50,
        limitations = new[] { "TCP IPv4 polling can miss short connections", "IPv6, UDP and DNS/TLS hostnames are not captured",
            "Process polling can miss short child processes", "Filesystem events are not attributed to a process",
            "Watcher coverage is not a complete machine snapshot" }
    };

    private void SampleProcesses()
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new IOException("PROCESS_SNAPSHOT_FAILED");
        try
        {
            var rows = new List<(int Id, int Parent, string Name)>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (!Process32FirstW(handle, ref entry)) return;
            do { rows.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile)); }
            while (Process32NextW(handle, ref entry));
            for (var i = 0; i < 16; i++)
            {
                var added = false;
                foreach (var row in rows)
                    if (children.ContainsKey(row.Parent)) added |= children.TryAdd(row.Id, row.Name);
                if (!added) break;
            }
        }
        finally { CloseHandle(handle); }
    }

    private void SampleTcp()
    {
        uint size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (size < 4) return;
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(memory, ref size, false, 2, 5, 0) != 0) return;
            var count = Marshal.ReadInt32(memory);
            for (var i = 0; i < count; i++)
            {
                var offset = 4 + i * 24;
                var pid = Marshal.ReadInt32(memory, offset + 20);
                if (!children.ContainsKey(pid)) continue;
                var ip = (uint)Marshal.ReadInt32(memory, offset + 12);
                var port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(memory, offset + 16));
                if (port != 0 && ip != 0) connections.TryAdd(new IPAddress(ip) + ":" + port, 0);
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void Dispose()
    {
        Stop();
        foreach (var watcher in watchers) watcher.Dispose();
        stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, ProcessId;
        internal UIntPtr DefaultHeapId;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}
