using System.Diagnostics;
using Rdt.Session;

namespace Rdt.Zcode;

public enum MockBehavior { Success, Crash, Hang }

// Runs only the RDT test child. The CLI never accepts an arbitrary executable or shell text.
public sealed class MockProcessRuntime(string rdtExecutable, MockBehavior behavior = MockBehavior.Success) : IZcodeRuntime
{
    public bool IsMock => true;
    public async Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken)
    {
        var info = ReadOnlyPolicy.CreateStartInfo(rdtExecutable, session, "Diagnostics fixture; no system investigation.");
        var args = info.ArgumentList.ToArray();
        info.ArgumentList.Clear();
        info.ArgumentList.Add("__mock-runtime"); info.ArgumentList.Add(behavior.ToString());
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        var cancelled = false;
        try
        {
            started();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        return new RuntimeResult(process.ExitCode, cancelled);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer) > 0) { /* Raw child output is never persisted or displayed. */ } }
        finally { Array.Clear(buffer); }
    }
}
