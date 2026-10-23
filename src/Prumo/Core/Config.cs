using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prumo.Core;

public enum CpuPriority { Idle, BelowNormal, Normal, AboveNormal, High, Realtime }
public enum IoPriority { VeryLow = 0, Low = 1, Normal = 2, High = 3 }
public enum GpuPriority { Idle = 0, BelowNormal = 1, Normal = 2, AboveNormal = 3, High = 4, Realtime = 5 }
public enum EfficiencyMode { Off, On }
public enum MemoryPriority { VeryLow = 1, Low = 2, Medium = 3, BelowNormal = 4, Normal = 5 }
public enum GpuPreference { PowerSaving = 1, HighPerformance = 2 }

/// <summary>"When the process goes over ThresholdPercent, keep it on Cores logical CPUs for DurationMs."</summary>
public sealed class CpuLimit
{
    public double ThresholdPercent { get; set; } = 50;
    public int Cores { get; set; } = 4;
    public int DurationMs { get; set; } = 10_000;
}

/// <summary>
/// What to apply to every process matching <see cref="Exe"/>: an executable name ("game.exe"), or a
/// wildcard pattern; a pattern with a backslash is matched against the full path ("E:\Steam\steamapps\common\*").
/// </summary>
public sealed class ProcessRule
{
    public string Exe { get; set; } = "";
    public CpuPriority? CpuPriority { get; set; }
    public IoPriority? IoPriority { get; set; }
    public GpuPriority? GpuPriority { get; set; }
    /// <summary>Logical CPUs, e.g. "1-7" or "0,2,4-6". Null = leave alone.</summary>
    public string? Affinity { get; set; }
    public EfficiencyMode? Efficiency { get; set; }
    /// <summary>Power plan (friendly name) while this process runs ("modo jogo"). Null = none.</summary>
    public string? PowerPlan { get; set; }
    /// <summary>Exclude from ProBalance.</summary>
    public bool ProBalanceExcluded { get; set; }
    public MemoryPriority? MemoryPriority { get; set; }
    /// <summary>Soft affinity (Windows CPU Sets): the scheduler prefers these CPUs. Same syntax as Affinity.</summary>
    public string? CpuSets { get; set; }
    /// <summary>True = turn off Windows' dynamic priority boost for this process's threads.</summary>
    public bool? DisablePriorityBoost { get; set; }
    /// <summary>Which GPU DirectX picks for this executable (Windows graphics settings); applies on its next start.</summary>
    public GpuPreference? GpuPreference { get; set; }
    public CpuLimit? CpuLimit { get; set; }
    /// <summary>Executable names (wildcards allowed) a pattern rule skips, e.g. tools that live in a game library.</summary>
    public List<string>? Except { get; set; }
    /// <summary>Keep the PC from sleeping while this process runs (downloads, renders).</summary>
    public bool PreventSleep { get; set; }

    /// <summary>Non-games found in game libraries: Wallpaper Engine, crash reporters, VR runtimes, redistributable installers.</summary>
    public static readonly string[] NotGames =
    {
        "wallpaper32.exe", "wallpaper64.exe", "webwallpaper32.exe", "ui32.exe", "launcher.exe",
        "*crashhandler*.exe", "*crashreport*.exe", "*crashpad*.exe", "unitycrashhandler*.exe",
        "vrserver.exe", "vrmonitor.exe", "vrcompositor.exe", "vrdashboard.exe", "vrwebhelper.exe",
        "vc_redist*.exe", "dxsetup.exe", "dotnet*.exe", "ueprereqsetup*.exe", "easyanticheat_setup.exe",
    };

    [JsonIgnore] public bool IsPattern => Exe.Contains('*') || Exe.Contains('?') || Exe.Contains('\\');
    [JsonIgnore] public bool MatchesPath => Exe.Contains('\\');

    [JsonIgnore] public bool IsEmpty =>
        CpuPriority is null && IoPriority is null && GpuPriority is null && Affinity is null &&
        Efficiency is null && PowerPlan is null && !ProBalanceExcluded && MemoryPriority is null &&
        CpuSets is null && DisablePriorityBoost is null && GpuPreference is null && CpuLimit is null && !PreventSleep;
}

public enum WatchMetric { Cpu, MemoryMb }
public enum WatchAction { Log, LowerPriority, TrimMemory, Restart, Kill }

