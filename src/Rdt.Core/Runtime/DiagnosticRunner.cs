using System.Security.Cryptography;
using Rdt.Cleanup;
using Rdt.Common;
using Rdt.Credentials;
using Rdt.Providers;
using Rdt.Reporting;
using Rdt.Session;
using Rdt.Zcode;

namespace Rdt.Runtime;

public sealed record DiagnosticResult(string ReportPath, Guid? SessionId, bool RuntimeStarted,
    int? RuntimeExitCode, bool CleanupComplete, FailureCode Error)
{
    public bool Success => Error == FailureCode.None && RuntimeStarted && RuntimeExitCode == 0 && CleanupComplete;
}

public sealed class DiagnosticRunner(CredentialVault vault, IProviderAdapter provider, IZcodeRuntime runtime)
{
    public async Task<DiagnosticResult> RunAsync(string credentialFile, byte[] pin, string reportsBase,
        string? tempBase = null, CancellationToken cancellationToken = default, Action? unlocked = null)
    {
        try { return await RunCoreAsync(credentialFile, pin, reportsBase, tempBase, cancellationToken, unlocked); }
        finally { CryptographicOperations.ZeroMemory(pin); }
    }

    private async Task<DiagnosticResult> RunCoreAsync(string credentialFile, byte[] pin, string reportsBase,
        string? tempBase = null, CancellationToken cancellationToken = default, Action? unlocked = null)
    {
        if (provider.Id != ProviderId.G) throw new RdtException("PROVIDER_UNAVAILABLE");
        tempBase ??= Path.GetTempPath();
        DiagnosticSession? session = null;
        byte[]? secret = null;
        var started = false;
        int? runtimeExit = null;
        var cleanupComplete = true;
        var failure = FailureCode.None;
        Report report;
        try { report = Report.Create(reportsBase, tempBase); }
        catch { CryptographicOperations.ZeroMemory(pin); throw; }
        using var audit = new AuditLog(Path.Combine(report.DirectoryPath, "session.log"));
        audit.Write(AuditEvent.Started);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            secret = vault.Unlock(credentialFile, pin);
            CryptographicOperations.ZeroMemory(pin);
            audit.Write(AuditEvent.CredentialUnlocked);
            unlocked?.Invoke();
            session = DiagnosticSession.Create(tempBase);
            audit.Write(AuditEvent.SessionCreated, session.Id);
            session.WriteRuntimeConfig();
            provider.WriteTemporaryConfig(session, secret);
            CryptographicOperations.ZeroMemory(secret);
            audit.Write(AuditEvent.ProviderConfigured, session.Id);
            var result = await runtime.RunAsync(session, () => { started = true; audit.Write(AuditEvent.RuntimeStarted, session.Id); }, cancellationToken);
            runtimeExit = result.ExitCode;
            failure = result.Cancelled ? FailureCode.Cancelled : result.ExitCode == 0 ? FailureCode.None : FailureCode.RuntimeFailure;
            audit.Write(AuditEvent.RuntimeExited, session.Id, result.ExitCode, failure);
        }
        catch (RdtException e) when (e.Code == "CREDENTIAL_UNLOCK_FAILED") { failure = FailureCode.CredentialRejected; }
        catch (RdtException e) when (e.Code == "RUNTIME_NOT_VALIDATED") { failure = FailureCode.RuntimeUnvalidated; }
        catch (OperationCanceledException) { failure = FailureCode.Cancelled; }
        catch (Exception) { failure = FailureCode.OperationFailed; } // Never serialize exception text.
        finally
        {
            CryptographicOperations.ZeroMemory(pin);
            if (secret != null) CryptographicOperations.ZeroMemory(secret);
            session?.Dispose();
            if (session != null)
            {
                var cleaned = new SessionCleaner(tempBase).Clean(session.Root);
                cleanupComplete = cleaned is CleanupResult.Complete or CleanupResult.AlreadyAbsent;
                audit.Write(cleanupComplete ? AuditEvent.CleanupCompleted : AuditEvent.CleanupIncomplete, session.Id);
            }
            if (!cleanupComplete && failure == FailureCode.None) failure = FailureCode.CleanupFailed;
            if (failure != FailureCode.None) audit.Write(AuditEvent.Failed, session?.Id, error: failure);
            audit.Write(AuditEvent.Exited, session?.Id, runtimeExit, failure);
            report.Complete(runtime.IsMock, session?.Id, started, runtimeExit, cleanupComplete, failure);
        }
        return new DiagnosticResult(report.DirectoryPath, session?.Id, started, runtimeExit, cleanupComplete, failure);
    }
}
