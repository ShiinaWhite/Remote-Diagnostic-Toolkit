using System.Security.AccessControl;
using System.Security.Principal;

namespace Rdt.Common;

public static class PrivateFiles
{
    // Reject existing links in every ancestor; do not change the ACL of existing user directories.
    public static void RejectLinks(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new RdtException("UNSAFE_PATH");
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new RdtException("UNSAFE_PATH");
    }

    public static void CreateDirectory(string path)
    {
        RejectLinks(path);
        if (Directory.Exists(path)) throw new RdtException("PATH_ALREADY_EXISTS");
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User ?? throw new RdtException("IDENTITY_UNAVAILABLE");
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void WriteNew(string path, ReadOnlySpan<byte> content)
    {
        RejectLinks(path);
        using var stream = OpenPrivateNew(path);
        stream.Write(content);
        stream.Flush(true);
    }

    private static FileStream OpenPrivateNew(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, acl);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }
}
