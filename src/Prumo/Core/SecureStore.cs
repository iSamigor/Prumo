using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Prumo.Core;

/// <summary>
/// Protects Prumo's ProgramData folder and writes its files atomically. The app runs elevated, so a
/// directory a standard user can add files to would let them plant a link where a config write lands.
/// </summary>
public static class SecureStore
{
    /// <summary>
    /// Creates <paramref name="dir"/> if needed and, when running elevated outside a PRUMO_HOME test
    /// directory, replaces its DACL with SYSTEM + Administrators full control and Users read/execute.
    /// Best effort: never throws, so a locked-down machine can still start.
    /// </summary>
    public static void HardenDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            if (Environment.GetEnvironmentVariable("PRUMO_HOME") is { Length: > 0 } || !IsElevated()) return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(security);
        }
        catch { /* ACL hardening is best effort */ }
    }

    /// <summary>
    /// Writes <paramref name="path"/> through a new uniquely named temp file in the same folder,
    /// then renames it over the target. UTF-8 without BOM; the temp file is removed on failure.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}.tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* nothing to clean up */ }
            throw;
        }
    }

    static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
