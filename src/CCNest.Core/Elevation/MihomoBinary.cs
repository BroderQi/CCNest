using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CCNest.Core.Infrastructure;

namespace CCNest.Core.Elevation;

/// <summary>
/// Integrity handling for the mihomo binary that the elevated helper runs. Before running it with
/// administrator rights, the helper verifies the SHA-256 against the value pinned at build time and
/// copies it into an admin-only directory, so a standard-user process cannot swap the binary that
/// gets elevated.
/// </summary>
public static class MihomoBinary
{
    /// <summary>mihomo v1.19.33 windows-amd64-v1 (from the official release, zip digest verified).</summary>
    public const string PinnedVersion = "v1.19.33";
    public const string PinnedSha256 = "a4fbf2b8bc2b5416ce2d0d392ac59f2e0a234ea4fcf635b66bd21e62ea9641b9";

    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static bool IsPinned(string path) => File.Exists(path) && Sha256Of(path) == PinnedSha256;

    /// <summary>Elevated: copies a verified binary into %ProgramData%\CCNest\core and returns its path.</summary>
    public static string PrepareElevatedCopy(string sourcePath)
    {
        var dir = AppPaths.ElevatedCoreDir;
        CreateAdminOnlyDirectory(dir);
        var target = Path.Combine(dir, "mihomo.exe");
        if (File.Exists(target) && Sha256Of(target) == PinnedSha256) return target;

        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到代理内核", sourcePath);
        var hash = Sha256Of(sourcePath);
        if (hash != PinnedSha256)
            throw new InvalidOperationException($"代理内核校验失败（SHA-256 {hash[..12]}… 与内置值不符），拒绝以管理员权限运行");
        File.Copy(sourcePath, target, overwrite: true);
        if (Sha256Of(target) != PinnedSha256) throw new InvalidOperationException("复制后的内核校验失败");
        return target;
    }

    public static void CreateAdminOnlyDirectory(string dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(dir);
        if (info.Exists)
        {
            // A directory pre-created by a non-admin (or a reparse point) is not trusted: recreate it.
            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var trusted = owner != null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
            if (!trusted || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Log.Warn("Elevation", $"{dir} 的所有者不可信，重新创建");
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();
                else info.Delete(recursive: true);
                info = new DirectoryInfo(dir);
            }
        }
        if (!info.Exists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            info.Create(security);
        }
        else
        {
            info.SetAccessControl(security);
        }
        info.Refresh();
        var acl = info.GetAccessControl();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        info.SetAccessControl(acl);
    }
}