/// <summary>"When Exe stays over Threshold (CPU % or MB of RAM) for ForSeconds, do Action."</summary>
public sealed class WatchdogRule
{
    public string Exe { get; set; } = "";
    public WatchMetric Metric { get; set; } = WatchMetric.Cpu;
    public double Threshold { get; set; } = 50;
    public int ForSeconds { get; set; } = 30;
    public WatchAction Action { get; set; } = WatchAction.Log;
    public bool Enabled { get; set; } = true;
}

public sealed class IdleSaverSettings
{
    public bool Enabled { get; set; }
    /// <summary>Minutes without keyboard/mouse input before the PC counts as idle.</summary>
    public int IdleMinutes { get; set; } = 5;
    /// <summary>Power plan while idle (friendly name).</summary>
    public string? IdlePlan { get; set; }
}

public sealed class ForegroundBoostSettings
{
    public bool Enabled { get; set; }
    public CpuPriority Priority { get; set; } = CpuPriority.AboveNormal;
    /// <summary>Only boost processes that are at Normal priority.</summary>
    public bool OnlyNormal { get; set; } = true;
}

public sealed class SmartTrimSettings
{
    public bool TrimWorkingSets { get; set; }
    /// <summary>Only trim while physical memory load is at least this (%).</summary>
    public double MinRamLoadPercent { get; set; } = 65;
    /// <summary>Only trim processes using more than this (MB).</summary>
    public int MinProcessMb { get; set; } = 196;
    public int IntervalMinutes { get; set; } = 15;
    public bool PurgeStandbyList { get; set; }
    /// <summary>Purge the standby list when available memory drops below this (MB).</summary>
    public int PurgeBelowFreeMb { get; set; } = 1024;
    public List<string> Exclusions { get; set; } = new();
}

public sealed class ProBalanceSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Total CPU (%) before anything is restrained.</summary>
    public double TotalCpuThreshold { get; set; } = 11;
    /// <summary>Per-process CPU (% of the whole system) before it is restrained.</summary>
    public double ProcessCpuThreshold { get; set; } = 8;
    /// <summary>How long a process must stay over the threshold.</summary>
    public int TimeOverQuotaMs { get; set; } = 900;
    /// <summary>Restore once the process falls below this (%).</summary>
    public double ProcessCpuRestore { get; set; } = 4;
    public int MinRestraintMs { get; set; } = 4200;
    /// <summary>0 = no limit.</summary>
    public int MaxRestraintMs { get; set; } = 0;
    public bool ExcludeForeground { get; set; } = true;
    public bool ExcludeChildrenOfForeground { get; set; } = true;
    public bool ExcludeServices { get; set; } = true;
    public bool OnlyNormalPriority { get; set; } = true;
    public bool LowerToIdle { get; set; } = false;
    public bool LowerIoPriority { get; set; } = false;
    public List<string> Exclusions { get; set; } = new();
}

public sealed class AppConfig
{
    public const int CurrentSchema = 1;
    /// <summary>Format version of config.json, so later versions can migrate old files.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;
    public List<ProcessRule> Rules { get; set; } = new();
    /// <summary>Executables closed as soon as they start (wildcards allowed).</summary>
    public List<string> Disallowed { get; set; } = new();
    /// <summary>Only log disallowed processes instead of closing them.</summary>
    public bool DisallowedLogOnly { get; set; }
    /// <summary>Executables started again when they close (wildcards allowed).</summary>
    public List<string> KeepRunning { get; set; } = new();
    public List<WatchdogRule> Watchdog { get; set; } = new();
    public IdleSaverSettings IdleSaver { get; set; } = new();
    public ProBalanceSettings ProBalance { get; set; } = new();
    /// <summary>Plan to go back to when no "modo jogo" process runs. Null = the plan active before.</summary>
    public string? DefaultPowerPlan { get; set; }
    public int UpdateIntervalMs { get; set; } = 1000;
    public ForegroundBoostSettings ForegroundBoost { get; set; } = new();
    public SmartTrimSettings SmartTrim { get; set; } = new();
    /// <summary>"Modo desempenho": keep <see cref="PerformancePlan"/> active regardless of what runs.</summary>
    public bool PerformanceMode { get; set; }
    public string? PerformancePlan { get; set; }
    /// <summary>Requested system timer resolution in 100 ns units (5000 = 0.5 ms); 0 = leave Windows alone.</summary>
    public int TimerResolution100ns { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>UI theme: Azul, Claro or Preto.</summary>
    public string Theme { get; set; } = "Azul";
    public bool ImportedFromProcessLasso { get; set; }

    /// <summary>%ProgramData%\Prumo, or PRUMO_HOME (used for testing without touching the real config).</summary>
    public static string Dir => Environment.GetEnvironmentVariable("PRUMO_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Prumo");
    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        if (File.Exists(FilePath))
        {
            string? text = null;
            for (int i = 0; i < 5 && text is null; i++)
            {
                try { text = File.ReadAllText(FilePath); }
                catch (IOException) { Thread.Sleep(200); }   // locked (antivirus, backup): retry
            }
            // Still unreadable: run on the imported/default rules for now, but never overwrite the user's file.
            if (text is null) return Fallback();

            try { return FromJson(text); }
            catch (JsonException) when (NewerSchema(text)) { return Fallback(); }   // written by a newer Prumo: leave it alone
            catch (JsonException) { File.Move(FilePath, FilePath + ".bad", true); }   // keep the broken file for inspection
        }

        var cfg = ProcessLassoImport.TryImport() ?? new AppConfig();
        cfg.Save();
        return cfg;
    }

