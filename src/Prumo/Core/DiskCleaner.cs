using System.Runtime.InteropServices;

namespace Prumo.Core;

public enum CleanCategory { UserTemp, WindowsTemp, UpdateDownloads, DeliveryOptimization, ErrorReports, CrashDumps, RecycleBin }

/// <summary>One kind of junk the cleanup can measure and delete.</summary>
public sealed record CleanTarget(CleanCategory Category, string Title, string Description, bool DefaultOn);

/// <summary>
/// The disk cleanup from PC Manager / Disk Cleanup, limited to what is safe to delete while Windows runs:
/// files in use are skipped, and temp/update files younger than a safety margin are left alone (an
/// installer or a download may still need them). Shader caches and browser data are left alone on
/// purpose: games rebuild shaders with stutter, browsers lose logins. Never throws.
/// </summary>
public static class DiskCleaner
{
    public static readonly IReadOnlyList<CleanTarget> Targets = new CleanTarget[]
    {
        new(CleanCategory.UserTemp, "Arquivos temporários do usuário", "Pasta Temp da sua conta (arquivos com mais de 1 dia)", true),
        new(CleanCategory.WindowsTemp, "Arquivos temporários do Windows", @"C:\Windows\Temp (arquivos com mais de 1 dia)", true),
        new(CleanCategory.UpdateDownloads, "Downloads do Windows Update", "Instaladores de atualizações já baixados (com mais de 10 dias)", true),
        new(CleanCategory.DeliveryOptimization, "Otimização de Entrega", "Cópias de atualizações guardadas para compartilhar com outros PCs", true),
        new(CleanCategory.ErrorReports, "Relatórios de erro", "Relatórios do Windows sobre programas que travaram", true),
        new(CleanCategory.CrashDumps, "Despejos de memória", "Arquivos .dmp de travamentos (servem só para diagnóstico)", true),
        new(CleanCategory.RecycleBin, "Lixeira", "Esvazia a Lixeira de todos os discos", false),
    };

    static string Windir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>The folders of a category and the minimum age of a file before it may go.</summary>
    static (string[] Dirs, TimeSpan MinAge, string Pattern) Spec(CleanCategory c) => c switch
    {
        CleanCategory.UserTemp => (new[] { Path.GetTempPath() }, TimeSpan.FromDays(1), "*"),
        CleanCategory.WindowsTemp => (new[] { Path.Combine(Windir, "Temp") }, TimeSpan.FromDays(1), "*"),
        CleanCategory.UpdateDownloads => (new[] { Path.Combine(Windir, @"SoftwareDistribution\Download") }, TimeSpan.FromDays(10), "*"),
        CleanCategory.DeliveryOptimization => (new[] { Path.Combine(Windir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") }, TimeSpan.Zero, "*"),
        CleanCategory.ErrorReports => (new[]
        {
            Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
            Path.Combine(LocalAppData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(LocalAppData, @"Microsoft\Windows\WER\ReportQueue"),
        }, TimeSpan.Zero, "*"),
        CleanCategory.CrashDumps => (new[] { Path.Combine(Windir, "Minidump"), Path.Combine(LocalAppData, "CrashDumps"), Windir }, TimeSpan.Zero, "*.dmp"),
        _ => (Array.Empty<string>(), TimeSpan.Zero, "*"),
    };

    /// <summary>Bytes the category would free now. Slow on big folders: call off the UI thread.</summary>
    public static long Measure(CleanCategory c, CancellationToken ct = default)
    {
        if (c == CleanCategory.RecycleBin) return RecycleBinBytes();
        long total = 0;
        foreach (var f in Files(c, ct)) total += OnDisk(f);
        return total;
    }

    /// <summary>Deletes the category's files; returns bytes freed and files skipped (in use or protected).</summary>
    public static (long Freed, int Skipped) Clean(CleanCategory c, CancellationToken ct = default)
    {
        if (c == CleanCategory.RecycleBin)
        {
            long bytes = RecycleBinBytes();
            // NOCONFIRMATION | NOPROGRESSUI | NOSOUND
            return SHEmptyRecycleBinW(0, null, 0x7) == 0 ? (bytes, 0) : (0, 1);
        }

        long freed = 0; int skipped = 0;
        foreach (var f in Files(c, ct).ToList())
        {
            try
            {
                long len = OnDisk(f);
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                f.Delete();
                freed += len;
            }
            catch { skipped++; }
        }
        // Empty folders left behind (only below the category's roots, never the roots themselves).
        var (dirs, _, pattern) = Spec(c);
        if (pattern == "*")
            foreach (var root in dirs) DeleteEmptyDirs(root, ct);
        return (freed, skipped);
    }

    static IEnumerable<FileInfo> Files(CleanCategory c, CancellationToken ct)
    {
        var (dirs, minAge, pattern) = Spec(c);
        var cutoff = DateTime.UtcNow - minAge;
        // Crash dumps in C:\Windows itself: only MEMORY.DMP at the top, never a recursive walk of Windows.
        var top = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var deep = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            bool windowsRoot = string.Equals(dir.TrimEnd('\\'), Windir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(dir).EnumerateFiles(windowsRoot ? "MEMORY.DMP" : pattern, windowsRoot ? top : deep); }
            catch { continue; }
            using var e = files.GetEnumerator();
            while (true)
            {
                if (ct.IsCancellationRequested) yield break;
                FileInfo f;
                try { if (!e.MoveNext()) break; f = e.Current; }
                catch { break; }
                bool old;
                try { old = f.LastWriteTimeUtc <= cutoff && f.CreationTimeUtc <= cutoff; }
                catch { continue; }
                if (old) yield return f;
            }
        }
    }

    /// <summary>Space the file really takes: a sparse or compressed file (RPCS3 leaves a 32 GB sparse
    /// one in Temp) frees far less than its length.</summary>
    static long OnDisk(FileInfo f)
    {
        try
        {
            uint low = GetCompressedFileSizeW(f.FullName, out uint high);
            if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) return f.Length;
            return (long)high << 32 | low;
        }
        catch { return 0; }
    }

    static void DeleteEmptyDirs(string root, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            // Deepest first, so a parent is empty once its children are gone.
            foreach (var d in Directory.EnumerateDirectories(root, "*", opts).OrderByDescending(d => d.Length))
            {
                if (ct.IsCancellationRequested) return;
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch { }
            }
        }
        catch { }
    }

    static long RecycleBinBytes()
    {
        try
        {
            var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBinW(null, ref info) == 0 ? info.SizeBytes : 0;
        }
        catch { return 0; }
    }

    /// <summary>Clears the DNS resolver cache (ipconfig /flushdns).</summary>
    public static bool FlushDns()
    {
        try { return DnsFlushResolverCache(); }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ShQueryRbInfo
    {
        public int Size;
        public long SizeBytes;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBinW(nint hwnd, string? rootPath, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetCompressedFileSizeW(string path, out uint high);

    [DllImport("dnsapi.dll")]
    static extern bool DnsFlushResolverCache();
}
