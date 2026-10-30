using System.Text.Json;
using System.Text.Json.Serialization;
using Rdt.Common;

namespace Rdt.Reporting;

public enum AuditEvent { Started, CredentialUnlocked, SessionCreated, ProviderConfigured, RuntimeStarted, RuntimeExited, CleanupCompleted, CleanupIncomplete, Failed, Exited }
public enum FailureCode { None, CredentialRejected, RuntimeUnvalidated, RuntimeFailure, Cancelled, OperationFailed, CleanupFailed,
    RuntimeMissing, RuntimeHashMismatch, BuiltinMissing, BuiltinHashMismatch, JobFailure, JobCleanupFailed,
    ProtocolMalformed, RuntimeEof, ProtocolError, ProtocolTimeout, SessionCreateFailed, SessionSendFailed,
    SessionSelectionMismatch, RuntimeTimeout, ProviderAuthError, ProviderNetworkError, ProviderFailure, TaskRequired }
internal static class FailureCodes
{
    internal static FailureCode FromRuntime(string code) => code switch {
        "RUNTIME_MISSING" => FailureCode.RuntimeMissing, "RUNTIME_HASH_MISMATCH" => FailureCode.RuntimeHashMismatch,
        "BUILTIN_MISSING" => FailureCode.BuiltinMissing, "BUILTIN_HASH_MISMATCH" => FailureCode.BuiltinHashMismatch,
        "JOB_CLEANUP_FAILED" => FailureCode.JobCleanupFailed, "PROTOCOL_MALFORMED" or "PROTOCOL_FRAME_TOO_LARGE" => FailureCode.ProtocolMalformed,
        "RUNTIME_EOF" => FailureCode.RuntimeEof, "PROTOCOL_ERROR" => FailureCode.ProtocolError,
        "PROTOCOL_TIMEOUT" => FailureCode.ProtocolTimeout, "SESSION_CREATE_FAILED" => FailureCode.SessionCreateFailed,
        "SESSION_SEND_FAILED" => FailureCode.SessionSendFailed, "SESSION_SELECTION_MISMATCH" => FailureCode.SessionSelectionMismatch,
        "RUNTIME_TIMEOUT" => FailureCode.RuntimeTimeout, "PROVIDER_AUTH_ERROR" => FailureCode.ProviderAuthError,
        "PROVIDER_NETWORK_ERROR" => FailureCode.ProviderNetworkError, "PROVIDER_FAILURE" => FailureCode.ProviderFailure,
        "TASK_REQUIRED" => FailureCode.TaskRequired, _ when code.StartsWith("JOB_", StringComparison.Ordinal) => FailureCode.JobFailure,
        _ => FailureCode.OperationFailed
    };
}
public sealed record AuditEntry(DateTimeOffset Utc, string Version, ProviderId Provider, string Mode,
    AuditEvent Event, Guid? SessionId, int? ExitCode, FailureCode Error);

// No free-form message, exception, prompt, secret, header, or raw child output API.
public sealed class AuditLog : IDisposable
{
    private readonly StreamWriter writer;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();
    private readonly string mode;
    public AuditLog(string path, bool isMock = true)
    {
        mode = isMock ? "READ ONLY" : "YOLO";
        PrivateFiles.WriteNew(path, []);
        writer = new StreamWriter(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }
    public void Write(AuditEvent kind, Guid? sessionId = null, int? exitCode = null, FailureCode error = FailureCode.None)
    {
        lock (gate) writer.WriteLine(JsonSerializer.Serialize(new AuditEntry(DateTimeOffset.UtcNow, RdtInfo.Version, ProviderId.G,
            mode, kind, sessionId, exitCode, error), Json));
    }
    public void Dispose() => writer.Dispose();
}