    /// <summary>True when this run must not save (the real file could not be read): edits stay in memory.</summary>
    [JsonIgnore] public bool ReadOnly { get; private set; }

    static AppConfig Fallback()
    {
        var cfg = ProcessLassoImport.TryImport() ?? new AppConfig();
        cfg.ReadOnly = true;
        return cfg;
    }

    static bool NewerSchema(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty(nameof(SchemaVersion), out var v) && v.TryGetInt32(out var n) && n > CurrentSchema;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Parses and validates a config file (also used for "Importar configuração"). Throws JsonException.</summary>
    public static AppConfig FromJson(string text)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(text, Json) ?? throw new JsonException("arquivo vazio");
        if (cfg.SchemaVersion > CurrentSchema) throw new JsonException($"feito por uma versão mais nova do Prumo (formato {cfg.SchemaVersion})");
        cfg.Validate();
        return cfg;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Clamps every number to a sane range and drops broken entries, so a hand-edited file can't hurt the engine.</summary>
    public void Validate()
    {
        SchemaVersion = CurrentSchema;
        Rules ??= new(); Disallowed ??= new(); KeepRunning ??= new(); Watchdog ??= new();
        ProBalance ??= new(); ForegroundBoost ??= new(); SmartTrim ??= new(); IdleSaver ??= new();
        Rules.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.Exe));
        foreach (var r in Rules)
            if (r.CpuLimit is { } l)
            {
                l.ThresholdPercent = Math.Clamp(l.ThresholdPercent, 1, 100);
                l.Cores = Math.Clamp(l.Cores, 1, Environment.ProcessorCount);
                l.DurationMs = Math.Clamp(l.DurationMs, 1000, 3_600_000);
            }
        Disallowed = Disallowed.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        KeepRunning = KeepRunning.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        Watchdog.RemoveAll(w => w is null || string.IsNullOrWhiteSpace(w.Exe));
        foreach (var w in Watchdog)
        {
            w.Threshold = Math.Clamp(w.Threshold, 1, w.Metric == WatchMetric.Cpu ? 100 : 1 << 20);
            w.ForSeconds = Math.Clamp(w.ForSeconds, 1, 86_400);
        }

        var pb = ProBalance;
        pb.Exclusions ??= new();
        pb.TotalCpuThreshold = Math.Clamp(pb.TotalCpuThreshold, 0, 100);
        pb.ProcessCpuThreshold = Math.Clamp(pb.ProcessCpuThreshold, 0.1, 100);
        pb.ProcessCpuRestore = Math.Clamp(pb.ProcessCpuRestore, 0, 100);
        pb.TimeOverQuotaMs = Math.Clamp(pb.TimeOverQuotaMs, 0, 600_000);
        pb.MinRestraintMs = Math.Clamp(pb.MinRestraintMs, 0, 3_600_000);
        pb.MaxRestraintMs = Math.Clamp(pb.MaxRestraintMs, 0, 86_400_000);

