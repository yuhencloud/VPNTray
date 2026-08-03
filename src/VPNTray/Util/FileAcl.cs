using System.Security.AccessControl;
using System.Security.Principal;

namespace VPNTray.Util;

public static class FileAcl
{
    /// <summary>Windows equivalent of chmod 0600 — owner full control, no inheritance.</summary>
    public static void RestrictToCurrentUser(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var security = new FileSecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception)
        {
            // ACL hardening is best-effort; never block the caller on it.
        }
    }
}
