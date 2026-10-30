using System.Text.Json;
using Rdt.Common;

namespace Rdt.Session;

// Shared identity checks for cleanup and report placement. A prefix alone is not an identity.
internal sealed class SessionPaths
{
    internal string TempBase { get; }
    internal static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal SessionPaths(string? tempBase = null) =>
        TempBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempBase ?? Path.GetTempPath()));

    internal bool IsDirectSession(string root) =>
        string.Equals(Path.GetDirectoryName(root), TempBase, Comparison)
        && Path.GetFileName(root) is { Length: 36 } name
        && name.StartsWith("RDT-", StringComparison.Ordinal)
        && Guid.TryParseExact(name[4..], "N", out _);

    internal SessionMarker? ReadMarker(string root)
    {
        if (!IsDirectSession(root)) return null;
        PrivateFiles.RejectLinks(root);
        var file = Path.Combine(root, "owner.json");
        PrivateFiles.RejectLinks(file);
        if (!File.Exists(file) || new FileInfo(file).Length > 1024) return null;
        try
        {
            var marker = JsonSerializer.Deserialize<SessionMarker>(File.ReadAllBytes(file));
            return marker is { Version: 1 } && marker.Id.ToString("N") == Path.GetFileName(root)[4..]
                && marker.CreatedUtc > DateTimeOffset.MinValue
                && marker.CreatedUtc <= DateTimeOffset.UtcNow.AddMinutes(5) ? marker : null;
        }
        catch (JsonException) { return null; }
    }

    internal bool ContainsSessionPath(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
        {
            // Reserve exact session names under this temp root, even before the marker exists
            // or after it is damaged. Report creation must never populate a partial session.
            if (IsDirectSession(dir.FullName)) return true;
            // Also recognize a marked session from a different configured temp root.
            // ReadMarker applies the same exact name, GUID, link and marker checks.
            if (dir.Parent != null && new SessionPaths(dir.Parent.FullName).ReadMarker(dir.FullName) != null)
                return true;
        }
        return false;
    }

    internal static void ValidateTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new RdtException("UNSAFE_PATH");
        if (Directory.Exists(path))
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) ValidateTree(entry);
    }
}
