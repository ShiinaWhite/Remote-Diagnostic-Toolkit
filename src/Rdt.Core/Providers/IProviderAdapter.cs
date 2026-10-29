using Rdt.Common;
using Rdt.Session;

namespace Rdt.Providers;

public interface IProviderAdapter
{
    ProviderId Id { get; }
    void WriteTemporaryConfig(DiagnosticSession session, byte[] secret);
}

public sealed class DeferredDProvider : IProviderAdapter
{
    public ProviderId Id => ProviderId.D;
    public void WriteTemporaryConfig(DiagnosticSession session, byte[] secret) => throw new RdtException("PROVIDER_UNAVAILABLE");
}
