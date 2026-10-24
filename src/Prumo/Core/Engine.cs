using System.Diagnostics;

namespace Prumo.Core;

public sealed record ProcessInfo(
    int Pid, int ParentPid, string Exe, uint SessionId, double Cpu, ulong WorkingSet,
    int BasePriority, bool Restrained, bool HasRule, bool Foreground, string? Path, long CreateTime);

public sealed record EngineSnapshot(
    DateTime Time, IReadOnlyList<ProcessInfo> Processes, double CpuTotal, SystemSample? System,
    int RestrainedCount, int RestraintsStarted, string? PowerPlan);

/// <summary>
/// Once a second: reads every process, applies the rules to new processes, runs ProBalance, the
/// foreground boost, the CPU limiter, the watchdog, disallowed/keep-running lists and SmartTrim, and
/// switches the power plan for "modo jogo" / "modo desempenho" / IdleSaver.
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
        public long RuleFailedAt; public int RuleRounds;               // rule retries for late-opening processes
        public uint SessionId;
        public bool KeepAlive, KilledByUs, DisallowedSeen, WatchLowered;
        public string? CommandLine, WorkDir;
        public Dictionary<WatchdogRule, long>? WatchOver, WatchFired;
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
    readonly Queue<(int Pid, long Create, ulong Ws, string Exe)> _trimQueue = new();
    int _trimCount; ulong _trimBytes;
    bool _awake;                 // SetThreadExecutionState held for a "prevent sleep" rule
    bool _disposed;
    // "Manter rodando": programs waiting to be started again, and how often each was restarted lately.
    readonly List<(string Cmd, string? Dir, string Exe, long Due)> _restarts = new();
    readonly Dictionary<string, (int Count, long Since)> _restartHistory = new(StringComparer.OrdinalIgnoreCase);
    static string StateFile => Path.Combine(AppConfig.Dir, "plan-before.txt");

    // The UI edits Config on its own thread; the engine only reads this immutable copy, swapped on ConfigChanged.
    sealed record View(AppConfig Cfg, Dictionary<string, Guid?> PlanIds, RuleMatcher Matcher,
        RuleMatcher Disallowed, RuleMatcher KeepRunning, List<(WatchdogRule Rule, RuleMatcher Match)> Watchdog)
    {
        public bool NeedsPaths => Matcher.NeedsPaths || Disallowed.NeedsPaths || KeepRunning.NeedsPaths || Watchdog.Any(w => w.Match.NeedsPaths);

        public static View Of(AppConfig cfg)
        {
            var c = cfg.Clone();
            static RuleMatcher Names(IEnumerable<string> names) => new(names.Select(n => new ProcessRule { Exe = n }));
            return new(c, new(StringComparer.OrdinalIgnoreCase), new RuleMatcher(c.Rules), Names(c.Disallowed), Names(c.KeepRunning),
                c.Watchdog.Where(w => w.Enabled).Select(w => (w, Names(new[] { w.Exe }))).ToList());
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
        // A crash or a kill from Task Manager left a game plan on: go back to the plan from before on the first tick.
        try
        {
            if (File.Exists(StateFile) && Guid.TryParse(File.ReadAllText(StateFile).Trim(), out var before))
            {
                _planBefore = before;
                if (PowerPlans.Active() is { } active && active != before) _planSet = active;
            }
        }
        catch { }
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

    /// <summary>Swaps in a whole new configuration (import, reset to defaults).</summary>
    public void ReplaceConfig(AppConfig cfg)
    {
        Config = cfg;
        ConfigChanged();
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
            var wait = Math.Clamp(Live.UpdateIntervalMs, 250, 10_000) - (int)(_clock.ElapsedMilliseconds - started);
            if (wait > 0) Thread.Sleep(wait);
        }
        if (_awake) Native.SetThreadExecutionState(Native.ES_CONTINUOUS);   // execution state belongs to this thread
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
        bool keepAwake = false, gameRunning = false;

        SystemSample? sys = null;
        try { sys = _sampler?.Sample(); } catch { }

        lock (_tracked)
        {
            foreach (var p in procs)
            {
                seen.Add(p.Pid);
                if (!_tracked.TryGetValue(p.Pid, out var t) || t.CreateTime != p.CreateTime)
                    _tracked[p.Pid] = t = new Tracked { CreateTime = p.CreateTime, Exe = p.Name, SessionId = p.SessionId };

                if (t.LastCpuTime >= 0 && wallMs > 0)
                    t.Cpu = Math.Clamp((p.CpuTime - t.LastCpuTime) / 10_000.0 / (wallMs * _cpuCount) * 100, 0, 100);
                t.LastCpuTime = p.CpuTime;
                if (p.Pid != 0) totalCpu += t.Cpu;

                // Paths cost an OpenProcess, so they are only read (once per process) when a rule matches on paths.
                if (view.NeedsPaths && !t.PathResolved && p.Pid > 4)
                {
                    t.Path = PathOf(p.Pid);
                    t.PathResolved = true;
                }
                bool ours = p.Pid > 4 && p.Pid != _selfPid && !Untouchable.Contains(p.Name);
                if (ours && !t.KilledByUs && view.Disallowed.Match(p.Name, t.Path) is not null && Disallow(p, t, cfg.DisallowedLogOnly)) continue;

                var rule = view.Matcher.Match(p.Name, t.Path);
                // A process that refused access (anti-cheat starting up, for example) gets another round every 30 s.
                if (rule is not null && t.RuleVersion != _ruleVersion && t.RuleAttempts >= 5 && t.RuleRounds < 10 && now - t.RuleFailedAt >= 30_000)
                    t.RuleAttempts = 0;
                if (rule is not null && t.RuleVersion != _ruleVersion && t.RuleAttempts < 5 && p.Pid > 4)
                    ApplyRule(p.Pid, t, rule);
                if (rule?.PowerPlan is not null) planRules.Add(rule);
                if (rule?.PreventSleep == true) keepAwake = true;
                if (rule?.CpuPriority is CpuPriority.High or CpuPriority.Realtime) gameRunning = true;

                if (ours && !t.KeepAlive && p.SessionId != 0 && view.KeepRunning.Match(p.Name, t.Path) is not null) RememberForRestart(p.Pid, t);
                if (ours && view.Watchdog.Count > 0) Watchdog(p, t, view, now);

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
                    p.BasePriority, t.RestrainedAt != 0, rule is not null, fg, t.Path, p.CreateTime));
            }

            foreach (var pid in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var t = _tracked[pid];
                if (t.RestrainedAt != 0) _log.Add(EventKind.Restored, pid, t.Exe, "ProBalance terminou: o processo fechou");
                if (t.KeepAlive && !t.KilledByUs && t.CommandLine is not null && view.KeepRunning.Match(t.Exe, t.Path) is not null)
                    QueueRestart(t, now);
                _tracked.Remove(pid);
            }
        }

        // Restraint decisions use the previous tick's total; the new one is only known now.
        _lastTotal = Math.Min(100, totalCpu);

        SmartTrim(procs, fgPid, parents, sys, now, view);
        var mc = view.Cfg.MemoryClean;
        if (mc.AutoAbovePercent > 0 && sys is not null && sys.MemoryLoad >= mc.AutoAbovePercent &&
            now - _lastAutoClean >= mc.AutoMinIntervalMinutes * 60_000L)
        {
            _lastAutoClean = now;
            _log.Add(EventKind.Memory, 0, "prumo.exe", $"RAM em {sys.MemoryLoad:0}%: limpando a memória sozinho");
            _ = CleanMemoryAsync(mc);
        }
        ManualClean(procs, fgPid, parents, view);
        RunRestarts(now);
        UpdatePowerPlan(planRules, gameRunning);
        if (keepAwake != _awake)
        {
            Native.SetThreadExecutionState(keepAwake ? Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED : Native.ES_CONTINUOUS);
            _awake = keepAwake;
            _log.Add(EventKind.Info, 0, "prumo.exe", keepAwake ? "Suspensão bloqueada: um programa com \"impedir suspensão\" está rodando" : "Suspensão liberada de novo");
        }

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
            // Never raise: an Idle/Below normal process is already as low as ProBalance would put it.
            if (Rank(cls) <= Rank(target)) { t.OverSince = 0; return 0; }
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
        if (t.OriginalIo >= 0)
        {
            // Same for I/O: a rule or the user may have set it since.
            if (Native.NtQueryInformationProcess(h, Native.ProcessIoPriority, out int curIo, 4, out _) >= 0 && curIo == (int)IoPriority.Low)
            {
                int io = t.OriginalIo;
                Native.NtSetInformationProcess(h, Native.ProcessIoPriority, ref io, 4);
            }
            t.OriginalIo = -1;
        }
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
            if (t.RuleAttempts == 5)
            {
                t.RuleFailedAt = _clock.ElapsedMilliseconds;
                if (++t.RuleRounds == 1) _log.Add(EventKind.RuleFailed, pid, t.Exe, "Sem acesso ao processo para aplicar a regra (tento de novo a cada 30 s)");
                if (t.RuleRounds >= 10) t.RuleVersion = _ruleVersion;
            }
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

        bool lastTry = t.RuleAttempts == 5;
        if (lastTry && failed.Count > 0) { t.RuleFailedAt = _clock.ElapsedMilliseconds; t.RuleRounds++; }
        if (failed.Count == 0 || (lastTry && t.RuleRounds >= 10)) t.RuleVersion = _ruleVersion;
        if (done.Count > 0 && (failed.Count == 0 || (lastTry && t.RuleRounds == 1)))
            _log.Add(EventKind.RuleApplied, pid, t.Exe, "Regra aplicada: " + string.Join(", ", done));
        if (failed.Count > 0 && lastTry && t.RuleRounds == 1)
            _log.Add(EventKind.RuleFailed, pid, t.Exe, "Falhou: " + string.Join(", ", failed) + " (tento de novo a cada 30 s)");
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

    /// <summary>Priority classes in order (their numeric values are not).</summary>
    static int Rank(uint cls) => cls switch
    {
        IDLE => 0, BELOW_NORMAL => 1, NORMAL => 2, 0x8000 => 3, 0x80 => 4, 0x100 => 5, _ => 2,
    };

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
        if (t.WatchLowered) return;   // the watchdog lowered it on purpose
        if (fb.OnlyNormal && p.BasePriority != 8) return;

        var hh = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (hh == 0) return;
        try
        {
            var cls = Native.GetPriorityClass(hh);
            var target = ToClass(fb.Priority);
            // Boost only raises: a High/Realtime foreground process stays where it is.
            if (cls == 0 || Rank(cls) >= Rank(target) || (fb.OnlyNormal && cls != NORMAL)) return;
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

    // ---------------------------------------------------- disallowed processes

    /// <returns>True when the process was closed (nothing else to do with it this tick).</returns>
    bool Disallow(RawProcess p, Tracked t, bool logOnly)
    {
        if (p.SessionId == 0) return false;   // services and system processes: never
        if (logOnly)
        {
            if (!t.DisallowedSeen) _log.Add(EventKind.Restrained, p.Pid, p.Name, "Processo proibido rodando (só registrando)");
            t.DisallowedSeen = true;
            return false;
        }
        if (!KillVerified(p.Pid, p.CreateTime))
        {
            if (!t.DisallowedSeen) _log.Add(EventKind.RuleFailed, p.Pid, p.Name, "Processo proibido: não consegui fechar");
            t.DisallowedSeen = true;
            return false;
        }
        t.KilledByUs = true;
        _log.Add(EventKind.Restrained, p.Pid, p.Name, "Processo proibido fechado");
        return true;
    }

    // ------------------------------------------------------------ keep running

    /// <summary>Reads what is needed to start the process again (once, while it still runs).</summary>
    void RememberForRestart(int pid, Tracked t)
    {
        t.KeepAlive = true;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return;
        try
        {
            t.CommandLine = Native.CommandLine(h);
            t.Path ??= Native.ImagePath(h);
            t.WorkDir = t.Path is null ? null : Path.GetDirectoryName(t.Path);
        }
        finally { Native.CloseHandle(h); }
    }

    void QueueRestart(Tracked t, long now)
    {
        var cmd = t.CommandLine!;
        if (_restarts.Any(r => r.Cmd == cmd)) return;
        // Backoff (2 s, 4 s, 8 s...) and a limit, so a program that crashes on start is not reopened forever.
        var (count, since) = _restartHistory.GetValueOrDefault(cmd, (0, now));
        if (now - since > 600_000) (count, since) = (0, now);
        _restartHistory[cmd] = (count + 1, since);
        if (count >= 5)
        {
            if (count == 5) _log.Add(EventKind.RuleFailed, 0, t.Exe, "Manter rodando: fechou 5 vezes em 10 minutos, parei de reabrir");
            return;
        }
        _restarts.Add((cmd, t.WorkDir, t.Exe, now + (2000L << count)));
    }

    void RunRestarts(long now)
    {
        for (int i = _restarts.Count - 1; i >= 0; i--)
        {
            var r = _restarts[i];
            if (now < r.Due) continue;
            _restarts.RemoveAt(i);
            bool running;
            lock (_tracked) running = _tracked.Values.Any(x => !x.KilledByUs && string.Equals(x.Exe, r.Exe, StringComparison.OrdinalIgnoreCase));
            if (running) continue;   // the user (or the program's own updater) already opened it
            var err = Launcher.StartAsUser(r.Cmd, r.Dir);
            _log.Add(err is null ? EventKind.RuleApplied : EventKind.RuleFailed, 0, r.Exe,
                err is null ? "Aberto de novo (manter rodando / vigia)" : "Não consegui abrir de novo: " + err);
        }
    }

    // --------------------------------------------------------------- watchdog

    void Watchdog(RawProcess p, Tracked t, View view, long now)
    {
        foreach (var (w, match) in view.Watchdog)
        {
            if (match.Match(p.Name, t.Path) is null) continue;
            double value = w.Metric == WatchMetric.Cpu ? t.Cpu : p.WorkingSet / 1048576.0;
            t.WatchOver ??= new(); t.WatchFired ??= new();
            // Hysteresis: it must drop 10% under the threshold to count as back to normal.
            if (value < w.Threshold * 0.9) { t.WatchOver.Remove(w); continue; }
            if (value < w.Threshold) continue;
            if (!t.WatchOver.TryGetValue(w, out var since)) { t.WatchOver[w] = now; continue; }
            if (now - since < w.ForSeconds * 1000L) continue;
            // After firing, wait at least a minute (or the rule's own time) before firing again for this process.
            if (t.WatchFired.TryGetValue(w, out var fired) && now - fired < Math.Max(60_000L, w.ForSeconds * 1000L)) continue;
            t.WatchFired[w] = now;
            t.WatchOver.Remove(w);
            Fire(p, t, w, value, now);
        }
    }

    void Fire(RawProcess p, Tracked t, WatchdogRule w, double value, long now)
    {
        var what = w.Metric == WatchMetric.Cpu ? $"CPU {value:0}% por {w.ForSeconds} s" : $"{value:0} MB de RAM por {w.ForSeconds} s";
        string result;
        switch (w.Action)
        {
            case WatchAction.LowerPriority:
                result = LowerNow(p.Pid, p.CreateTime, t) ? "prioridade reduzida para Abaixo do normal" : "não consegui reduzir a prioridade";
                break;
            case WatchAction.TrimMemory:
                result = MemoryTools.TrimWorkingSet(p.Pid) ? "memória liberada" : "não consegui liberar a memória";
                break;
            case WatchAction.Kill when p.SessionId == 0:
            case WatchAction.Restart when p.SessionId == 0:
                result = "é um processo do sistema, deixei como está";
                break;
            case WatchAction.Kill:
                t.KilledByUs = KillVerified(p.Pid, p.CreateTime);
                result = t.KilledByUs ? "processo fechado" : "não consegui fechar";
                break;
            case WatchAction.Restart:
                if (t.CommandLine is null) RememberForRestart(p.Pid, t);
                if (t.CommandLine is null) { result = "não consegui ler a linha de comando"; break; }
                if (!KillVerified(p.Pid, p.CreateTime)) { result = "não consegui fechar"; break; }
                t.KilledByUs = true;
                _restarts.Add((t.CommandLine, t.WorkDir, t.Exe, now + 1500));
                result = "processo fechado, abrindo de novo";
                break;
            default:
                result = "só registrado";
                break;
        }
        _log.Add(EventKind.Restrained, p.Pid, p.Name, $"Vigia: {what} → {result}");
    }

    bool LowerNow(int pid, long createTime, Tracked t)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try
        {
            if (!Native.IsSameProcess(h, createTime)) return false;
            var cls = Native.GetPriorityClass(h);
            if (cls != 0 && Rank(cls) <= Rank(BELOW_NORMAL)) return true;
            if (!Native.SetPriorityClass(h, BELOW_NORMAL)) return false;
            // ProBalance or the foreground boost must not undo this when they restore.
            if (t.RestrainedAt != 0) t.OriginalClass = BELOW_NORMAL;
            t.Boosted = false;
            t.WatchLowered = true;
            return true;
        }
        finally { Native.CloseHandle(h); }
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
            _trimQueue.Clear(); _trimCount = 0; _trimBytes = 0;
            ulong min = (ulong)Math.Max(1, st.MinProcessMb) << 20;
            foreach (var p in procs)
                if (p.WorkingSet >= min && TrimCandidate(p, fgPid, parents, view))
                    _trimQueue.Enqueue((p.Pid, p.CreateTime, p.WorkingSet, p.Name));
        }

        // A few processes per tick: trimming dozens at once stalls the loop (and the disk).
        if (_trimQueue.Count > 0)
        {
            for (int i = 0; i < 6 && _trimQueue.TryDequeue(out var q); i++)
                if (MemoryTools.TrimWorkingSet(q.Pid, q.Create)) { _trimCount++; _trimBytes += q.Ws; }
            if (_trimQueue.Count == 0 && _trimCount > 0)
                _log.Add(EventKind.Memory, 0, "prumo.exe", $"SmartTrim: memória de {_trimCount} processos em segundo plano liberada (~{_trimBytes >> 20} MB)");
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

    /// <summary>A background process whose memory may be trimmed: never the foreground one, its children,
    /// system processes, SmartTrim exclusions or programs with a priority rule (games).</summary>
    bool TrimCandidate(RawProcess p, int fgPid, Dictionary<int, (int Parent, long Create)> parents, View view)
    {
        if (p.Pid <= 4 || p.Pid == _selfPid || p.SessionId == 0) return false;
        if (p.Pid == fgPid || IsDescendant(p.Pid, fgPid, parents) || Untouchable.Contains(p.Name)) return false;
        if (view.Cfg.SmartTrim.Exclusions.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) return false;
        string? path;
        lock (_tracked) path = _tracked.TryGetValue(p.Pid, out var t) ? t.Path : null;
        return view.Matcher.Match(p.Name, path) is not { CpuPriority: not null };
    }

    // ------------------------------------------------- memory clean (manual)

    readonly object _cleanLock = new();
    TaskCompletionSource<MemoryCleanResult>? _cleanPending;
    MemoryCleanSettings? _cleanSettings;
    bool _cleanRunning;
    long _lastAutoClean = -24 * 3_600_000L;

    /// <summary>"Limpar memória agora": the next tick picks the processes to trim and the cleaning runs on
    /// the thread pool. A request while one is running gets the running one's result.</summary>
    public Task<MemoryCleanResult> CleanMemoryAsync(MemoryCleanSettings settings)
    {
        lock (_cleanLock)
        {
            if (_cleanPending is not null) return _cleanPending.Task;
            _cleanPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanSettings = settings;
            return _cleanPending.Task;
        }
    }

    void ManualClean(List<RawProcess> procs, int fgPid, Dictionary<int, (int Parent, long Create)> parents, View view)
    {
        TaskCompletionSource<MemoryCleanResult> tcs; MemoryCleanSettings s;
        lock (_cleanLock)
        {
            if (_cleanPending is null || _cleanSettings is null || _cleanRunning) return;
            tcs = _cleanPending; s = _cleanSettings; _cleanRunning = true;
        }
        var targets = s.WorkingSets
            ? procs.Where(p => p.WorkingSet >= 8 << 20 && TrimCandidate(p, fgPid, parents, view)).Select(p => (p.Pid, p.CreateTime)).ToList()
            : new List<(int, long)>();
        Task.Run(() =>
        {
            var r = MemoryTools.Clean(s, targets);
            _log.Add(EventKind.Memory, 0, "prumo.exe", $"Memória limpa: +{r.FreedBytes >> 20} MB livres ({r.Trimmed} processos em segundo plano)"
                + (r.Failed.Count > 0 ? "; não consegui limpar: " + string.Join(", ", r.Failed) : ""));
            lock (_cleanLock) { _cleanPending = null; _cleanSettings = null; _cleanRunning = false; }
            tcs.TrySetResult(r);
        });
    }

    // ------------------------------------------------------------ power plan

    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Something fullscreen in front (a game, a movie, a presentation): no keyboard/mouse input does not mean idle.</summary>
    static bool FullscreenInFront() =>
        SHQueryUserNotificationState(out var st) == 0 && st is 2 or 3 or 4;   // QUNS_BUSY, RUNNING_D3D_FULL_SCREEN, PRESENTATION_MODE

    void UpdatePowerPlan(List<ProcessRule> running, bool gameRunning)
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

        // IdleSaver only when nothing else asked for a plan: a game or "modo desempenho" always wins. A controller
        // game or a movie has no keyboard/mouse input, so programs with a High priority rule and fullscreen windows block it too.
        if (want is null && !gameRunning && view.Cfg.IdleSaver is { Enabled: true, IdlePlan: { } idlePlan } idle &&
            Native.IdleMilliseconds() >= (uint)idle.IdleMinutes * 60_000u && !FullscreenInFront() && Resolve(idlePlan) is { } idleId)
        {
            want = idleId; who = "IdleSaver";
        }

        if (want is not null && _planSet != want)
        {
            var active = PowerPlans.Active();
            if (_planBefore is null && active is not null)
            {
                _planBefore = active;
                try { File.WriteAllText(StateFile, active.Value.ToString()); } catch { }
            }
            if (active != want && PowerPlans.SetActive(want.Value))
                _log.Add(EventKind.PowerPlan, 0, who == "IdleSaver" ? "prumo.exe" : who!,
                    who switch { "modo desempenho" => "Modo desempenho", "IdleSaver" => "IdleSaver (computador ocioso)", _ => "Modo jogo" }
                    + ": plano de energia " + PlanName(want.Value));
            _planSet = want;
        }
        else if (want is null && _planSet is not null)
        {
            var back = (Live.DefaultPowerPlan is { } n ? PowerPlans.Find(n) : null) ?? _planBefore;
            if (back is not null && PowerPlans.Active() != back && PowerPlans.SetActive(back.Value))
                _log.Add(EventKind.PowerPlan, 0, "prumo.exe", "De volta ao plano de energia normal: " + PlanName(back.Value));
            _planSet = null; _planBefore = null;
            try { File.Delete(StateFile); } catch { }
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
    /// <remarks>Manual actions take the creation time from the snapshot: the menu can stay open long enough for the PID to be reused.</remarks>
    public bool SetPriorityNow(int pid, long createTime, CpuPriority p)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try { return Native.IsSameProcess(h, createTime) && Native.SetPriorityClass(h, ToClass(p)); } finally { Native.CloseHandle(h); }
    }

    public bool SetAffinityNow(int pid, long createTime, ulong mask)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try { return Native.IsSameProcess(h, createTime) && Native.SetProcessAffinityMask(h, (nuint)mask); } finally { Native.CloseHandle(h); }
    }

    public static ulong? GetAffinity(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try { return Native.GetProcessAffinityMask(h, out var pm, out _) ? pm : null; } finally { Native.CloseHandle(h); }
    }

    /// <summary>"Finalizar processo" from the list: also tells "manter rodando" not to reopen it.</summary>
    public bool KillNow(int pid, long createTime)
    {
        if (!KillVerified(pid, createTime)) return false;
        lock (_tracked) if (_tracked.TryGetValue(pid, out var t) && t.CreateTime == createTime) t.KilledByUs = true;
        return true;
    }

    /// <summary>Terminates the process only if it is still the one created at createTime.</summary>
    public static bool KillVerified(int pid, long createTime)
    {
        var h = Native.OpenProcess(Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try { return Native.IsSameProcess(h, createTime) && Native.TerminateProcess(h, 1); } finally { Native.CloseHandle(h); }
    }

    public static string? PathOf(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try { return Native.ImagePath(h); } finally { Native.CloseHandle(h); }
    }

    public void Dispose()
    {
        if (_disposed) return;   // OnExit and SessionEnding can both call it
        _disposed = true;
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
        try { File.Delete(StateFile); } catch { }
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
