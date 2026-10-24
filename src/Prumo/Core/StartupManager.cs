using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Prumo.Core;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed record StartupEntry(
    string Name,          // value name, or the .lnk file name without extension
    string Command,       // command line, or the shortcut's target path (fall back to the .lnk path)
    StartupSource Source,
    bool Enabled,
    string Location);     // human text in PT-BR, e.g. "Registro (usuário)", "Pasta Inicializar (todos)"

/// <summary>Programs Windows launches at logon: the Run keys and the Startup folders. Never throws.</summary>
public static class StartupManager
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";

    // Task Manager only hides these extensions; anything else in the folder is not a startup item.
    private static readonly HashSet<string> FolderExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".exe", ".bat", ".cmd" };

    /// <summary>Every startup entry from the Run keys and the Startup folders, sorted by name. Never throws.</summary>
    public static List<StartupEntry> List()
    {
        var entries = new List<StartupEntry>();
        foreach (var source in Enum.GetValues<StartupSource>())
        {
            Guard(() =>
            {
                if (source is StartupSource.UserFolder or StartupSource.CommonFolder) AddFolder(entries, source);
                else AddRunKey(entries, source);
            });
        }
        entries.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return entries;
    }

    /// <summary>Enables or disables an entry the way Task Manager does (StartupApproved). Never throws.</summary>
    public static bool SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            using var root = OpenRoot(entry.Source);
            // The value name for a Startup folder item is the file name; on disk is the truth.
            string name = entry.Source is StartupSource.UserFolder or StartupSource.CommonFolder
                ? FolderValueName(entry, root)
                : entry.Name;
            if (string.IsNullOrEmpty(name)) return false;

            // CreateSubKey also opens an existing key; the app runs elevated, so HKLM is writable.
            using var approved = root.CreateSubKey(ApprovedSubKey(entry.Source));
            if (approved is null) return false;

            approved.SetValue(name, ApprovedBytes(enabled), RegistryValueKind.Binary);
            return true;
        }
        catch { return false; }
    }

    // ---- reading ----

    private static void AddRunKey(List<StartupEntry> entries, StartupSource source)
    {
        using var root = OpenRoot(source);
        using var key = root.OpenSubKey(RunSubKey(source));
        if (key is null) return;

        var approved = ReadApproved(source);
        foreach (string name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;   // the unnamed default value is never a command
            // DoNotExpand: show the command as stored, like Task Manager does.
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command) continue;
            entries.Add(new StartupEntry(name, command, source, IsEnabled(approved, name), LocationOf(source)));
        }
    }

    private static void AddFolder(List<StartupEntry> entries, StartupSource source)
    {
        string dir = FolderPath(source);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        var approved = ReadApproved(source);
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string ext = Path.GetExtension(file);
            if (!FolderExtensions.Contains(ext)) continue;   // desktop.ini and stray files stay out

            string full = Path.GetFullPath(file);
            string name = Path.GetFileNameWithoutExtension(full);
            string command = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ShortcutTarget(full) ?? full
                : full;

            // Some builds key these entries by the file name without the extension.
            bool enabled = IsEnabled(approved, Path.GetFileName(full)) && IsEnabled(approved, name);
            entries.Add(new StartupEntry(name, command, source, enabled, LocationOf(source)));
        }
    }

    private static Dictionary<string, bool> ReadApproved(StartupSource source)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var root = OpenRoot(source);
        using var key = root.OpenSubKey(ApprovedSubKey(source));
        if (key is null) return map;
        foreach (string name in key.GetValueNames())
            map[name] = ApprovedValue(key.GetValue(name));
        return map;
    }

    /// <summary>Byte 0 even means enabled; odd means disabled. No value at all means enabled.</summary>
    private static bool ApprovedValue(object? value) =>
        value is not byte[] { Length: >= 1 } data || (data[0] & 1) == 0;

    private static bool IsEnabled(Dictionary<string, bool> map, string name) =>
        !map.TryGetValue(name, out bool enabled) || enabled;

    // ---- writing ----

    // 12 bytes: 0x02 enabled / 0x03 disabled, then zero, then the FILETIME Task Manager stamps.
    private static byte[] ApprovedBytes(bool enabled)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)0x02 : (byte)0x03;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }

    // The StartupApproved name of a folder item: an existing value wins, then the file on disk.
    private static string FolderValueName(StartupEntry entry, RegistryKey root)
    {
        using (var key = root.OpenSubKey(ApprovedSubKey(entry.Source)))
        {
            string? match = key?.GetValueNames()
                .FirstOrDefault(n => string.Equals(Path.GetFileNameWithoutExtension(n), entry.Name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match)) return match;
        }

        string dir = FolderPath(entry.Source);
        string? file = null;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            file = Directory.EnumerateFiles(dir)
                .Select(Path.GetFileName)
                .FirstOrDefault(n => string.Equals(Path.GetFileNameWithoutExtension(n), entry.Name, StringComparison.OrdinalIgnoreCase));

        return file ?? entry.Name + ".lnk";
    }

    // ---- shortcuts ----

    private static string? ShortcutTarget(string lnkPath)
    {
        try
        {
            if (Type.GetTypeFromProgID("WScript.Shell") is not { } shellType) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(lnkPath);
                string? target = (string)link.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }   // advertised MSI shortcuts and broken links fall back to the .lnk path
    }

    // ---- locations ----

    private static RegistryKey OpenRoot(StartupSource source) =>
        RegistryKey.OpenBaseKey(
            source is StartupSource.UserRun or StartupSource.UserFolder ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            RegistryView.Registry64);

    private static string RunSubKey(StartupSource source) => source switch
    {
        StartupSource.MachineRun32 => @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        _ => CurrentVersion + @"\Run",
    };

    private static string ApprovedSubKey(StartupSource source) => source switch
    {
        StartupSource.MachineRun32 => CurrentVersion + @"\Explorer\StartupApproved\Run32",
        StartupSource.UserRun or StartupSource.MachineRun => CurrentVersion + @"\Explorer\StartupApproved\Run",
        _ => CurrentVersion + @"\Explorer\StartupApproved\StartupFolder",
    };

    private static string FolderPath(StartupSource source) => source switch
    {
        StartupSource.UserFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        StartupSource.CommonFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        _ => "",
    };

    private static string LocationOf(StartupSource source) => source switch
    {
        StartupSource.UserRun => "Registro (usuário)",
        StartupSource.MachineRun => "Registro (todos)",
        StartupSource.MachineRun32 => "Registro (todos, 32 bits)",
        StartupSource.UserFolder => "Pasta Inicializar (usuário)",
        _ => "Pasta Inicializar (todos)",
    };

    private static void Guard(Action action)
    {
        try { action(); } catch { /* one unreadable hive must not hide the others */ }
    }
}
