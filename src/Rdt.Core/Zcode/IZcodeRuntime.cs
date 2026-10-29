using Rdt.Common;
using Rdt.Session;

namespace Rdt.Zcode;

public sealed record RuntimeResult(int ExitCode, bool Cancelled);

public interface IZcodeRuntime
{
    bool IsMock { get; }
    Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken);
}

public sealed class ZcodeRuntime : IZcodeRuntime
{
    public bool IsMock => false;
    public Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken)
        => throw new RdtException("RUNTIME_NOT_VALIDATED");
}
