using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Rdt.Common;

namespace Rdt.Credentials;

public sealed record CredentialEnvelope(int Version, string Provider, string Kdf, int N, int R, int P,
    string Cipher, string Salt, string Nonce, string Ciphertext, string Tag);

public sealed class CredentialVault
{
    public const int Cost = 131072;
    private const int MaxSize = 65536;
    private static byte[] Aad(ProviderId provider) => Encoding.UTF8.GetBytes(
        $"RDT|1|{provider}|scrypt|131072|8|1|AES-256-GCM");

    public void Create(string path, byte[] pin, byte[] secret, ProviderId provider = ProviderId.G)
    {
        if (pin.Length is < 8 or > 128 || secret.Length is < 1 or > 4096 || provider != ProviderId.G)
            throw new RdtException("CREDENTIAL_INPUT_INVALID");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = SCrypt.Generate(pin, salt, Cost, 8, 1, 32);
        var ciphertext = new byte[secret.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, secret, ciphertext, tag, Aad(provider));
            var envelope = new CredentialEnvelope(1, provider.ToString(), "scrypt", Cost, 8, 1, "AES-256-GCM",
                Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
            PrivateFiles.WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(envelope));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public byte[] Unlock(string path, byte[] pin)
    {
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            if (pin.Length is < 8 or > 128) throw new CryptographicException();
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxSize) throw new CryptographicException();
            var raw = new byte[(int)input.Length];
            input.ReadExactly(raw);
            var e = JsonSerializer.Deserialize<CredentialEnvelope>(raw) ?? throw new CryptographicException();
            if (e.Version != 1 || e.Provider != "G" || e.Kdf != "scrypt" || e.N != Cost || e.R != 8 || e.P != 1 || e.Cipher != "AES-256-GCM")
                throw new CryptographicException();
            var salt = Convert.FromBase64String(e.Salt);
            var nonce = Convert.FromBase64String(e.Nonce);
            var ciphertext = Convert.FromBase64String(e.Ciphertext);
            var tag = Convert.FromBase64String(e.Tag);
            if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length is < 1 or > 4096) throw new CryptographicException();
            key = SCrypt.Generate(pin, salt, Cost, 8, 1, 32);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(ProviderId.G));
            return plaintext;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException or ArgumentException or NullReferenceException)
        {
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            throw new RdtException("CREDENTIAL_UNLOCK_FAILED");
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }
}
