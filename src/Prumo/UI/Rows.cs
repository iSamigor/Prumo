using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Prumo.Core;

namespace Prumo.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProcessRow : Observable
{
    public int Pid { get; }
    public string Exe { get; }
    public ProcessRow(int pid, string exe) { Pid = pid; Exe = exe; }

    double _cpu; ulong _mem; int _prio; bool _restrained, _rule, _fg;
    public double Cpu => _cpu;
    public ulong Mem => _mem;
    public int Priority => _prio;

    string _cpuText = "", _memText = "", _prioText = "";
    double _barWidth;
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public double CpuBarWidth { get => _barWidth; private set => Set(ref _barWidth, value); }
    public string MemText { get => _memText; private set => Set(ref _memText, value); }
    public string PriorityText { get => _prioText; private set => Set(ref _prioText, value); }
    public Visibility RestrainedVisibility => _restrained ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuleVisibility => _rule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ForegroundVisibility => _fg ? Visibility.Visible : Visibility.Collapsed;

    public void Update(ProcessInfo p)
    {
        _cpu = p.Cpu; _mem = p.WorkingSet; _prio = p.BasePriority;
        CpuText = p.Cpu < 0.05 ? "0" : p.Cpu.ToString(p.Cpu < 10 ? "0.0" : "0");
        CpuBarWidth = Math.Clamp(p.Cpu, 0, 100) / 100 * 76;
        MemText = Format.Bytes(p.WorkingSet);
        PriorityText = Names.OfBase(p.BasePriority);
        if (_restrained != p.Restrained) { _restrained = p.Restrained; Raise(nameof(RestrainedVisibility)); }
        if (_rule != p.HasRule) { _rule = p.HasRule; Raise(nameof(RuleVisibility)); }
        if (_fg != p.Foreground) { _fg = p.Foreground; Raise(nameof(ForegroundVisibility)); }
    }
}

public sealed record Option<T>(string Label, T? Value)
{
    public override string ToString() => Label;
}

public sealed class RuleRow : Observable
{
    readonly ProcessRule _rule;
    readonly Action _changed;
    public ProcessRule Rule => _rule;

    public static readonly List<Option<CpuPriority?>> CpuChoices =
        new[] { new Option<CpuPriority?>("Não alterar", null) }
        .Concat(Enum.GetValues<CpuPriority>().Select(v => new Option<CpuPriority?>(Names.Of(v), v))).ToList();
    public static readonly List<Option<IoPriority?>> IoChoices =
        new[] { new Option<IoPriority?>("Não alterar", null) }
        .Concat(Enum.GetValues<IoPriority>().Select(v => new Option<IoPriority?>(Names.Of(v), v))).ToList();
    public static readonly List<Option<GpuPriority?>> GpuChoices =
        new[] { new Option<GpuPriority?>("Não alterar", null) }
        .Concat(Enum.GetValues<GpuPriority>().Select(v => new Option<GpuPriority?>(Names.Of(v), v))).ToList();
    public static readonly List<Option<EfficiencyMode?>> EffChoices = new()
    {
        new("Não alterar", null), new("Desligado", EfficiencyMode.Off), new("Ligado", EfficiencyMode.On),
    };

    public static readonly List<Option<MemoryPriority?>> MemChoices =
        new[] { new Option<MemoryPriority?>("Não alterar", null) }
        .Concat(Enum.GetValues<MemoryPriority>().Reverse().Select(v => new Option<MemoryPriority?>(Names.Of(v), v))).ToList();
    public static readonly List<Option<GpuPreference?>> GpuPrefChoices = new()
    {
        new("Deixar o Windows decidir", null), new("Alto desempenho (placa dedicada)", Core.GpuPreference.HighPerformance),
        new("Economia de energia (integrada)", Core.GpuPreference.PowerSaving),
    };
    public static readonly List<Option<bool?>> BoostChoices = new()
    {
        new("Não alterar", null), new("Desligado", true), new("Ligado", false),
    };

