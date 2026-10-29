using System.Security.Cryptography;
using System.Text;
using Rdt.Common;

namespace Rdt.Auth;

public interface ISecretInput
{
    byte[] Read(string label, CancellationToken cancellationToken = default);
}

public sealed class ConsoleSecretInput : ISecretInput
{
    public byte[] Read(string label, CancellationToken cancellationToken = default)
    {
        if (Console.IsInputRedirected) throw new RdtException("INTERACTIVE_TERMINAL_REQUIRED");
        Console.Write(label);
        var buffer = new char[512];
        var length = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable) { Thread.Sleep(20); continue; }
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (length > 0) buffer[--length] = '\0'; continue; }
                if (!char.IsControl(key.KeyChar))
                {
                    if (length == buffer.Length) throw new RdtException("SECRET_TOO_LONG");
                    buffer[length++] = key.KeyChar;
                }
            }
            Console.WriteLine();
            return Encoding.UTF8.GetBytes(buffer, 0, length);
        }
        finally { CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan())); }
    }
}
