using System.Diagnostics;

namespace Prumo.Core;

public sealed record ProcessInfo(
    int Pid, int ParentPid, string Exe, uint SessionId, double Cpu, ulong WorkingSet,
    int BasePriority, bool Restrained, bool HasRule, bool Foreground);

public sealed record EngineSnapshot(
    DateTime Time, IReadOnlyList<ProcessInfo> Processes, double CpuTotal, SystemSample? System,
    int RestrainedCount, int RestraintsStarted, string? PowerPlan);

/// <summary>
/// Once a second: reads every process, applies the per-exe rules to new processes, runs
/// ProBalance and switches the power plan while a "modo jogo" process runs.
/// </summary>
public sealed unsafe class Engine : IDisposable
{
    const uint BELOW_NORMAL = 0x4000, IDLE = 0x40, NORMAL = 0x20;

    static readonly HashSet<string> Untouchable = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "dwm.exe", "audiodg.exe", "fontdrvhost.exe", "memory compression", "secure system",
        "prumo.exe", "processlasso.exe", "processgovernor.exe",
    };

    sealed class Tracked
    {
        public long CreateTime;
        public string Exe = "";
        public long LastCpuTime = -1;
        public double Cpu;
        public long OverSince;          // Stopwatch ms; 0 = not over the threshold
        public long RestrainedAt;       // 0 = not restrained
        public uint OriginalClass, RestrainedClass;
        public int OriginalIo = -1;     // I/O priority before we lowered it; -1 = untouched
        public int RestoreAttempts;
        public int RuleVersion = -1, RuleAttempts;
    }

    readonly ActivityLog _log;
    readonly ISystemSampler? _sampler;
    readonly Dictionary<int, Tracked> _tracked = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly int _selfPid = Environment.ProcessId;
    readonly int _cpuCount = Environment.ProcessorCount;
    byte[] _buf = new byte[1 << 20];
    Thread? _thread;
    volatile bool _stop;
    long _lastTick;
    int _ruleVersion;
    Guid? _planBefore;           // plan to restore once no game runs
    Guid? _planSet;              // plan we switched to
    // The UI edits Config on its own thread; the engine only reads this immutable copy, swapped on ConfigChanged.
    sealed record View(AppConfig Cfg, Dictionary<string, Guid?> PlanIds);
    volatile View _view;
    AppConfig Live => _view.Cfg;
    string? _planName; long _planNameAt = -10_000;

    public AppConfig Config { get; private set; }
    public EngineSnapshot? Last { get; private set; }
    public event Action<EngineSnapshot>? Updated;

    public Engine(AppConfig config, ActivityLog log, ISystemSampler? sampler)
    {
        Config = config; _log = log; _sampler = sampler;
        _view = new View(config.Clone(), new(StringComparer.OrdinalIgnoreCase));
        Native.EnablePrivilege("SeIncreaseBasePriorityPrivilege");
        Native.EnablePrivilege("SeDebugPrivilege");
    }

    public void Start()
    {
        _log.Add(EventKind.Info, 0, "prumo.exe", $"Gerenciamento iniciado ({Config.Rules.Count} regras, ProBalance {(Config.ProBalance.Enabled ? "ligado" : "desligado")})");
        _thread = new Thread(Loop) { IsBackground = true, Name = "Prumo engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Call after editing <see cref="Config"/>: rules are re-applied to running processes.</summary>
    public void ConfigChanged()
    {
        lock (_tracked) { _ruleVersion++; foreach (var t in _tracked.Values) t.RuleAttempts = 0; }
        _view = new View(Config.Clone(), new(StringComparer.OrdinalIgnoreCase));
        Config.Save();
    }

    void Loop()
    {
        while (!_stop)
        {
            var started = _clock.ElapsedMilliseconds;
            try { Tick(); }
            catch (Exception ex) { _log.Add(EventKind.Info, 0, "prumo.exe", "Erro no ciclo: " + ex.Message); }
            var wait = Live.UpdateIntervalMs - (int)(_clock.ElapsedMilliseconds - started);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    // ------------------------------------------------------------------ tick

    void Tick()
    {
        long now = _clock.ElapsedMilliseconds;
        double wallMs = _lastTick == 0 ? 0 : now - _lastTick;
        _lastTick = now;

        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var fgPid);
        var procs = ReadProcesses();
        var parents = procs.ToDictionary(p => p.Pid, p => (p.ParentPid, p.CreateTime));

        double totalCpu = 0;
        var seen = new HashSet<int>();
        var list = new List<ProcessInfo>(procs.Count);
        int started = 0, restrained = 0;
        var cfg = Live;
        var pb = cfg.ProBalance;

        lock (_tracked)
        {
            foreach (var p in procs)
            {
                seen.Add(p.Pid);
                if (!_tracked.TryGetValue(p.Pid, out var t) || t.CreateTime != p.CreateTime)
                    _tracked[p.Pid] = t = new Tracked { CreateTime = p.CreateTime, Exe = p.Name };

                if (t.LastCpuTime >= 0 && wallMs > 0)
                    t.Cpu = Math.Clamp((p.CpuTime - t.LastCpuTime) / 10_000.0 / (wallMs * _cpuCount) * 100, 0, 100);
                t.LastCpuTime = p.CpuTime;
                if (p.Pid != 0) totalCpu += t.Cpu;

                var rule = cfg.RuleFor(p.Name);
                if (rule is not null && t.RuleVersion != _ruleVersion && t.RuleAttempts < 5 && p.Pid > 4)
                    ApplyRule(p.Pid, t, rule);

                bool fg = p.Pid == fgPid;
                if (!pb.Enabled && t.RestrainedAt != 0) Restore(p.Pid, t, "o ProBalance foi desligado");
                else if (pb.Enabled && p.Pid > 4 && p.Pid != _selfPid)
                    started += ProBalance(p, t, rule, now, fg || (pb.ExcludeChildrenOfForeground && IsDescendant(p.Pid, fgPid, parents)));
                if (t.RestrainedAt != 0) restrained++;

                list.Add(new ProcessInfo(p.Pid, p.ParentPid, p.Name, p.SessionId, t.Cpu, p.WorkingSet,
                    p.BasePriority, t.RestrainedAt != 0, rule is not null, fg));
            }

            foreach (var pid in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var t = _tracked[pid];
                if (t.RestrainedAt != 0) _log.Add(EventKind.Restored, pid, t.Exe, "ProBalance terminou: o processo fechou");
                _tracked.Remove(pid);
            }
        }

        // Second pass of ProBalance needs the total, which is only known now; restraint uses last tick's total.
        _lastTotal = Math.Min(100, totalCpu);

        UpdatePowerPlan(procs);

        SystemSample? sys = null;
        try { sys = _sampler?.Sample(); } catch { }

        if (now - _planNameAt > 5000) { _planName = PowerPlans.ActiveName(); _planNameAt = now; }
        var snap = new EngineSnapshot(DateTime.Now, list, sys?.CpuTotal ?? _lastTotal, sys, restrained, started, _planName);
        Last = snap;
        Updated?.Invoke(snap);
    }

    double _lastTotal;

    static bool IsDescendant(int pid, int ancestor, Dictionary<int, (int Parent, long Create)> map)
    {
        if (ancestor <= 4) return false;
        for (int depth = 0; depth < 16 && map.TryGetValue(pid, out var me); depth++)
        {
            if (me.Parent == ancestor)
                return map.TryGetValue(ancestor, out var a) && a.Create <= me.Create;
            if (!map.TryGetValue(me.Parent, out var parent) || parent.Create > me.Create) return false; // PID reused
            pid = me.Parent;
        }
        return false;
    }

    // ------------------------------------------------------------- ProBalance

    /// <returns>1 when a restraint started.</returns>
    int ProBalance(RawProcess p, Tracked t, ProcessRule? rule, long now, bool foregroundish)
    {
        var pb = Live.ProBalance;

        if (t.RestrainedAt != 0)
        {
            string? why = null;
            if (pb.ExcludeForeground && foregroundish) why = "o processo foi para o primeiro plano";
            else if (t.Cpu < pb.ProcessCpuRestore && now - t.RestrainedAt >= pb.MinRestraintMs) why = "o uso de CPU voltou ao normal";
            else if (pb.MaxRestraintMs > 0 && now - t.RestrainedAt >= pb.MaxRestraintMs) why = "tempo máximo de restrição";
            if (why is not null) Restore(p.Pid, t, why);
            return 0;
        }

        bool eligible =
            !(pb.ExcludeForeground && foregroundish) &&
            !(pb.ExcludeServices && p.SessionId == 0) &&
            !(pb.OnlyNormalPriority && p.BasePriority != 8) &&
            !Untouchable.Contains(p.Name) &&
            rule is not { ProBalanceExcluded: true } &&
            rule?.CpuPriority is null &&
            !pb.Exclusions.Contains(p.Name, StringComparer.OrdinalIgnoreCase);

        if (!eligible || t.Cpu < pb.ProcessCpuThreshold || _lastTotal < pb.TotalCpuThreshold) { t.OverSince = 0; return 0; }
        if (t.OverSince == 0) { t.OverSince = now; return 0; }
        if (now - t.OverSince < pb.TimeOverQuotaMs) return 0;

        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h == 0) { t.OverSince = 0; return 0; }
        try
        {
            var cls = Native.GetPriorityClass(h);
            if (cls == 0 || (pb.OnlyNormalPriority && cls != NORMAL)) return 0;
            var target = pb.LowerToIdle ? IDLE : BELOW_NORMAL;
            if (!Native.SetPriorityClass(h, target)) return 0;
            t.OriginalIo = -1;
            // A rule that sets the I/O priority wins over ProBalance.
            if (pb.LowerIoPriority && rule?.IoPriority is null &&
                Native.NtQueryInformationProcess(h, Native.ProcessIoPriority, out int io, 4, out _) >= 0 && io > (int)IoPriority.Low)
            {
                int low = (int)IoPriority.Low;
                if (Native.NtSetInformationProcess(h, Native.ProcessIoPriority, ref low, 4) >= 0) t.OriginalIo = io;
            }
            t.OriginalClass = cls; t.RestoreAttempts = 0; t.RestrainedClass = target; t.RestrainedAt = now; t.OverSince = 0;
            _log.Add(EventKind.Restrained, p.Pid, p.Name, $"Prioridade reduzida pelo ProBalance (CPU {t.Cpu:0.0}%)");
            return 1;
        }
        finally { Native.CloseHandle(h); }
    }

    void Restore(int pid, Tracked t, string why)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0)
        {
            // Transient failure: keep it restrained and try again next tick, but do not retry forever.
            if (++t.RestoreAttempts < 10) return;
            t.RestrainedAt = 0;
            _log.Add(EventKind.RuleFailed, pid, t.Exe, "Não consegui restaurar a prioridade (sem acesso ao processo)");
            return;
        }
        // Only undo our own change: if something else changed the priority since, leave it.
        if (Native.GetPriorityClass(h) == t.RestrainedClass) Native.SetPriorityClass(h, t.OriginalClass);
        if (t.OriginalIo >= 0) { int io = t.OriginalIo; Native.NtSetInformationProcess(h, Native.ProcessIoPriority, ref io, 4); t.OriginalIo = -1; }
        Native.CloseHandle(h);
        t.RestrainedAt = 0;
        _log.Add(EventKind.Restored, pid, t.Exe, "Prioridade original restaurada: " + why);
    }

    // ------------------------------------------------------------------ rules

    void ApplyRule(int pid, Tracked t, ProcessRule rule)
    {
        t.RuleAttempts++;
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0)
        {
            if (t.RuleAttempts == 5) _log.Add(EventKind.RuleFailed, pid, t.Exe, "Sem acesso ao processo para aplicar a regra");
            return;
        }
        var done = new List<string>();
        var failed = new List<string>();
        try
        {
            if (rule.CpuPriority is { } cpu)
                (Native.SetPriorityClass(h, ToClass(cpu)) ? done : failed).Add("CPU " + Names.Of(cpu));
            if (rule.IoPriority is { } io)
            {
                int v = (int)io;
                (Native.NtSetInformationProcess(h, Native.ProcessIoPriority, ref v, 4) >= 0 ? done : failed).Add("I/O " + Names.Of(io));
            }
            if (rule.GpuPriority is { } gpu)
                (Native.D3DKMTSetProcessSchedulingPriorityClass(h, (int)gpu) == 0 ? done : failed).Add("GPU " + Names.Of(gpu));
            if (rule.Affinity is { } affText && Affinity.Parse(affText) is var mask and not 0)
                (Native.SetProcessAffinityMask(h, (nuint)mask) ? done : failed).Add("afinidade " + affText);
            if (rule.Efficiency is { } eff)
            {
                var st = new Native.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = eff == EfficiencyMode.On ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
                };
                (Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref st, sizeof(Native.PROCESS_POWER_THROTTLING_STATE)) ? done : failed)
                    .Add("modo de eficiência " + (eff == EfficiencyMode.On ? "ligado" : "desligado"));
            }
        }
        finally { Native.CloseHandle(h); }

        if (failed.Count == 0 || t.RuleAttempts == 5) t.RuleVersion = _ruleVersion;
        if (done.Count > 0 && (failed.Count == 0 || t.RuleAttempts == 5))
            _log.Add(EventKind.RuleApplied, pid, t.Exe, "Regra aplicada: " + string.Join(", ", done));
        if (failed.Count > 0 && t.RuleAttempts == 5)
            _log.Add(EventKind.RuleFailed, pid, t.Exe, "Falhou: " + string.Join(", ", failed));
    }

    static uint ToClass(CpuPriority p) => p switch
    {
        CpuPriority.Idle => IDLE,
        CpuPriority.BelowNormal => BELOW_NORMAL,
        CpuPriority.Normal => NORMAL,
        CpuPriority.AboveNormal => 0x8000,
        CpuPriority.High => 0x80,
        CpuPriority.Realtime => 0x100,
        _ => NORMAL,
    };

    // ------------------------------------------------------------ power plan

    void UpdatePowerPlan(List<RawProcess> procs)
    {
        Guid? want = null;
        string? who = null;
        var view = _view;
        foreach (var r in view.Cfg.Rules)
        {
            if (r.PowerPlan is null || !procs.Any(p => string.Equals(p.Name, r.Exe, StringComparison.OrdinalIgnoreCase))) continue;
            if (!view.PlanIds.TryGetValue(r.PowerPlan, out var id)) view.PlanIds[r.PowerPlan] = id = PowerPlans.Find(r.PowerPlan);
            if (id is not null) { want = id; who = r.Exe; break; }
        }

        if (want is not null && _planSet != want)
        {
            var active = PowerPlans.Active();
            _planBefore ??= active;
            if (active != want && PowerPlans.SetActive(want.Value))
                _log.Add(EventKind.PowerPlan, 0, who!, "Modo jogo: plano de energia " + PlanName(want.Value));
            _planSet = want;
        }
        else if (want is null && _planSet is not null)
        {
            var back = (Live.DefaultPowerPlan is { } n ? PowerPlans.Find(n) : null) ?? _planBefore;
            if (back is not null && PowerPlans.Active() != back && PowerPlans.SetActive(back.Value))
                _log.Add(EventKind.PowerPlan, 0, "prumo.exe", "Modo jogo terminou: plano de energia " + PlanName(back.Value));
            _planSet = null; _planBefore = null;
        }
    }

    static string PlanName(Guid id) => PowerPlans.List().FirstOrDefault(p => p.Id == id)?.Name ?? id.ToString();

    // ------------------------------------------------------- process snapshot

    readonly record struct RawProcess(int Pid, int ParentPid, string Name, uint SessionId, long CreateTime,
        long CpuTime, ulong WorkingSet, int BasePriority);

    readonly Dictionary<string, string> _names = new();

    List<RawProcess> ReadProcesses()
    {
        int ret;
        while (true)
        {
            fixed (byte* b = _buf)
            {
                int st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, b, _buf.Length, out ret);
                if (st == 0) break;
                if ((uint)st != 0xC0000004) throw new InvalidOperationException($"NtQuerySystemInformation 0x{st:X8}");
            }
            _buf = new byte[Math.Max(ret + (1 << 16), _buf.Length * 2)];
        }

        var list = new List<RawProcess>(512);
        fixed (byte* b = _buf)
        {
            byte* cur = b;
            while (true)
            {
                var spi = (Native.SYSTEM_PROCESS_INFORMATION*)cur;
                int pid = (int)spi->UniqueProcessId;
                string name = pid == 0 ? "Idle" : spi->NameBuffer == null ? "System" : Intern(new string(spi->NameBuffer, 0, spi->NameLength / 2));
                list.Add(new RawProcess(pid, (int)spi->InheritedFromUniqueProcessId, name, spi->SessionId,
                    spi->CreateTime, spi->UserTime + spi->KernelTime, spi->WorkingSetSize, spi->BasePriority));
                if (spi->NextEntryOffset == 0) break;
                cur += spi->NextEntryOffset;
            }
        }
        return list;
    }

    string Intern(string s) => _names.TryGetValue(s, out var v) ? v : _names[s] = s;

    // ---------------------------------------------------------- manual actions

    /// <summary>One-off change from the process list (not saved as a rule).</summary>
    public bool SetPriorityNow(int pid, CpuPriority p)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION, false, pid);
        if (h == 0) return false;
        try { return Native.SetPriorityClass(h, ToClass(p)); } finally { Native.CloseHandle(h); }
    }

    public bool SetAffinityNow(int pid, ulong mask)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try { return Native.SetProcessAffinityMask(h, (nuint)mask); } finally { Native.CloseHandle(h); }
    }

    public static ulong? GetAffinity(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try { return Native.GetProcessAffinityMask(h, out var pm, out _) ? pm : null; } finally { Native.CloseHandle(h); }
    }

    public static bool Kill(int pid)
    {
        try { Process.GetProcessById(pid).Kill(); return true; } catch { return false; }
    }

    public static string? PathOf(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try { return Native.ImagePath(h); } finally { Native.CloseHandle(h); }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(3000);
        lock (_tracked)
            foreach (var (pid, t) in _tracked)
                if (t.RestrainedAt != 0) Restore(pid, t, "Prumo foi fechado");
        if (_planSet is not null)
        {
            var back = (Live.DefaultPowerPlan is { } n ? PowerPlans.Find(n) : null) ?? _planBefore;
            if (back is not null) PowerPlans.SetActive(back.Value);
        }
        _sampler?.Dispose();
    }
}

