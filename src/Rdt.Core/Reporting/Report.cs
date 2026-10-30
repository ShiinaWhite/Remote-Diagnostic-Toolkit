using System.Text;
using Rdt.Common;
using Rdt.Session;

namespace Rdt.Reporting;

public sealed class Report
{
    public string DirectoryPath { get; }
    private Report(string path) => DirectoryPath = path;

    public static Report Create(string reportsBase, string tempBase)
    {
        reportsBase = Path.GetFullPath(reportsBase);
        PrivateFiles.RejectLinks(reportsBase);
        var paths = new SessionPaths(tempBase);
        if (paths.ContainsSessionPath(reportsBase)
            || string.Equals(paths.TempBase, Path.TrimEndingDirectorySeparator(reportsBase), SessionPaths.Comparison))
            throw new RdtException("REPORT_PATH_UNSAFE");
        Directory.CreateDirectory(reportsBase);
        var path = Path.Combine(reportsBase, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        PrivateFiles.CreateDirectory(path);
        return new Report(path);
    }

    public void Complete(bool isMock, Guid? sessionId, bool runtimeStarted, int? runtimeExit, bool cleanupComplete, FailureCode error)
    {
        var content = $"""
        # Remote Diagnostic Toolkit

        Version: {RdtInfo.Version}
        Provider: G
        Mode: {(isMock ? "READ ONLY" : "YOLO")}
        Runtime: {(isMock ? "MOCK (no diagnostics performed)" : "FIXED ZCODE STDIO")}
        Session: {sessionId?.ToString("N") ?? "not created"}
        Runtime started: {runtimeStarted}
        Runtime exit: {runtimeExit?.ToString() ?? "none"}
        Cleanup complete: {cleanupComplete}
        Result: {error}

        This report contains lifecycle metadata only. It is not a diagnosis or a proof of system health.
        """;
        PrivateFiles.WriteNew(Path.Combine(DirectoryPath, "summary.md"), Encoding.UTF8.GetBytes(content));
    }
}