        var st = SmartTrim;
        st.Exclusions ??= new();
        st.MinRamLoadPercent = Math.Clamp(st.MinRamLoadPercent, 1, 100);
        st.MinProcessMb = Math.Clamp(st.MinProcessMb, 1, 65_536);
        st.IntervalMinutes = Math.Clamp(st.IntervalMinutes, 1, 1440);
        st.PurgeBelowFreeMb = Math.Clamp(st.PurgeBelowFreeMb, 64, 65_536);

        IdleSaver.IdleMinutes = Math.Clamp(IdleSaver.IdleMinutes, 1, 240);
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 250, 10_000);
        if (ForegroundBoost.Priority is not (CpuPriority.AboveNormal or CpuPriority.High)) ForegroundBoost.Priority = CpuPriority.AboveNormal;
        TimerResolution100ns = TimerResolution100ns is 0 or 5_000 or 10_000 ? TimerResolution100ns : 0;
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json), Json)!;

    public void Save()
    {
        if (ReadOnly) return;
        SecureStore.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public ProcessRule? RuleFor(string exe) =>
        Rules.FirstOrDefault(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));

    public ProcessRule GetOrAddRule(string exe)
    {
        var r = RuleFor(exe);
        if (r is null) { r = new ProcessRule { Exe = exe.ToLowerInvariant() }; Rules.Add(r); }
        return r;
    }
}

/// <summary>Finds the rule for a process: an exact executable-name rule wins, then the first matching pattern.</summary>
public sealed class RuleMatcher
{
    readonly Dictionary<string, ProcessRule> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(System.Text.RegularExpressions.Regex Re, System.Text.RegularExpressions.Regex? Except, ProcessRule Rule)> _patterns = new();

    public bool NeedsPaths { get; }

    public RuleMatcher(IEnumerable<ProcessRule> rules)
    {
        // Overlapping patterns: the most specific one (most literal characters) wins, whatever the list order.
        foreach (var r in rules.OrderByDescending(r => r.IsPattern ? r.Exe.Count(c => c is not ('*' or '?')) : int.MaxValue))
        {
            if (!r.IsPattern) { _byName.TryAdd(r.Exe, r); continue; }
            var except = r.Except is { Count: > 0 } ex ? Wildcard("(" + string.Join(")|(", ex.Select(ToRegex)) + ")") : null;
            _patterns.Add((Wildcard(ToRegex(r.Exe)), except, r));
            if (r.MatchesPath) NeedsPaths = true;
        }
    }

    static string ToRegex(string wildcard) =>
        System.Text.RegularExpressions.Regex.Escape(wildcard.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".");

    static System.Text.RegularExpressions.Regex Wildcard(string body) => new("^(" + body + ")$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public ProcessRule? Match(string name, string? path)
    {
        if (_byName.TryGetValue(name, out var r)) return r;
        foreach (var (re, except, rule) in _patterns)
        {
            var subject = rule.MatchesPath ? path : name;
            if (subject is not null && re.IsMatch(subject) && except?.IsMatch(name) != true) return rule;
        }
        return null;
    }
}

/// <summary>Reads Process Lasso's prolasso.ini so the first start keeps the user's rules.</summary>
public static class ProcessLassoImport
{
    public static string IniPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessLasso", "config", "prolasso.ini");

    public static AppConfig? TryImport()
    {
        try { return File.Exists(IniPath) ? Import(File.ReadAllLines(IniPath)) : null; }
        catch { return null; }
    }

