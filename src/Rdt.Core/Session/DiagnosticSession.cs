using System.Text;
using System.Text.Json;
using Rdt.Common;

namespace Rdt.Session;

public sealed record SessionMarker(int Version, Guid Id, DateTimeOffset CreatedUtc);

public sealed class DiagnosticSession : IDisposable
{
    public Guid Id { get; }
    public string Root { get; }
    public string Home => Path.Combine(Root, "home");
    public string Data => Path.Combine(Root, "data");
    public string Workspace => Path.Combine(Root, "workspace");
    public string Runtime => Path.Combine(Root, "runtime");
    public string ProviderFile => Path.Combine(Data, ".zcode", "v2", "provider_config.json");
    private FileStream? lease;

    private DiagnosticSession(string tempBase, Guid id) { Id = id; Root = Path.Combine(Path.GetFullPath(tempBase), "RDT-" + id.ToString("N")); }

    public static DiagnosticSession Create(string? tempBase = null)
    {
        var session = new DiagnosticSession(tempBase ?? Path.GetTempPath(), Guid.NewGuid());
        PrivateFiles.CreateDirectory(session.Root);
        try
        {
            PrivateFiles.WriteNew(Path.Combine(session.Root, "owner.json"), JsonSerializer.SerializeToUtf8Bytes(new SessionMarker(1, session.Id, DateTimeOffset.UtcNow)));
            session.lease = new FileStream(Path.Combine(session.Root, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            foreach (var dir in new[] { session.Home, session.Data, session.Workspace, session.Runtime }) PrivateFiles.CreateDirectory(dir);
            return session;
        }
        catch { session.Dispose(); new Cleanup.SessionCleaner(tempBase ?? Path.GetTempPath()).Clean(session.Root); throw; }
    }

    public IReadOnlyDictionary<string, string> EnvironmentVariables()
    {
        // No inherited PATH, NODE_OPTIONS, API secrets, ZCODE overrides, proxy or shell hooks.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "SystemDrive" })
            if (Environment.GetEnvironmentVariable(name) is { } value) env[name] = value;
        env["HOME"] = Home;
        env["USERPROFILE"] = Home;
        env["APPDATA"] = Path.Combine(Home, "AppData", "Roaming");
        env["LOCALAPPDATA"] = Path.Combine(Home, "AppData", "Local");
        env["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config");
        env["XDG_CACHE_HOME"] = Path.Combine(Runtime, "cache");
        env["XDG_DATA_HOME"] = Data;
        env["TEMP"] = Runtime;
        env["TMP"] = Runtime;
        env["TMPDIR"] = Runtime;
        env["ZCODE_DATA_BASE_DIR"] = Data;
        env["ZCODE_STORAGE_DIR"] = Path.Combine(Home, ".zcode", "cli");
        env["ZCODE_SESSION_DB_PATH"] = Path.Combine(Home, ".zcode", "cli", "db", "db.sqlite");
        env["ZCODE_LOG_DIR"] = Path.Combine(Runtime, "log");
        env["ZCODE_MAILBOX_ROOT"] = Path.Combine(Data, "mailbox");
        // Entry point prepares the bundled builtin config when only personal path is supplied.
        env["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"] = ProviderFile;
        env["ZCODE_MESSAGE_ENABLED"] = "0";
        env["RDT_SESSION_ID"] = Id.ToString("N");
        return env;
    }

    public void WriteRuntimeConfig()
    {
        var dir = Path.Combine(Home, ".zcode", "cli");
        Directory.CreateDirectory(dir);
        PrivateFiles.WriteNew(Path.Combine(dir, "config.json"), Encoding.UTF8.GetBytes("""
        {
          "permission": { "mode": "plan" },
          "features": { "mcp": false, "subagent": false, "memory": false, "skill": false },
          "memory": { "use": false },
          "plugins": { "enabled": false },
          "skills": { "enabled": false },
          "hooks": { "enabled": false }
        }
        """));
    }

    public void Dispose() { lease?.Dispose(); lease = null; }
}
