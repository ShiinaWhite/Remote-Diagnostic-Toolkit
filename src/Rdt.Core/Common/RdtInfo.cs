namespace Rdt.Common;

public static class RdtInfo
{
    public const string Version = "0.1.0";
    public const string Mode = "READ ONLY";
    public const string UpstreamCommit = "3c5b227649371507ddc4eb46ac5133afe6db1cab";
}

public enum ProviderId { G, D }

public sealed class RdtException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
