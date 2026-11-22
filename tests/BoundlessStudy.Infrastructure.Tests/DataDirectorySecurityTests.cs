using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Infrastructure.Tests;

/// <summary>
/// BS-NFR-SEC-009: a data directory created by the agent must not be readable by
/// every local user. Only the current user, SYSTEM, and Administrators keep access.
/// </summary>
public sealed class DataDirectorySecurityTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void New_directory_gets_a_protected_owner_only_dacl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(), "BoundlessStudy.Acl.Tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "data");

        try
        {
            Assert.True(DataDirectorySecurity.CreateWithOwnerOnlyAcl(directory));
            Assert.False(DataDirectorySecurity.CreateWithOwnerOnlyAcl(directory));

            var acl = new DirectoryInfo(directory).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);

            var rules = acl
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();

            var currentUser = WindowsIdentity.GetCurrent().User!;
            Assert.Contains(rules, rule =>
                rule.IdentityReference.Equals(currentUser)
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var builtinUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            Assert.DoesNotContain(rules, rule =>
                rule.IdentityReference.Equals(authenticatedUsers)
                || rule.IdentityReference.Equals(builtinUsers));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
