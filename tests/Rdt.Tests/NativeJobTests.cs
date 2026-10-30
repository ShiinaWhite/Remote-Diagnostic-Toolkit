using System.Diagnostics;
using Rdt.Runtime.Windows;
using Xunit;

namespace Rdt.Tests;

public sealed class NativeJobTests
{
    private static Process Canary()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    [Fact]
    public void JobHasOnlyKillOnCloseAndNoninheritableHandle()
    {
        using var job = new RuntimeJob(); Assert.Equal(RuntimeJob.KillOnClose, job.Flags); Assert.False(job.Inheritable);
    }

    [Fact]
    public async Task RealMembershipAndLastHandleClosureTerminatesMember()
    {
        using var process = Canary();
        try
        {
            using var job = new RuntimeJob(); job.Assign(process);
            Assert.True(job.Contains(process.SafeHandle)); Assert.Contains(process.Id, job.Members()); Assert.False(process.HasExited);
            job.Dispose(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.True(process.HasExited);
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task NestedJobMembershipAndClosureTerminatesMember()
    {
        using var process = Canary();
        try
        {
            using var parent = new RuntimeJob(); using var nested = new RuntimeJob();
            parent.Assign(process); nested.Assign(process);
            Assert.True(parent.Contains(process.SafeHandle)); Assert.True(nested.Contains(process.SafeHandle));
            nested.Dispose(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.Empty(parent.Members());
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task ExplicitJobTerminationEmptiesMembership()
    {
        using var process = Canary();
        try
        {
            using var job = new RuntimeJob(); job.Assign(process); job.Terminate();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.Empty(job.Members());
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
