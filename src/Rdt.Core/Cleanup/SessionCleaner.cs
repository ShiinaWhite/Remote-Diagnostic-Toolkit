using Rdt.Common;
using Rdt.Session;

namespace Rdt.Cleanup;

public enum CleanupResult { Complete, AlreadyAbsent, Active, Refused, Failed, Orphaned }

public sealed class SessionCleaner
{
    private readonly SessionPaths paths;
    public SessionCleaner(string? tempBase = null) => paths = new SessionPaths(tempBase);

    public CleanupResult Clean(string root) => CleanCore(root, null);

    public async Task<CleanupResult> CleanAsync(string root)
    {
        // Windows can briefly retain a file/directory handle after process exit.
        // Each attempt repeats ownership, link and lease validation. Never retry an
        // active lease, refused path or orphan as if it belonged to this invocation.
        var result = Clean(root);
        foreach (var delay in new[] { 50, 100, 200, 400, 800 })
        {
            if (result != CleanupResult.Failed) break;
            await Task.Delay(delay, CancellationToken.None);
            result = Clean(root);
        }
        return result;
    }

    // Only creation rollback can supply the exact marker it just wrote. This is not a
    // public bypass for age checks on sessions discovered on disk.
    internal CleanupResult CleanOwned(string root, SessionMarker owner) => CleanCore(root, owner);

    private CleanupResult CleanCore(string root, SessionMarker? owner)
    {
        root = Path.GetFullPath(root);
        if (!paths.IsDirectSession(root)) return CleanupResult.Refused;
        if (!Directory.Exists(root)) return CleanupResult.AlreadyAbsent;
        try
        {
            var marker = paths.ReadMarker(root);
            if (marker == null || (owner != null && marker != owner)) return CleanupResult.Refused;
            SessionPaths.ValidateTree(root);
            var leasePath = Path.Combine(root, ".lease");
            FileStream? lease;
            try
            {
                lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (FileNotFoundException)
            {
                // Missing is not active. A discovered orphan must age for 24 hours;
                // a failed creation has in-memory ownership of the exact fresh marker.
                if (owner == null && marker.CreatedUtc > DateTimeOffset.UtcNow.AddHours(-24))
                    return CleanupResult.Orphaned;
                // A failed creation has not started a runtime. Its rollback must not
                // depend on creating the same file whose creation just failed.
                // Serialize discovered orphan cleanup without overwriting a new lease.
                lease = owner != null ? null
                    : new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e) when (OperatingSystem.IsWindows() && (e.HResult & 0xffff) == 32)
            {
                // ERROR_SHARING_VIOLATION while opening the existing lease is the only
                // evidence of an active lease. Other I/O failures are never Active.
                return CleanupResult.Active;
            }
            // Keep marker and lease until all runtime material has been removed, to permit retries.
            using (lease)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    if (Path.GetFileName(path) is "owner.json" or ".lease") continue;
                    DeleteTree(path);
                }
            }
            File.Delete(leasePath);
            File.Delete(Path.Combine(root, "owner.json"));
            Directory.Delete(root);
            return CleanupResult.Complete;
        }
        catch (RdtException) { return CleanupResult.Refused; }
        catch (IOException) { return CleanupResult.Failed; }
        catch (UnauthorizedAccessException) { return CleanupResult.Failed; }
    }

    public IReadOnlyList<string> FindStale(DateTimeOffset now)
    {
        var stale = new List<string>();
        if (!Directory.Exists(paths.TempBase)) return stale;
        PrivateFiles.RejectLinks(paths.TempBase);
        foreach (var root in Directory.EnumerateDirectories(paths.TempBase, "RDT-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var marker = paths.ReadMarker(root);
                if (marker == null || marker.CreatedUtc > now.AddHours(-24)) continue;
                SessionPaths.ValidateTree(root);
                try
                {
                    using var lease = new FileStream(Path.Combine(root, ".lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (FileNotFoundException) { /* An aged, validated orphan is discoverable. */ }
                stale.Add(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RdtException) { /* Leave uncertain ownership untouched. */ }
        }
        return stale;
    }

    private static void DeleteTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new RdtException("UNSAFE_PATH");
        if (!Directory.Exists(path)) { File.Delete(path); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) DeleteTree(entry);
        Directory.Delete(path);
    }
}
