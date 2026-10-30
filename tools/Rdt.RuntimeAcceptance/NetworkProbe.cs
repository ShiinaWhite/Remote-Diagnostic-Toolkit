using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using Rdt.Runtime.Windows;

namespace Rdt.RuntimeAcceptance;

// Acceptance observer only, no policy/firewall changes. Captures endpoints, never
// packets, headers, payloads or credentials. Public evidence contains counts only.
internal sealed class NetworkProbe(RuntimeJob job) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<IPAddress, byte> addresses = new();
    private Task? sampler;
    internal bool Failed { get; private set; }
    internal void Start() => sampler = Task.Run(async () => {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var members = job.Members().ToHashSet();
                Sample(2, 24, 20, 12, 4, members);
                Sample(23, 56, 52, 24, 16, members);
            }
            catch { if (!stop.IsCancellationRequested) Failed = true; break; }
            try { await Task.Delay(25, stop.Token); } catch (OperationCanceledException) { break; }
        }
    });
    internal void Stop() { stop.Cancel(); sampler?.GetAwaiter().GetResult(); }
    internal int Unexpected(HashSet<IPAddress> allowed) => addresses.Keys.Count(ip => !IPAddress.IsLoopback(ip) && !allowed.Contains(ip));
    internal int Observed => addresses.Count;
    private void Sample(int family, int rowSize, int pidOffset, int remoteOffset, int addressSize, HashSet<int> members)
    {
        uint size = 0;
        var query = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
        if (query is not (0 or 122)) throw new IOException("TCP_QUERY_FAILED");
        if (size < 4) return;
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            query = GetExtendedTcpTable(memory, ref size, false, family, 5, 0);
            if (query == 122) return; // Table grew; retry on the next sample.
            if (query != 0) throw new IOException("TCP_QUERY_FAILED");
            var count = Marshal.ReadInt32(memory);
            if (count < 0 || 4L + count * (long)rowSize > size) throw new IOException("TCP_QUERY_INVALID");
            for (var i = 0; i < count; i++)
            {
                var offset = 4 + i * rowSize;
                if (!members.Contains(Marshal.ReadInt32(memory, offset + pidOffset))) continue;
                var bytes = new byte[addressSize]; Marshal.Copy(memory + offset + remoteOffset, bytes, 0, addressSize);
                var address = new IPAddress(bytes);
                if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)) addresses.TryAdd(address, 0);
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public void Dispose() { Stop(); stop.Dispose(); }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int kind, uint reserved);
}
