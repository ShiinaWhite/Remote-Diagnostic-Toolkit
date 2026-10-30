using System.Diagnostics;
using System.Text.Json;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Reporting;
using Rdt.Session;
using Xunit;

namespace Rdt.Tests;

public sealed class HardeningTests(FoundationFixture f) : IClassFixture<FoundationFixture>
{
    private static void SetAge(DiagnosticSession session, TimeSpan age) =>
        File.WriteAllBytes(Path.Combine(session.Root, "owner.json"), JsonSerializer.SerializeToUtf8Bytes(
            new SessionMarker(1, session.Id, DateTimeOffset.UtcNow - age)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerWrittenThenLeaseCreationFailsLeavesNoDirectory(bool partialLease)
    {
        var before = Directory.GetDirectories(f.Root).Order().ToArray();
        string? root = null;
        var error = new IOException("Injected lease creation failure");
        Assert.Same(error, Assert.Throws<IOException>(() => DiagnosticSession.Create(f.Root, leasePath =>
        {
            root = Path.GetDirectoryName(leasePath)!;
            var marker = JsonSerializer.Deserialize<SessionMarker>(File.ReadAllBytes(Path.Combine(root, "owner.json")))!;
            Assert.Equal("RDT-" + marker.Id.ToString("N"), Path.GetFileName(root));
            Assert.False(File.Exists(leasePath));
            if (partialLease) File.WriteAllBytes(leasePath, []);
            throw error;
        })));
        Assert.NotNull(root);
        Assert.False(Directory.Exists(root));
        Assert.Equal(before, Directory.GetDirectories(f.Root).Order().ToArray());
        Assert.Equal(CleanupResult.AlreadyAbsent, new SessionCleaner(f.Root).Clean(root));
    }

    [Fact]
    public void FailureAfterLeaseAcquisitionAlsoRollsBack()
    {
        string? root = null;
        Assert.Equal("PATH_ALREADY_EXISTS", Assert.Throws<RdtException>(() => DiagnosticSession.Create(f.Root, leasePath =>
        {
            root = Path.GetDirectoryName(leasePath)!;
            Directory.CreateDirectory(Path.Combine(root, "home"));
            return new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        })).Code);
        Assert.NotNull(root);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void FailedCreationCannotBypassMarkerIdentity()
    {
        string? root = null;
        Assert.Throws<AggregateException>(() => DiagnosticSession.Create(f.Root, leasePath =>
        {
            root = Path.GetDirectoryName(leasePath)!;
            File.WriteAllBytes(Path.Combine(root, "owner.json"), JsonSerializer.SerializeToUtf8Bytes(
                new SessionMarker(1, Guid.NewGuid(), DateTimeOffset.UtcNow)));
            throw new IOException("Injected marker replacement");
        }));
        Assert.True(Directory.Exists(root));
        Assert.Equal(CleanupResult.Refused, new SessionCleaner(f.Root).Clean(root!));
    }

    [Fact]
    public void RecentMissingLeaseIsOrphanedAndPreservedUntilOldEnough()
    {
        using var session = DiagnosticSession.Create(f.Root);
        session.Dispose();
        File.Delete(Path.Combine(session.Root, ".lease"));
        File.WriteAllText(Path.Combine(session.Data, "evidence.txt"), "keep");
        var cleaner = new SessionCleaner(f.Root);
        Assert.Equal(CleanupResult.Orphaned, cleaner.Clean(session.Root));
        Assert.DoesNotContain(session.Root, cleaner.FindStale(DateTimeOffset.UtcNow));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(session.Data, "evidence.txt")));
        Assert.False(File.Exists(Path.Combine(session.Root, ".lease")));
        SetAge(session, TimeSpan.FromDays(2));
        Assert.Contains(session.Root, cleaner.FindStale(DateTimeOffset.UtcNow));
        Assert.Equal(CleanupResult.Complete, cleaner.Clean(session.Root));
        Assert.False(Directory.Exists(session.Root));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("mismatched")]
    [InlineData("version")]
    [InlineData("future")]
    [InlineData("zero-time")]
    [InlineData("oversized")]
    public void MissingLeaseNeverBypassesMarkerValidation(string kind)
    {
        using var session = DiagnosticSession.Create(f.Root);
        session.Dispose();
        File.Delete(Path.Combine(session.Root, ".lease"));
        var file = Path.Combine(session.Root, "owner.json");
        SetAge(session, TimeSpan.FromDays(2));
        switch (kind)
        {
            case "missing": File.Delete(file); break;
            case "malformed": File.WriteAllText(file, "not JSON"); break;
            case "oversized": File.WriteAllText(file, new string(' ', 1025)); break;
            default:
                File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(new SessionMarker(
                    kind == "version" ? 2 : 1,
                    kind == "mismatched" ? Guid.NewGuid() : session.Id,
                    kind == "future" ? DateTimeOffset.UtcNow.AddDays(2)
                        : kind == "zero-time" ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddDays(-2))));
                break;
        }
        var cleaner = new SessionCleaner(f.Root);
        Assert.Equal(CleanupResult.Refused, cleaner.Clean(session.Root));
        Assert.DoesNotContain(session.Root, cleaner.FindStale(DateTimeOffset.UtcNow));
        Assert.True(Directory.Exists(session.Root));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("nested")]
    [InlineData("bad-name")]
    public void MissingLeaseNeverBypassesTempScopeOrGuidFormat(string kind)
    {
        var parent = Path.Combine(f.Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        using var session = DiagnosticSession.Create(parent);
        session.Dispose();
        SetAge(session, TimeSpan.FromDays(2));
        File.Delete(Path.Combine(session.Root, ".lease"));
        var root = session.Root;
        if (kind == "bad-name")
        {
            var renamed = Path.Combine(parent, "RDT-not-a-guid");
            Directory.Move(root, renamed);
            root = renamed;
        }
        var cleaner = new SessionCleaner(kind == "outside" ? f.Reports : kind == "nested" ? f.Root : parent);
        Assert.Equal(CleanupResult.Refused, cleaner.Clean(root));
        Assert.DoesNotContain(root, cleaner.FindStale(DateTimeOffset.UtcNow));
        Assert.True(File.Exists(Path.Combine(root, "owner.json")));
    }

    [Fact]
    public void DeletionSharingViolationIsFailedAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var session = DiagnosticSession.Create(f.Root);
        session.Dispose();
        var file = Path.Combine(session.Data, "locked.txt");
        File.WriteAllText(file, "keep until released");
        var cleaner = new SessionCleaner(f.Root);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(CleanupResult.Failed, cleaner.Clean(session.Root));
            Assert.True(File.Exists(Path.Combine(session.Root, "owner.json")));
            Assert.True(File.Exists(Path.Combine(session.Root, ".lease")));
            Assert.True(File.Exists(file));
        }
        Assert.Equal(CleanupResult.Complete, cleaner.Clean(session.Root));
    }

    [Fact]
    public void UnopenableLeaseIsFailedRatherThanActive()
    {
        using var session = DiagnosticSession.Create(f.Root);
        session.Dispose();
        var lease = Path.Combine(session.Root, ".lease");
        File.Delete(lease);
        Directory.CreateDirectory(lease);
        Assert.Equal(CleanupResult.Failed, new SessionCleaner(f.Root).Clean(session.Root));
        Assert.True(File.Exists(Path.Combine(session.Root, "owner.json")));
    }

    [Fact]
    public async Task LeaseHeldByAnotherProcessIsActive()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var session = DiagnosticSession.Create(f.Root);
        session.Dispose();
        SetAge(session, TimeSpan.FromDays(2));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$lease = [IO.File]::Open('" + Path.Combine(session.Root, ".lease").Replace("'", "''")
            + "', 'Open', 'ReadWrite', 'None'); try { [Console]::WriteLine('LEASE_READY'); [Console]::ReadLine() | Out-Null } finally { $lease.Dispose() }");
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("LEASE_READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var cleaner = new SessionCleaner(f.Root);
            Assert.Equal(CleanupResult.Active, cleaner.Clean(session.Root));
            Assert.DoesNotContain(session.Root, cleaner.FindStale(DateTimeOffset.UtcNow));
            Assert.True(Directory.Exists(session.Data));
        }
        finally
        {
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(CleanupResult.Complete, new SessionCleaner(f.Root).Clean(session.Root));
    }

    [Fact]
    public void RdtSupportReportsAreAllowed()
    {
        var reports = Path.Combine(f.Root, "RDT-Support", "Reports");
        var report = Report.Create(reports, f.Root);
        Assert.Equal(reports, Path.GetDirectoryName(report.DirectoryPath));
        Assert.True(Directory.Exists(report.DirectoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportsCannotEnterMarkedSessionFromAnotherTempRoot(bool orphan)
    {
        using var session = DiagnosticSession.Create(f.Root);
        if (orphan)
        {
            session.Dispose();
            SetAge(session, TimeSpan.FromDays(2));
            File.Delete(Path.Combine(session.Root, ".lease"));
        }
        Assert.Throws<RdtException>(() => Report.Create(Path.Combine(session.Root, "Reports"), Path.GetTempPath()));
        Assert.False(Directory.Exists(Path.Combine(session.Root, "Reports")));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("orphan")]
    [InlineData("missing-marker")]
    [InlineData("not-created")]
    public void ExactSessionReportsAreRejected(string kind)
    {
        using var session = DiagnosticSession.Create(f.Root);
        var root = session.Root;
        if (kind != "active") session.Dispose();
        if (kind == "orphan") { SetAge(session, TimeSpan.FromDays(2)); File.Delete(Path.Combine(root, ".lease")); }
        if (kind == "missing-marker") File.Delete(Path.Combine(root, "owner.json"));
        if (kind == "not-created") root = Path.Combine(f.Root, "RDT-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("REPORT_PATH_UNSAFE", Assert.Throws<RdtException>(() =>
            Report.Create(Path.Combine(root, "Reports", "nested"), f.Root)).Code);
        Assert.False(Directory.Exists(Path.Combine(root, "Reports")));
    }
}