public static class Names
{
    public static string Of(CpuPriority p) => p switch
    {
        CpuPriority.Idle => "Ociosa", CpuPriority.BelowNormal => "Abaixo do normal", CpuPriority.Normal => "Normal",
        CpuPriority.AboveNormal => "Acima do normal", CpuPriority.High => "Alta", CpuPriority.Realtime => "Tempo real", _ => p.ToString(),
    };
    public static string Of(IoPriority p) => p switch
    {
        IoPriority.VeryLow => "Muito baixa", IoPriority.Low => "Baixa", IoPriority.Normal => "Normal", IoPriority.High => "Alta", _ => p.ToString(),
    };
    public static string Of(GpuPriority p) => p switch
    {
        GpuPriority.Idle => "Ociosa", GpuPriority.BelowNormal => "Abaixo do normal", GpuPriority.Normal => "Normal",
        GpuPriority.AboveNormal => "Acima do normal", GpuPriority.High => "Alta", GpuPriority.Realtime => "Tempo real", _ => p.ToString(),
    };
    public static string OfBase(int basePriority) => basePriority switch
    {
        4 => "Ociosa", 6 => "Abaixo do normal", 8 => "Normal", 10 => "Acima do normal", 13 => "Alta", 24 => "Tempo real", _ => basePriority.ToString(),
    };
}