    public List<Option<MemoryPriority?>> MemOptions => MemChoices;
    public List<Option<GpuPreference?>> GpuPrefOptions => GpuPrefChoices;
    public List<Option<bool?>> BoostOptions => BoostChoices;
    public List<Option<CpuPriority?>> CpuOptions => CpuChoices;
    public List<Option<IoPriority?>> IoOptions => IoChoices;
    public List<Option<GpuPriority?>> GpuOptions => GpuChoices;
    public List<Option<EfficiencyMode?>> EfficiencyOptions => EffChoices;
    public List<Option<string>> PlanOptions { get; }

    public RuleRow(ProcessRule rule, List<Option<string>> plans, Action changed)
    {
        _rule = rule; _changed = changed;
        PlanOptions = plans;
        if (rule.PowerPlan is { } p && !plans.Any(o => o.Value == p)) PlanOptions = plans.Append(new Option<string>(p + " (não encontrado)", p)).ToList();
    }

    public string Exe => _rule.Exe;
    public string Kind => _rule.MatchesPath ? "pasta (todos os programas dentro dela)" : _rule.IsPattern ? "padrão de nome" : "programa";

    public Option<MemoryPriority?> Memory
    {
        get => MemChoices.First(o => o.Value == _rule.MemoryPriority);
        set { _rule.MemoryPriority = value?.Value; _changed(); }
    }
    public Option<GpuPreference?> GpuPref
    {
        get => GpuPrefChoices.First(o => o.Value == _rule.GpuPreference);
        set { _rule.GpuPreference = value?.Value; _changed(); }
    }
    public Option<bool?> Boost
    {
        get => BoostChoices.First(o => o.Value == _rule.DisablePriorityBoost);
        set { _rule.DisablePriorityBoost = value?.Value; _changed(); }
    }
    public string CpuSets
    {
        get => _rule.CpuSets ?? "";
        set
        {
            var t = value.Trim();
            if (t.Length == 0) _rule.CpuSets = null;
            else if (Core.Affinity.Parse(t) is var m and not 0) _rule.CpuSets = Core.Affinity.Format(m);
            else { MessageBox.Show("CPU Sets inválido. Use algo como 2-7 ou 0,2,4-6.", "Prumo"); Raise(nameof(CpuSets)); return; }
            Raise(nameof(CpuSets));
            _changed();
        }
    }

