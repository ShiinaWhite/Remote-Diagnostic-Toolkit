using System.Diagnostics;
using Rdt.Common;
using Rdt.Session;

namespace Rdt.Zcode;

public static class ReadOnlyPolicy
{
    // Historical READ ONLY mock/research contract. Production uses ZcodeRuntime's
    // YOLO stdio launch and never calls this prompt/denylist path.
    public const string DisallowedTools = "Bash Write Edit ApplyPatch NotebookEdit Agent Task Skill EnterPlanMode ExitPlanMode AskUserQuestion WebFetch WebSearch SendMessage ReplyToChannel CreateWorkflow AmendWorkflow SaveWorkflow ResumeWorkflowRun FillWorkflowHole ResolveWorkflowQuestion CronCreate CronUpdate CronDelete";

    public static ProcessStartInfo CreateStartInfo(string executable, DiagnosticSession session, string prompt)
    {
        if (!Path.IsPathFullyQualified(executable) || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 8192)
            throw new RdtException("RUNTIME_INPUT_INVALID");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = session.Workspace,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        info.Environment.Clear();
        foreach (var (name, value) in session.EnvironmentVariables()) info.Environment[name] = value;
        foreach (var arg in new[] { "--prompt", prompt, "--cwd", session.Workspace, "--mode", "plan",
            "--disallowed-tools", DisallowedTools, "--workflow-mode", "disabled", "--json", "--no-color" }) info.ArgumentList.Add(arg);
        return info;
    }
}
