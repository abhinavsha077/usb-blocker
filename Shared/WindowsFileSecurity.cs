using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace USBGuardian.Shared;

internal static class WindowsFileSecurity
{
    public static void RestrictExistingFileToSystemAndAdministrators(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        EnsureRuntimeParentDirectory(path);
        if (!File.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Protected runtime files must not be links.");
        // A pre-created ProgramData file must not become trusted just because
        // the LocalSystem service replaces its ACL after reading it.
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (WindowsIdentity.GetCurrent().User?.Equals(system) == true)
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (owner == null || (!owner.Equals(system) && !owner.Equals(admins)))
                throw new IOException("Runtime file owner is not SYSTEM or Administrators; administrator repair is required.");
        }
        new FileInfo(path).SetAccessControl(CreateFileSecurity());
    }

    public static void WriteAllTextAtomically(
        string path,
        string contents,
        bool restrictToSystemAndAdministrators)
    {
        EnsureRuntimeParentDirectory(path);
        string tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (restrictToSystemAndAdministrators && OperatingSystem.IsWindows())
                WriteProtectedFile(tempPath, contents);
            else
                File.WriteAllText(tempPath, contents);

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // The original write exception is more useful than cleanup failure.
            }
        }
    }

    public static void AppendAllTextProtected(string path, string contents)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.AppendAllText(path, contents);
            return;
        }

        if (File.Exists(path))
        {
            RestrictExistingFileToSystemAndAdministrators(path);
            using var existing = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var existingWriter = new StreamWriter(existing);
            existingWriter.Write(contents);
            existingWriter.Flush();
            existing.Flush(flushToDisk: true);
            return;
        }

        EnsureRuntimeParentDirectory(path);

        using var stream = FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.None,
            CreateFileSecurity());
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static void EnsureRuntimeParentDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string root = @"C:\ProgramData\USBGuardian";
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        string parent = Path.GetDirectoryName(fullPath)!;
        RestrictRuntimeDirectory(root);
        string relative = Path.GetRelativePath(root, parent);
        if (relative == ".") return;
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            RestrictRuntimeDirectory(current);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void RestrictRuntimeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var directory = new DirectoryInfo(path);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Runtime directories must not be links.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User! })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        directory.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteProtectedFile(string path, string contents)
    {
        using var stream = FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            CreateFileSecurity());
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateFullControlRule(WellKnownSidType.LocalSystemSid));
        security.AddAccessRule(CreateFullControlRule(
            WellKnownSidType.BuiltinAdministratorsSid));

        // A Windows service runs as LocalSystem, but local development and the
        // test suite run under the interactive user's filtered UAC token. In
        // that token the Administrators SID is deny-only, so an ACL containing
        // only SYSTEM and Administrators makes the newly created file
        // inaccessible to the process that still needs to atomically rename
        // and subsequently read it. Grant that process identity explicitly.
        // LocalSystem deployments keep the original SYSTEM/Administrators-only
        // ACL because the current SID is already SYSTEM.
        var currentUser = WindowsIdentity.GetCurrent().User;
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
        if (currentUser != null && !currentUser.Equals(localSystem))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule CreateFullControlRule(
        WellKnownSidType sidType) =>
        new(
            new SecurityIdentifier(sidType, domainSid: null),
            FileSystemRights.FullControl,
            AccessControlType.Allow);
}
