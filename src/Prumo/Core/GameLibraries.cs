using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Prumo.Core;

/// <summary>A folder where a launcher keeps its games. <see cref="Path"/> is normalized and has no trailing separator.</summary>
public sealed record GameLibrary(string Launcher, string Path);

/// <summary>Discovers the game folders of the launchers installed on this machine. Never throws.</summary>
public static class GameLibraries
{
    private static readonly Regex VdfPath = new(@"^\s*""path""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
    private static readonly Regex VdfLegacy = new(@"^\s*""\d+""\s+""((?:[^""\\]|\\.)*)""");

    public static IReadOnlyList<GameLibrary> Find()
    {
        var found = new List<GameLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string launcher, string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full = Path.GetFullPath(path.Replace('/', '\\').Trim().Trim('"'));
                if (full.Length > 3) full = full.TrimEnd('\\');
                if (!Directory.Exists(full) || !seen.Add(full)) return;
                found.Add(new GameLibrary(launcher, full));
            }
            catch { /* malformed path */ }
        }

        Guard(() => AddSteam(Add));
        Guard(() => AddEpic(Add));
        Guard(() => AddRiot(Add));
        Guard(() => AddGog(Add));
        Guard(() => AddEa(Add));
        Guard(() => AddUbisoft(Add));
        Guard(() => AddXbox(Add));
        return found;
    }

    private static void Guard(Action action)
    {
        try { action(); } catch { /* one broken launcher must not hide the others */ }
    }

    private static string? RegString(RegistryKey root, string subKey, string name)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }

    // ---- Steam ----
    private static void AddSteam(Action<string, string?> add)
    {
        string? dir = RegString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                   ?? RegString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                   ?? RegString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (string.IsNullOrWhiteSpace(dir)) return;

        dir = dir.Replace('/', '\\').TrimEnd('\\');
        var libs = new List<string>();
        string vdf = Path.Combine(dir, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libs.AddRange(ParseVdfLibraries(vdf));
        if (libs.Count == 0) libs.Add(dir); // no vdf yet: at least the install's own library

        foreach (var lib in libs) add("Steam", Path.Combine(lib, "steamapps", "common"));
    }

    private static IEnumerable<string> ParseVdfLibraries(string file)
    {
        var libs = new List<string>();
        foreach (var raw in File.ReadLines(file))
        {
            var m = VdfPath.Match(raw);
            if (!m.Success)
            {
                m = VdfLegacy.Match(raw);
                if (!m.Success) continue;
            }

            string value = m.Groups[1].Value.Replace(@"\\", @"\").Replace("\\\"", "\"");
            // Legacy lines are "<index>" "<path>"; skip anything that is not path-shaped.
            if (m.Groups[1].Value == value && !value.Contains(':') && !value.StartsWith(@"\\")) continue;
            if (value.Length > 2) value = value.TrimEnd('\\');
            libs.Add(value);
        }
        return libs;
    }

    // ---- Epic ----
    private static void AddEpic(Action<string, string?> add)
    {
        string manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("InstallLocation", out var el)) continue;
                string? install = el.GetString();
                if (string.IsNullOrWhiteSpace(install)) continue;
                add("Epic", Path.GetDirectoryName(install.Replace('/', '\\')));
            }
            catch { /* skip broken manifest */ }
        }
    }

    // ---- Riot ----
    private static void AddRiot(Action<string, string?> add)
    {
        string root = @"C:\Riot Games";
        try
        {
            string json = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "RiotClientInstalls.json");
            if (File.Exists(json))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("rc_live", out var el) && el.GetString() is { } client)
                {
                    string? dir = Path.GetDirectoryName(Path.GetDirectoryName(client.Replace('/', '\\')));
                    if (!string.IsNullOrEmpty(dir)) root = dir;
                }
            }
        }
        catch { /* fall back to the default folder */ }
        add("Riot", root);
    }

    // ---- GOG ----
    private static void AddGog(Action<string, string?> add)
    {
        foreach (var sub in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            try
            {
                using var games = Registry.LocalMachine.OpenSubKey(sub);
                if (games is null) continue;
                foreach (var name in games.GetSubKeyNames())
                {
                    using var key = games.OpenSubKey(name);
                    if (key?.GetValue("path") is string path)
                        add("GOG", Path.GetDirectoryName(path.Replace('/', '\\')));
                }
            }
            catch { /* skip broken key */ }
        }
    }

    // ---- EA app ----
    private static void AddEa(Action<string, string?> add)
    {
        add("EA", @"C:\Program Files\EA Games");
        add("EA", @"C:\Program Files (x86)\EA Games");
    }

    // ---- Ubisoft Connect ----
    private static void AddUbisoft(Action<string, string?> add)
    {
        string? dir = RegString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir")
                   ?? RegString(Registry.LocalMachine, @"SOFTWARE\Ubisoft\Launcher", "InstallDir");
        if (string.IsNullOrWhiteSpace(dir)) return;

        dir = dir.Replace('/', '\\').TrimEnd('\\');
        add("Ubisoft", Path.Combine(dir, "games"));
        string? parent = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(parent)) add("Ubisoft", Path.Combine(parent, "games"));
    }

    // ---- Xbox app ----
    private static void AddXbox(Action<string, string?> add)
    {
        add("Xbox", @"C:\XboxGames");
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory) continue;
                    add("Xbox", Path.Combine(drive.RootDirectory.FullName, "XboxGames"));
                }
                catch { /* drive vanished */ }
            }
        }
        catch { /* enumeration failed */ }
    }
}
