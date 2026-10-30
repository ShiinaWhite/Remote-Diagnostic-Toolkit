using System.Text;
using System.Text.RegularExpressions;

namespace Rdt.Reporting;

// Terminal output only. Raw frames, errors and model text never enter AuditLog.
public sealed class SecretRedactor(byte[] secret) : IDisposable
{
    private string? value = Encoding.UTF8.GetString(secret);
    public string Redact(string text)
    {
        if (!string.IsNullOrEmpty(value))
        {
            text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            text = text.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), "[REDACTED]", StringComparison.Ordinal);
        }
        text = Regex.Replace(text, @"(?i)Bearer\s+\S+", "Bearer [REDACTED]");
        // Remove terminal control characters, including escape-sequence introducers.
        return string.Concat(text.Where(c => !char.IsControl(c) || c is '\n' or '\t'));
    }
    public void Dispose() => value = null;
}