    // CPU limiter: all three fields filled = on; clearing the percentage turns it off.
    public string LimitPercent
    {
        get => _rule.CpuLimit is { } l ? l.ThresholdPercent.ToString("0.#") : "";
        set
        {
            var t = value.Trim().Replace(',', '.');
            if (t.Length == 0) _rule.CpuLimit = null;
            else if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is > 0 and <= 100)
                (_rule.CpuLimit ??= new CpuLimit()).ThresholdPercent = v;
            else { Raise(nameof(LimitPercent)); return; }
            RaiseLimit(); _changed();
        }
    }
    public string LimitCores
    {
        get => _rule.CpuLimit?.Cores.ToString() ?? "";
        set
        {
            if (int.TryParse(value.Trim(), out var v) && v >= 1 && v <= Environment.ProcessorCount)
            { (_rule.CpuLimit ??= new CpuLimit()).Cores = v; _changed(); }
            RaiseLimit();
        }
    }
    public string LimitSeconds
    {
        get => _rule.CpuLimit is { } l ? (l.DurationMs / 1000.0).ToString("0.#") : "";
        set
        {
            var t = value.Trim().Replace(',', '.');
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 3600)
            { (_rule.CpuLimit ??= new CpuLimit()).DurationMs = (int)(v * 1000); _changed(); }
            RaiseLimit();
        }
    }
    public bool IsPattern => _rule.IsPattern;
    public string ExceptText
    {
        get => _rule.Except is { Count: > 0 } e ? string.Join(", ", e) : "";
        set
        {
            var list = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()).Distinct().ToList();
            _rule.Except = list.Count > 0 ? list : null;
            Raise(nameof(ExceptText));
            _changed();
        }
    }

    void RaiseLimit() { Raise(nameof(LimitPercent)); Raise(nameof(LimitCores)); Raise(nameof(LimitSeconds)); }

    string _running = "";
    public string Running { get => _running; set => Set(ref _running, value); }

    public Option<CpuPriority?> Cpu
    {
        get => CpuChoices.First(o => o.Value == _rule.CpuPriority);
        set { _rule.CpuPriority = value?.Value; _changed(); }
    }
    public Option<IoPriority?> Io
    {
        get => IoChoices.First(o => o.Value == _rule.IoPriority);
        set { _rule.IoPriority = value?.Value; _changed(); }
    }
    public Option<GpuPriority?> Gpu
    {
        get => GpuChoices.First(o => o.Value == _rule.GpuPriority);
        set { _rule.GpuPriority = value?.Value; _changed(); }
    }
    public Option<EfficiencyMode?> Efficiency
    {
        get => EffChoices.First(o => o.Value == _rule.Efficiency);
        set { _rule.Efficiency = value?.Value; _changed(); }
    }
    public Option<string> Plan
    {
        get => PlanOptions.First(o => o.Value == _rule.PowerPlan);
        set { _rule.PowerPlan = value?.Value; _changed(); }
    }
    public string Affinity
    {
        get => _rule.Affinity ?? "";
        set
        {
            var t = value.Trim();
            if (t.Length == 0) _rule.Affinity = null;
            else if (Core.Affinity.Parse(t) is var m and not 0) _rule.Affinity = Core.Affinity.Format(m);
            else { MessageBox.Show("Afinidade inválida. Use algo como 1-7 ou 0,2,4-6.", "Prumo"); Raise(nameof(Affinity)); return; }
            Raise(nameof(Affinity));
            _changed();
        }
    }
    public bool Excluded
    {
        get => _rule.ProBalanceExcluded;
        set { _rule.ProBalanceExcluded = value; _changed(); }
    }
}

public sealed class LogRow
{
    static readonly Brush Amber = Frozen(0xF5, 0x9E, 0x0B), Teal = Frozen(0x2D, 0xD4, 0xBF), Red = Frozen(0xF4, 0x3F, 0x5E),
                          Blue = Frozen(0x60, 0xA5, 0xFA), Gray = Frozen(0x8A, 0x9B, 0xB8), Violet = Frozen(0xA7, 0x8B, 0xFA);

    public LogEntry Entry { get; }
    public LogRow(LogEntry e) => Entry = e;
    public string Time => Entry.Time.ToString("HH:mm:ss");
    public string Who => Entry.Pid > 0 ? $"{Entry.Exe}  ·  {Entry.Pid}" : Entry.Exe;
    public string Text => Entry.Text;
    public string Icon => Entry.Kind switch
    {
        EventKind.Memory => "",
        EventKind.Restrained => "", EventKind.Restored => "", EventKind.RuleApplied => "",
        EventKind.RuleFailed => "", EventKind.PowerPlan => "", _ => "",
    };
    public Brush Color => Entry.Kind switch
    {
        EventKind.Restrained => Amber, EventKind.Restored => Teal, EventKind.RuleApplied => Blue,
        EventKind.RuleFailed => Red, EventKind.PowerPlan => Violet, EventKind.Memory => Teal, _ => Gray,
    };

    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}

public static class Format
{
    public static string Bytes(ulong b) => b switch
    {
        >= 1UL << 30 => $"{b / (double)(1UL << 30):0.0} GB",
        >= 1UL << 20 => $"{b / (double)(1UL << 20):0} MB",
        _ => $"{b / 1024.0:0} KB",
    };
}
