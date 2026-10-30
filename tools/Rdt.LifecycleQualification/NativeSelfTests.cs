using System.Diagnostics;
using Rdt.Cleanup;
using Rdt.Session;

namespace Rdt.Qualification;

// Windows CI: exercises real Job APIs without any ZCode binary or model API.
internal static class NativeSelfTests
{
    private static readonly List<(int Pid, long Birth)> Canaries = [];
    internal static async Task<int> RunAsync()
    {
        var passed = new List<string>();
        void Assert(bool value, string name) { if (!value) throw new IOException("SELF_TEST_FAILED:" + name); passed.Add(name); }
        using var session = DiagnosticSession.Create();
        try
        {
            using (var job = new LifetimeJob())
            {
                Assert(job.Flags == LifetimeJob.KillOnClose, "only_kill_on_close_flag");
                Assert(!job.Inheritable, "noninherited_job_handle");
            }
            foreach (var fault in new[] { "create", "config" })
            {
                var rejected = false;
                try { using var ignored = new LifetimeJob(fault); }
                catch (IOException e) when (e.Message.StartsWith("INJECTED_", StringComparison.Ordinal)) { rejected = true; }
                Assert(rejected, fault + "_failure_closed");
            }
            using (var child = StartCanary(session))
            {
                using var job = new LifetimeJob();
                job.Assign(child);
                Assert(job.Contains(child.SafeHandle), "actual_process_membership");
                Assert(job.Members().Contains(child.Id), "query_information_process_list");
                Assert(!child.HasExited, "member_alive_before_close");
                job.Dispose();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Assert(child.HasExited, "kernel_close_terminates_member");
            }
            using (var child = StartCanary(session))
            using (var parent = new LifetimeJob())
            using (var nested = new LifetimeJob())
            {
                parent.Assign(child); nested.Assign(child);
                Assert(parent.Contains(child.SafeHandle) && nested.Contains(child.SafeHandle), "nested_membership");
                nested.Dispose(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Assert(child.HasExited, "nested_close_terminates_member");
            }
            using (var child = StartCanary(session))
            using (var job = new LifetimeJob())
            {
                var rejected = false;
                try { job.Assign(child, injectedFailure: true); }
                catch (IOException) { rejected = true; child.Kill(); await child.WaitForExitAsync(); }
                Assert(rejected && child.HasExited, "assignment_failure_stops_unbound_canary");
            }
        }
        finally
        {
            var recoveryNeeded = false;
            foreach (var record in Canaries)
            {
                try
                {
                    using var child = Process.GetProcessById(record.Pid);
                    if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == record.Birth)
                    {
                        recoveryNeeded = true; child.Kill(); await child.WaitForExitAsync();
                    }
                }
                catch (ArgumentException) { }
            }
            session.Dispose();
            Assert(new SessionCleaner().Clean(session.Root) == CleanupResult.Complete, "private_session_cleanup");
            if (recoveryNeeded) throw new IOException("NATIVE_SELF_TEST_REQUIRED_PID_RECOVERY");
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed = passed.Count, tests = passed, real_api_used = false }));
        return 0;
    }
    private static Process StartCanary(DiagnosticSession session)
    {
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = session.Workspace };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) start.ArgumentList.Add(arg);
        start.Environment.Clear(); foreach (var (key, value) in session.EnvironmentVariables()) start.Environment[key] = value;
        var process = Process.Start(start)!;
        Canaries.Add((process.Id, process.StartTime.ToUniversalTime().Ticks));
        return process;
    }
}
