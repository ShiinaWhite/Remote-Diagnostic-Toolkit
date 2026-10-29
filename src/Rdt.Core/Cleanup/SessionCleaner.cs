using System.Text.Json;
using Rdt.Common;
using Rdt.Session;

namespace Rdt.Cleanup;

public enum CleanupResult { Complete, AlreadyAbsent, Active, Refused, Failed }

public sealed class SessionCleaner
{
    private readonly string tempBase;
    public SessionCleaner(string? tempBase = null) => this.tempBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempBase ?? Path.GetTempPath()));

    public CleanupResult Clean(string root)
    {
        root = Path.GetFullPath(root);
        if (!IsDirectSession(root)) return CleanupResult.Refused;
        if (!Directory.Exists(root)) return CleanupResult.AlreadyAbsent;
        try
        {
            if (ReadMarker(root) == null) return CleanupResult.Refused;
            ValidateTree(root);
            // An active wrapper holds this with FileShare.None. Never delete a running session.
            using (new FileStream(Path.Combine(root, ".lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            // Keep marker and lease until all runtime material has been removed, to permit retries.
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                if (Path.GetFileName(path) is "owner.json" or ".lease") continue;
                DeleteTree(path);
            }
            File.Delete(Path.Combine(root, ".lease"));
            File.Delete(Path.Combine(root, "owner.json"));
            Directory.Delete(root);
            return CleanupResult.Complete;
        }
        catch (RdtException) { return CleanupResult.Refused; }
        catch (IOException) { return CleanupResult.Active; }
        catch (UnauthorizedAccessException) { return CleanupResult.Failed; }
    }

    public IReadOnlyList<string> FindStale(DateTimeOffset now)
    {
        var stale = new List<string>();
        if (!Directory.Exists(tempBase)) return stale;
        PrivateFiles.RejectLinks(tempBase);
        foreach (var root in Directory.EnumerateDirectories(tempBase, "RDT-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var marker = ReadMarker(root);
                if (marker == null || marker.CreatedUtc > now.AddHours(-24)) continue;
                using var lease = new FileStream(Path.Combine(root, ".lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                stale.Add(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RdtException) { /* Leave uncertain ownership untouched. */ }
        }
        return stale;
    }

    private bool IsDirectSession(string root) =>
        string.Equals(Path.GetDirectoryName(root), tempBase, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && Path.GetFileName(root).Length == 36 && Path.GetFileName(root).StartsWith("RDT-", StringComparison.Ordinal)
        && Guid.TryParseExact(Path.GetFileName(root)[4..], "N", out _);

    private SessionMarker? ReadMarker(string root)
    {
        if (!IsDirectSession(root)) return null;
        PrivateFiles.RejectLinks(root);
        var file = Path.Combine(root, "owner.json");
        if (!File.Exists(file) || new FileInfo(file).Length > 1024 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return null;
        try
        {
            var marker = JsonSerializer.Deserialize<SessionMarker>(File.ReadAllBytes(file));
            return marker is { Version: 1 } && marker.Id.ToString("N") == Path.GetFileName(root)[4..]
                && marker.CreatedUtc <= DateTimeOffset.UtcNow.AddMinutes(5) ? marker : null;
        }
        catch (JsonException) { return null; }
    }

    private static void ValidateTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new RdtException("UNSAFE_PATH");
        if (Directory.Exists(path))
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) ValidateTree(entry);
    }

    private static void DeleteTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new RdtException("UNSAFE_PATH");
        if (!Directory.Exists(path)) { File.Delete(path); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) DeleteTree(entry);
        Directory.Delete(path);
    }
}
