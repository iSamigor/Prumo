using System.Diagnostics;

namespace Prumo.Core;

public sealed record ProcessInfo(
    int Pid, int ParentPid, string Exe, uint SessionId, double Cpu, ulong WorkingSet,
    int BasePriority, bool Restrained, bool HasRule, bool Foreground, string? Path);

public sealed record EngineSnapshot(
    DateTime Time, IReadOnlyList<ProcessInfo> Processes, double CpuTotal, SystemSample? System,
    int RestrainedCount, int RestraintsStarted, string? PowerPlan);

/// <summary>
/// Once a second: reads every process, applies the rules to new processes, runs ProBalance, the
/// foreground boost, the CPU limiter and SmartTrim, and switches the power plan for "modo jogo" /
/// "modo desempenho".
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
        public string? Path; public bool PathResolved;
        public bool Boosted; public uint BoostOriginal, BoostClass;   // foreground boost
        public long LimitedAt; public ulong LimitOriginal, LimitMask;  // CPU limiter
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
    uint[]? _cpuSetIds;
    uint _timerSet;
    long _lastTrim = -1, _lastPurge = -10 * 60_000;

    // The UI edits Config on its own thread; the engine only reads this immutable copy, swapped on ConfigChanged.
    sealed record View(AppConfig Cfg, Dictionary<string, Guid?> PlanIds, RuleMatcher Matcher)
    {
        public static View Of(AppConfig cfg)
        {
            var c = cfg.Clone();
            return new(c, new(StringComparer.OrdinalIgnoreCase), new RuleMatcher(c.Rules));
        }
    }
    volatile View _view;
    AppConfig Live => _view.Cfg;
    string? _planName; long _planNameAt = -10_000;

    public AppConfig Config { get; private set; }
    public EngineSnapshot? Last { get; private set; }
    public event Action<EngineSnapshot>? Updated;

    public Engine(AppConfig config, ActivityLog log, ISystemSampler? sampler)
    {
        Config = config; _log = log; _sampler = sampler;
        _view = View.Of(config);
        Native.EnablePrivilege("SeIncreaseBasePriorityPrivilege");
        Native.EnablePrivilege("SeDebugPrivilege");
    }

    public void Start()
    {
        _log.Add(EventKind.Info, 0, "prumo.exe", $"Gerenciamento iniciado ({Config.Rules.Count} regras, ProBalance {(Config.ProBalance.Enabled ? "ligado" : "desligado")})");
        ApplyTimerResolution();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Prumo engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Call after editing <see cref="Config"/>: rules are re-applied to running processes.</summary>
    public void ConfigChanged()
    {
        lock (_tracked) { _ruleVersion++; foreach (var t in _tracked.Values) t.RuleAttempts = 0; }
        _view = View.Of(Config);
        ApplyTimerResolution();
        Config.Save();
    }

    void ApplyTimerResolution()
    {
        uint want = (uint)Math.Max(0, Live.TimerResolution100ns);
        if (want == _timerSet) return;
        if (_timerSet != 0) Native.NtSetTimerResolution(_timerSet, false, out _);
        if (want != 0 && Native.NtSetTimerResolution(want, true, out var cur) == 0)
            _log.Add(EventKind.Info, 0, "prumo.exe", $"Resolução do timer pedida: {want / 10_000.0:0.###} ms (atual {cur / 10_000.0:0.###} ms)");
        _timerSet = want;
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
        var view = _view;
        var cfg = view.Cfg;
        var pb = cfg.ProBalance;
        var planRules = new List<ProcessRule>();

        SystemSample? sys = null;
        try { sys = _sampler?.Sample(); } catch { }

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

                // Paths cost an OpenProcess, so they are only read (once per process) when a rule matches on paths.
                if (view.Matcher.NeedsPaths && !t.PathResolved && p.Pid > 4)
                {
                    t.Path = PathOf(p.Pid);
                    t.PathResolved = true;
                }
                var rule = view.Matcher.Match(p.Name, t.Path);
                if (rule is not null && t.RuleVersion != _ruleVersion && t.RuleAttempts < 5 && p.Pid > 4)
                    ApplyRule(p.Pid, t, rule);
                if (rule?.PowerPlan is not null) planRules.Add(rule);

                bool fg = p.Pid == fgPid;
                if (p.Pid > 4 && p.Pid != _selfPid)
                {
                    ForegroundBoost(p, t, rule, fg, cfg.ForegroundBoost);
                    CpuLimiter(p, t, rule, now);
                }
                if (!pb.Enabled && t.RestrainedAt != 0) Restore(p.Pid, t, "o ProBalance foi desligado");
                else if (pb.Enabled && p.Pid > 4 && p.Pid != _selfPid)
                    started += ProBalance(p, t, rule, now, fg || (pb.ExcludeChildrenOfForeground && IsDescendant(p.Pid, fgPid, parents)));
                if (t.RestrainedAt != 0) restrained++;

                list.Add(new ProcessInfo(p.Pid, p.ParentPid, p.Name, p.SessionId, t.Cpu, p.WorkingSet,
                    p.BasePriority, t.RestrainedAt != 0, rule is not null, fg, t.Path));
            }

            foreach (var pid in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var t = _tracked[pid];
                if (t.RestrainedAt != 0) _log.Add(EventKind.Restored, pid, t.Exe, "ProBalance terminou: o processo fechou");
                _tracked.Remove(pid);
            }
        }

        // Restraint decisions use the previous tick's total; the new one is only known now.
        _lastTotal = Math.Min(100, totalCpu);

        SmartTrim(procs, fgPid, parents, sys, now, view);
        UpdatePowerPlan(planRules);

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
            if (rule.MemoryPriority is { } mem)
            {
                uint v = (uint)mem;
                (Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref v, 4) ? done : failed).Add("memória " + Names.Of(mem));
            }
            if (rule.CpuSets is { } setsText && Affinity.Parse(setsText) is var setMask and not 0)
            {
                _cpuSetIds ??= Native.CpuSetIds();
                var ids = Enumerable.Range(0, Math.Min(64, _cpuSetIds.Length))
                    .Where(i => (setMask & (1UL << i)) != 0).Select(i => _cpuSetIds[i]).ToArray();
                (ids.Length > 0 && Native.SetProcessDefaultCpuSets(h, ids, (uint)ids.Length) ? done : failed).Add("CPU Sets " + setsText);
            }
            if (rule.DisablePriorityBoost is { } noBoost)
                (Native.SetProcessPriorityBoost(h, noBoost) ? done : failed).Add(noBoost ? "boost de prioridade desligado" : "boost de prioridade ligado");
            if (rule.GpuPreference is { } pref)
            {
                var path = t.Path ?? Native.ImagePath(h);
                (path is not null && SetGpuPreference(path, pref) ? done : failed)
                    .Add("GPU preferida " + (pref == GpuPreference.HighPerformance ? "alto desempenho" : "economia de energia"));
            }
        }
        finally { Native.CloseHandle(h); }

        if (failed.Count == 0 || t.RuleAttempts == 5) t.RuleVersion = _ruleVersion;
        if (done.Count > 0 && (failed.Count == 0 || t.RuleAttempts == 5))
            _log.Add(EventKind.RuleApplied, pid, t.Exe, "Regra aplicada: " + string.Join(", ", done));
        if (failed.Count > 0 && t.RuleAttempts == 5)
            _log.Add(EventKind.RuleFailed, pid, t.Exe, "Falhou: " + string.Join(", ", failed));
    }

    /// <summary>Same setting as Windows' Settings &gt; Display &gt; Graphics; DirectX reads it when the program starts.</summary>
    static bool SetGpuPreference(string exePath, GpuPreference pref)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            // The value holds several "Name=Value;" pairs (Windows also keeps AppStatus there): change only ours.
            var old = key.GetValue(exePath) as string ?? "";
            var pairs = old.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).ToList();
            pairs.Add($"GpuPreference={(int)pref}");
            var value = string.Join(";", pairs) + ";";
            if (old != value) key.SetValue(exePath, value);
            return true;
        }
        catch { return false; }
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

    // -------------------------------------------------------- foreground boost

    void ForegroundBoost(RawProcess p, Tracked t, ProcessRule? rule, bool fg, ForegroundBoostSettings fb)
    {
        if (t.Boosted && (!fg || !fb.Enabled))
        {
            var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
            if (h != 0)
            {
                if (Native.GetPriorityClass(h) == t.BoostClass) Native.SetPriorityClass(h, t.BoostOriginal);
                Native.CloseHandle(h);
            }
            t.Boosted = false;
            return;
        }
        if (!fb.Enabled || !fg || t.Boosted || t.RestrainedAt != 0 || rule?.CpuPriority is not null || Untouchable.Contains(p.Name)) return;
        if (fb.OnlyNormal && p.BasePriority != 8) return;

        var hh = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (hh == 0) return;
        try
        {
            var cls = Native.GetPriorityClass(hh);
            var target = ToClass(fb.Priority);
            if (cls == 0 || cls == target || (fb.OnlyNormal && cls != NORMAL)) return;
            if (Native.SetPriorityClass(hh, target)) { t.Boosted = true; t.BoostOriginal = cls; t.BoostClass = target; }
        }
        finally { Native.CloseHandle(hh); }
    }

    // ------------------------------------------------------------ CPU limiter

    void CpuLimiter(RawProcess p, Tracked t, ProcessRule? rule, long now)
    {
        if (t.LimitedAt != 0)
        {
            if (now - t.LimitedAt < (rule?.CpuLimit?.DurationMs ?? 0)) return;
            var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
            if (h != 0)
            {
                if (Native.GetProcessAffinityMask(h, out var cur, out _) && cur == (nuint)t.LimitMask)
                    Native.SetProcessAffinityMask(h, (nuint)t.LimitOriginal);
                Native.CloseHandle(h);
            }
            t.LimitedAt = 0;
            _log.Add(EventKind.Restored, p.Pid, p.Name, "Limitador de CPU terminou: núcleos originais devolvidos");
            return;
        }
        if (rule?.CpuLimit is not { } lim || lim.Cores <= 0 || t.Cpu < lim.ThresholdPercent) return;

        var hh = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (hh == 0) return;
        try
        {
            if (!Native.GetProcessAffinityMask(hh, out var cur, out _)) return;
            ulong mask = 0; int n = 0;
            // Keep the highest-numbered CPUs it already had, leaving CPU 0 (interrupts, the OS) for last.
            for (int i = 63; i >= 0 && n < lim.Cores; i--)
                if (((ulong)cur & (1UL << i)) != 0) { mask |= 1UL << i; n++; }
            if (mask == 0 || mask == (ulong)cur || !Native.SetProcessAffinityMask(hh, (nuint)mask)) return;
            t.LimitedAt = now; t.LimitOriginal = cur; t.LimitMask = mask;
            _log.Add(EventKind.Restrained, p.Pid, p.Name,
                $"Limitador de CPU: {t.Cpu:0}% → restrito às CPUs {Affinity.Format(mask)} por {lim.DurationMs / 1000.0:0.#} s");
        }
        finally { Native.CloseHandle(hh); }
    }

    // --------------------------------------------------------------- SmartTrim

    void SmartTrim(List<RawProcess> procs, int fgPid, Dictionary<int, (int Parent, long Create)> parents,
                   SystemSample? sys, long now, View view)
    {
        var st = view.Cfg.SmartTrim;
        if (_lastTrim < 0) _lastTrim = now;   // first trim one interval after start, not at boot

        if (st.TrimWorkingSets && sys is not null && sys.MemoryLoad >= st.MinRamLoadPercent &&
            now - _lastTrim >= Math.Max(1, st.IntervalMinutes) * 60_000L)
        {
            _lastTrim = now;
            int count = 0; ulong trimmed = 0;
            ulong min = (ulong)Math.Max(1, st.MinProcessMb) << 20;
            foreach (var p in procs)
            {
                if (p.Pid <= 4 || p.Pid == _selfPid || p.SessionId == 0 || p.WorkingSet < min) continue;
                if (p.Pid == fgPid || IsDescendant(p.Pid, fgPid, parents) || Untouchable.Contains(p.Name)) continue;
                if (st.Exclusions.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
                // Programs with a priority rule are the ones the user cares about (games): never page them out.
                string? path;
                lock (_tracked) path = _tracked.TryGetValue(p.Pid, out var t) ? t.Path : null;
                if (view.Matcher.Match(p.Name, path) is { CpuPriority: not null }) continue;
                if (MemoryTools.TrimWorkingSet(p.Pid)) { count++; trimmed += p.WorkingSet; }
            }
            if (count > 0)
                _log.Add(EventKind.Memory, 0, "prumo.exe", $"SmartTrim: memória de {count} processos em segundo plano liberada (~{trimmed >> 20} MB)");
        }

        if (st.PurgeStandbyList && now - _lastPurge >= 5 * 60_000)
        {
            ulong avail = MemoryTools.AvailableBytes();
            if (avail > 0 && avail < ((ulong)Math.Max(1, st.PurgeBelowFreeMb) << 20))
            {
                _lastPurge = now;
                ulong standby = MemoryTools.StandbyBytes();
                if (MemoryTools.PurgeStandbyList())
                    _log.Add(EventKind.Memory, 0, "prumo.exe", $"Lista de espera limpa (~{standby >> 20} MB): só {avail >> 20} MB livres");
            }
        }
    }

    // ------------------------------------------------------------ power plan

    void UpdatePowerPlan(List<ProcessRule> running)
    {
        Guid? want = null;
        string? who = null;
        var view = _view;
        Guid? Resolve(string name)
        {
            if (!view.PlanIds.TryGetValue(name, out var id)) view.PlanIds[name] = id = PowerPlans.Find(name);
            return id;
        }

        if (view.Cfg.PerformanceMode && view.Cfg.PerformancePlan is { } perf && Resolve(perf) is { } perfId)
        {
            want = perfId; who = "modo desempenho";
        }
        else
            foreach (var r in running)
                if (Resolve(r.PowerPlan!) is { } id) { want = id; who = r.Exe; break; }

        if (want is not null && _planSet != want)
        {
            var active = PowerPlans.Active();
            _planBefore ??= active;
            if (active != want && PowerPlans.SetActive(want.Value))
                _log.Add(EventKind.PowerPlan, 0, who!,
                    (who == "modo desempenho" ? "Modo desempenho" : "Modo jogo") + ": plano de energia " + PlanName(want.Value));
            _planSet = want;
        }
        else if (want is null && _planSet is not null)
        {
            var back = (Live.DefaultPowerPlan is { } n ? PowerPlans.Find(n) : null) ?? _planBefore;
            if (back is not null && PowerPlans.Active() != back && PowerPlans.SetActive(back.Value))
                _log.Add(EventKind.PowerPlan, 0, "prumo.exe", "Modo jogo/desempenho terminou: plano de energia " + PlanName(back.Value));
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
            {
                if (t.RestrainedAt != 0) Restore(pid, t, "Prumo foi fechado");
                if (!t.Boosted && t.LimitedAt == 0) continue;
                var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == 0) continue;
                if (t.Boosted && Native.GetPriorityClass(h) == t.BoostClass) Native.SetPriorityClass(h, t.BoostOriginal);
                if (t.LimitedAt != 0 && Native.GetProcessAffinityMask(h, out var cur, out _) && cur == (nuint)t.LimitMask)
                    Native.SetProcessAffinityMask(h, (nuint)t.LimitOriginal);
                Native.CloseHandle(h);
            }
        if (_planSet is not null)
        {
            var back = (Live.DefaultPowerPlan is { } n ? PowerPlans.Find(n) : null) ?? _planBefore;
            if (back is not null) PowerPlans.SetActive(back.Value);
        }
        if (_timerSet != 0) Native.NtSetTimerResolution(_timerSet, false, out _);
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
    public static string Of(MemoryPriority p) => p switch
    {
        MemoryPriority.VeryLow => "Muito baixa", MemoryPriority.Low => "Baixa", MemoryPriority.Medium => "Média",
        MemoryPriority.BelowNormal => "Abaixo do normal", MemoryPriority.Normal => "Normal", _ => p.ToString(),
    };
    public static string OfBase(int basePriority) => basePriority switch
    {
        4 => "Ociosa", 6 => "Abaixo do normal", 8 => "Normal", 10 => "Acima do normal", 13 => "Alta", 24 => "Tempo real", _ => basePriority.ToString(),
    };
}
