using System.Security.AccessControl;
using System.Security.Principal;
using USBGuardian.Shared;

namespace USBGuardianTests;

public sealed class RuntimeDirectorySecurityTests
{
    [Fact]
    public void RuntimeDirectoryAllowsUsersToReadAlertsButNotReplaceServiceFiles()
    {
        string path = Path.Combine(Path.GetTempPath(), "usb-runtime-acl", Guid.NewGuid().ToString("N"));
        try
        {
            WindowsFileSecurity.RestrictRuntimeDirectory(path);
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(WindowsIdentity.GetCurrent().User, security.GetOwner(typeof(SecurityIdentifier)));
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            var users = Assert.Single(rules, rule => rule.IdentityReference.Value == "S-1-5-32-545");
            Assert.Equal(0, (int)(users.FileSystemRights & (FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions)));
            Assert.NotEqual(0, (int)(users.FileSystemRights & FileSystemRights.Read));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}
