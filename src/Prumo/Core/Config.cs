using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prumo.Core;

public enum CpuPriority { Idle, BelowNormal, Normal, AboveNormal, High, Realtime }
public enum IoPriority { VeryLow = 0, Low = 1, Normal = 2, High = 3 }
public enum GpuPriority { Idle = 0, BelowNormal = 1, Normal = 2, AboveNormal = 3, High = 4, Realtime = 5 }
public enum EfficiencyMode { Off, On }

/// <summary>What to apply to every process whose executable name matches <see cref="Exe"/>.</summary>
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

    [JsonIgnore] public bool IsEmpty =>
        CpuPriority is null && IoPriority is null && GpuPriority is null && Affinity is null &&
        Efficiency is null && PowerPlan is null && !ProBalanceExcluded;
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
    public List<ProcessRule> Rules { get; set; } = new();
    public ProBalanceSettings ProBalance { get; set; } = new();
    /// <summary>Plan to go back to when no "modo jogo" process runs. Null = the plan active before.</summary>
    public string? DefaultPowerPlan { get; set; }
    public int UpdateIntervalMs { get; set; } = 1000;
    public bool CloseToTray { get; set; } = true;
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
            if (text is null) return ProcessLassoImport.TryImport() ?? new AppConfig();

            try { return JsonSerializer.Deserialize<AppConfig>(text, Json) ?? new(); }
            catch (JsonException) { File.Move(FilePath, FilePath + ".bad", true); }   // keep the broken file for inspection
        }

        var cfg = ProcessLassoImport.TryImport() ?? new AppConfig();
        cfg.Save();
        return cfg;
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json), Json)!;

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
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
