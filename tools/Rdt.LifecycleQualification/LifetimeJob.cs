using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rdt.Qualification;

// Qualification only: lifetime ownership, no resource/security/UI limits and no breakaway.
internal sealed class LifetimeJob : IDisposable
{
    internal const uint KillOnClose = 0x2000;
    private readonly JobHandle handle;
    internal uint Flags { get; }
    internal bool Inheritable { get; }
    internal LifetimeJob(string? injectedFailure = null, bool nestedTestParent = false)
    {
        if (injectedFailure == "create") throw new IOException("INJECTED_JOB_CREATE_FAILURE");
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "JOB_CREATE_FAILED"); }
        try
        {
            if (injectedFailure == "config") throw new IOException("INJECTED_JOB_CONFIG_FAILURE");
            Check(SetHandleInformation(handle, 1, 0), "JOB_HANDLE_INHERITANCE_FAILED");
            var information = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = nestedTestParent ? 0u : KillOnClose } };
            Check(SetInformationJobObject(handle, 9, ref information, (uint)Marshal.SizeOf<ExtendedLimits>()), "JOB_CONFIG_FAILED");
            Check(QueryLimits(handle, 9, out var actual, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero), "JOB_QUERY_LIMITS_FAILED");
            Check(GetHandleInformation(handle, out var attributes), "JOB_QUERY_HANDLE_FAILED");
            Flags = actual.Basic.LimitFlags; Inheritable = (attributes & 1) != 0;
            if (Flags != information.Basic.LimitFlags || Inheritable) throw new IOException("JOB_CONFIG_NOT_VERIFIED");
        }
        catch { handle.Dispose(); throw; }
    }
    internal void Assign(Process process, bool injectedFailure = false)
    {
        if (injectedFailure) throw new IOException("INJECTED_JOB_ASSIGN_FAILURE");
        Check(AssignProcessToJobObject(handle, process.SafeHandle), "JOB_ASSIGN_FAILED");
        if (!Contains(process.SafeHandle)) throw new IOException("JOB_ASSIGN_NOT_VERIFIED");
    }
    internal bool Contains(SafeProcessHandle process)
    {
        Check(IsProcessInJob(process, handle, out var member), "JOB_MEMBERSHIP_QUERY_FAILED"); return member;
    }
    internal static bool InAnyJob(Process process)
    {
        Check(IsProcessInAnyJob(process.SafeHandle, IntPtr.Zero, out var member), "PARENT_JOB_QUERY_FAILED"); return member;
    }
    internal int[] Members()
    {
        for (var capacity = 32; capacity <= 65536; capacity *= 2)
        {
            var bytes = 8 + capacity * IntPtr.Size;
            var memory = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!QueryProcessIds(handle, 3, memory, (uint)bytes, IntPtr.Zero))
                {
                    if (Marshal.GetLastWin32Error() == 234) continue;
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "JOB_PROCESS_LIST_FAILED");
                }
                var count = Marshal.ReadInt32(memory, 4);
                if (count < 0 || count > capacity) throw new IOException("JOB_PROCESS_LIST_INVALID");
                return Enumerable.Range(0, count).Select(i => checked((int)Marshal.ReadIntPtr(memory, 8 + i * IntPtr.Size))).ToArray();
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new IOException("JOB_OBSERVATION_CAPACITY_EXCEEDED"); // Observer bound, never a process/resource limit.
    }
    internal void Terminate() => Check(TerminateJobObject(handle, 125), "JOB_TERMINATION_FAILED");
    public void Dispose() => handle.Dispose(); // Last non-inherited handle closes -> kernel terminates members.
    private static void Check(bool success, string code) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), code); }

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern JobHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(JobHandle job, int kind, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryLimits(JobHandle job, int kind, out ExtendedLimits information, uint length, IntPtr returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryProcessIds(JobHandle job, int kind, IntPtr information, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(SafeProcessHandle process, JobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInAnyJob(SafeProcessHandle process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(JobHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetHandleInformation(JobHandle handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(JobHandle job, uint exit);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