    public static AppConfig Import(string[] lines)
    {
        // Sections repeat in prolasso.ini, so keys are read flat (they are unique).
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && !line.StartsWith('[')) kv[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        string Get(string k) => kv.TryGetValue(k, out var v) ? v : "";
        bool Bool(string k, bool def) => kv.TryGetValue(k, out var v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
        double Num(string k, double def) => double.TryParse(Get(k), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;
        static string[] List(string v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var cfg = new AppConfig { ImportedFromProcessLasso = true };
        var pb = cfg.ProBalance;
        pb.Enabled = Bool("OocOn", true);
        pb.TotalCpuThreshold = Num("TotalProcessorUsageBeforeRestraint", pb.TotalCpuThreshold);
        pb.ProcessCpuThreshold = Num("PerProcessUsageBeforeRestraint", pb.ProcessCpuThreshold);
        pb.TimeOverQuotaMs = (int)Num("TimeOverQuotaBeforeRestraint", pb.TimeOverQuotaMs);
        pb.ProcessCpuRestore = Num("PerProcessUsageForRestore", pb.ProcessCpuRestore);
        pb.MinRestraintMs = (int)Num("MinimumTimeOfRestraint", pb.MinRestraintMs);
        pb.MaxRestraintMs = (int)Num("MaximumTimeOfRestraint", pb.MaxRestraintMs);
        pb.ExcludeForeground = Bool("ExcludeForegroundProcesses2", true);
        pb.ExcludeChildrenOfForeground = Bool("ExcludeChildrenOfForeground", true);
        pb.ExcludeServices = Bool("ExcludeServices", true);
        pb.OnlyNormalPriority = Bool("TameOnlyNormal", true);
        pb.LowerToIdle = Bool("LowerToIdleInsteadOfBelowNormal", false);
        pb.LowerIoPriority = Bool("LowerIOPriorityDuringRestraint", false);
        pb.Exclusions = List(Get("OocExclusions")).Select(s => s.ToLowerInvariant()).ToList();

        var pr = List(Get("DefaultPriorities"));
        for (int i = 0; i + 1 < pr.Length; i += 2)
            if (ParseCpu(pr[i + 1]) is { } p) cfg.GetOrAddRule(pr[i]).CpuPriority = p;

        var io = List(Get("DefaultIOPriorities"));
        for (int i = 0; i + 1 < io.Length; i += 2)
            if (int.TryParse(io[i + 1], out var v) && v is >= 0 and <= 3) cfg.GetOrAddRule(io[i]).IoPriority = (IoPriority)v;

        var gpu = List(Get("DefaultGPUPriorities"));
        for (int i = 0; i + 1 < gpu.Length; i += 2)
            if (int.TryParse(gpu[i + 1], out var v) && v is >= 0 and <= 5) cfg.GetOrAddRule(gpu[i]).GpuPriority = (GpuPriority)v;

        // name,group,cpus triples
        var aff = List(Get("DefaultAffinitiesEx"));
        for (int i = 0; i + 2 < aff.Length; i += 3)
        {
            // An affinity of every CPU is the same as no affinity rule.
            var mask = Affinity.Parse(aff[i + 2]);
            if (mask != 0 && mask != Affinity.AllCpus) cfg.GetOrAddRule(aff[i]).Affinity = aff[i + 2];
        }

        var eff = List(Get("EfficiencyMode"));
        for (int i = 0; i + 1 < eff.Length; i += 2)
            cfg.GetOrAddRule(eff[i]).Efficiency = eff[i + 1] == "1" ? EfficiencyMode.On : EfficiencyMode.Off;

        var plan = Get("TargetPowerPlan");
        if (plan.Length > 0)
            foreach (var exe in List(Get("AutomaticGamingModeProcessPaths")))
                cfg.GetOrAddRule(Path.GetFileName(exe)).PowerPlan = plan;

        foreach (var ex in pb.Exclusions)
            cfg.GetOrAddRule(ex).ProBalanceExcluded = true;
        pb.Exclusions.Clear();

        cfg.Rules.RemoveAll(r => r.IsEmpty);
        return cfg;
    }

    static CpuPriority? ParseCpu(string s) => s.ToLowerInvariant() switch
    {
        "idle" => CpuPriority.Idle,
        "belownormal" => CpuPriority.BelowNormal,
        "normal" => CpuPriority.Normal,
        "abovenormal" => CpuPriority.AboveNormal,
        "high" => CpuPriority.High,
        "realtime" => CpuPriority.Realtime,
        _ => null,
    };
}

public static class Affinity
{
    public static ulong AllCpus => Environment.ProcessorCount >= 64 ? ulong.MaxValue : (1UL << Environment.ProcessorCount) - 1;

    /// <summary>"1-7" / "0,2,4-6" -> bit mask; 0 when invalid.</summary>
    public static ulong Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        ulong mask = 0;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(ab[0], out var a)) return 0;
            int b = a;
            if (ab.Length == 2 && !int.TryParse(ab[1], out b)) return 0;
            if (a < 0 || b < a || b >= 64) return 0;
            for (int i = a; i <= b; i++) mask |= 1UL << i;
        }
        return mask & AllCpus;
    }

    public static string Format(ulong mask)
    {
        var parts = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) == 0) continue;
            int j = i;
            while (j + 1 < 64 && (mask & (1UL << (j + 1))) != 0) j++;
            parts.Add(i == j ? $"{i}" : $"{i}-{j}");
            i = j;
        }
        return string.Join(",", parts);
    }
}
