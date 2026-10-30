using System.Security.Cryptography;
using Rdt.Common;
using Rdt.Session;

namespace Rdt.Runtime;

public static class RuntimeIdentity
{
    public const string Sha256 = "f40f4b6c8709ba2cc89ea000c3a6d97e8680ee4233720f737b6b7a506417ddf8";
    public const string BuiltinSha256 = "e4e8e3527fa131195e3dc009d4fac9e5b403834281e9ecdddaa68850dc2cb7c0";
    public const string CliVersion = "0.17.0";
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "runtime", "zcode-windows-x64.exe");

    // Keep the verified image open without write/delete sharing through process exit.
    // No executable path is accepted by the production CLI.
    internal static FileStream OpenVerified(string path, string hash, string missing, string mismatch)
    {
        PrivateFiles.RejectLinks(path);
        if (!File.Exists(path)) throw new RdtException(missing);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new RdtException(mismatch);
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static void StageBuiltin(DiagnosticSession session)
    {
        using var source = OpenVerified(Path.Combine(AppContext.BaseDirectory, "runtime", "zcode-builtin.json"),
            BuiltinSha256, "BUILTIN_MISSING", "BUILTIN_HASH_MISMATCH");
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        PrivateFiles.WriteNew(session.BuiltinFile, bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
        using var verified = OpenVerified(session.BuiltinFile, BuiltinSha256, "BUILTIN_MISSING", "BUILTIN_HASH_MISMATCH");
    }
}
