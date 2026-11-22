using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Creates the agent's data folders with a protected DACL (BS-NFR-SEC-009): only the
/// current Windows user, SYSTEM, and Administrators may read or write the database,
/// backups, and migration lock file. Existing folders are never re-permissioned by
/// startup; changing an existing DACL is an explicit administrative action.
/// </summary>
public static class DataDirectorySecurity
{
    /// <summary>
    /// Creates <paramref name="directory"/> when absent and applies the owner-only
    /// DACL to the newly created folder. Returns true only when this call created it.
    /// </summary>
    public static bool CreateWithOwnerOnlyAcl(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (Directory.Exists(directory))
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            ApplyOwnerOnlyAcl(directory);
        }

        return true;
    }

    /// <summary>
    /// Replaces inherited access with an explicit protected DACL for the current
    /// user, SYSTEM, and the built-in Administrators group. Windows-only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void ApplyOwnerOnlyAcl(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");

        AddFullControl(security, currentUser, inheritance);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), inheritance);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), inheritance);

        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControl(
        DirectorySecurity security,
        IdentityReference identity,
        InheritanceFlags inheritance)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
