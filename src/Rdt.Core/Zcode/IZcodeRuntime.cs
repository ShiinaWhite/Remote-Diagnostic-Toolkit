using Rdt.Common;
using Rdt.Session;

namespace Rdt.Zcode;

public sealed record RuntimeResult(int ExitCode, bool Cancelled);

public interface IZcodeRuntime
{
    bool IsMock { get; }
    Task<RuntimeResult> RunAsync(DiagnosticSession session, Action started, CancellationToken cancellationToken);
}
