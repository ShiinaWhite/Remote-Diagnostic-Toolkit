using System.Security.Cryptography;
using System.Text.Json;
using Rdt.Common;
using Rdt.Session;

namespace Rdt.Providers;

public sealed record GProviderOptions(string BaseUrl, string ModelId)
{
    public int SchemaVersion { get; init; } = 1;
    public string ApiSchema { get; init; } = "openai-chat-completions";
    public string EndpointProfile { get; init; } = "custom";
    public static GProviderOptions Load(string file)
    {
        try
        {
            var options = JsonSerializer.Deserialize<GProviderOptions>(File.ReadAllBytes(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new RdtException("PROVIDER_OPTIONS_INVALID");
            options.Validate();
            return options;
        }
        catch (RdtException) { throw; }
        catch { throw new RdtException("PROVIDER_OPTIONS_INVALID"); }
    }
    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 128 || ModelId.Any(char.IsControl) ||
            SchemaVersion != 1 || ApiSchema != "openai-chat-completions" ||
            string.IsNullOrWhiteSpace(EndpointProfile) || EndpointProfile.Length > 64 || EndpointProfile.Any(char.IsControl))
            throw new RdtException("PROVIDER_OPTIONS_INVALID");
    }
}

public sealed class GProviderAdapter(GProviderOptions options) : IProviderAdapter
{
    public ProviderId Id => ProviderId.G;
    public void WriteTemporaryConfig(DiagnosticSession session, byte[] secret)
    {
        options.Validate();
        if (secret.Length is < 1 or > 4096) throw new RdtException("CREDENTIAL_INPUT_INVALID");
        Directory.CreateDirectory(Path.GetDirectoryName(session.ProviderFile)!);
        using var buffer = new MemoryStream();
        try
        {
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1);
                w.WriteStartObject("config"); w.WriteStartObject("providerConfigRules"); w.WriteStartArray("providerRules");
                w.WriteStartObject(); w.WriteString("providerId", "rdt-g"); w.WriteString("providerName", "G");
                w.WriteStartObject("config"); w.WriteString("group", "standard-personal");
                w.WriteStartObject("access"); w.WriteString("type", "api-key"); w.WriteString("apiKey", secret.AsSpan()); w.WriteEndObject();
                w.WriteStartObject("api"); w.WriteString("type", "openai-chat-completions"); w.WriteString("baseUrl", options.BaseUrl); w.WriteEndObject();
                w.WriteStartArray("personalModelIds"); w.WriteStringValue(options.ModelId); w.WriteEndArray();
                w.WriteEndObject(); w.WriteEndObject(); w.WriteEndArray(); w.WriteEndObject();
                w.WriteStartObject("modelConfigRules"); w.WriteStartArray("providerModelRules"); w.WriteEndArray();
                w.WriteStartArray("manualProviderModelRules"); w.WriteEndArray(); w.WriteEndObject();
                w.WriteStartObject("defaultModelSelection"); w.WriteString("providerId", "rdt-g"); w.WriteString("modelId", options.ModelId); w.WriteEndObject();
                w.WriteEndObject(); w.WriteEndObject();
            }
            PrivateFiles.WriteNew(session.ProviderFile, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
        finally { CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
    }
}
