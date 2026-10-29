using System.Text.Json;
using System.Text.Json.Serialization;
using Rdt.Common;

namespace Rdt.Reporting;

public enum AuditEvent { Started, CredentialUnlocked, SessionCreated, ProviderConfigured, RuntimeStarted, RuntimeExited, CleanupCompleted, CleanupIncomplete, Failed, Exited }
public enum FailureCode { None, CredentialRejected, RuntimeUnvalidated, RuntimeFailure, Cancelled, OperationFailed, CleanupFailed }
public sealed record AuditEntry(DateTimeOffset Utc, string Version, ProviderId Provider, string Mode,
    AuditEvent Event, Guid? SessionId, int? ExitCode, FailureCode Error);

// No free-form message, exception, prompt, secret, header, or raw child output API.
public sealed class AuditLog : IDisposable
{
    private readonly StreamWriter writer;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();
    public AuditLog(string path)
    {
        PrivateFiles.WriteNew(path, []);
        writer = new StreamWriter(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }
    public void Write(AuditEvent kind, Guid? sessionId = null, int? exitCode = null, FailureCode error = FailureCode.None)
    {
        lock (gate) writer.WriteLine(JsonSerializer.Serialize(new AuditEntry(DateTimeOffset.UtcNow, RdtInfo.Version, ProviderId.G,
            RdtInfo.Mode, kind, sessionId, exitCode, error), Json));
    }
    public void Dispose() => writer.Dispose();
}
