using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Prumo.Core;

namespace Prumo.UI;

public partial class MainWindow : Window
{
    readonly Engine _engine;
    readonly ActivityLog _log;
    readonly ObservableCollection<ProcessRow> _rows = new();
    readonly Dictionary<int, ProcessRow> _rowByPid = new();
    readonly ObservableCollection<RuleRow> _rules = new();
    readonly ObservableCollection<LogRow> _logRows = new();
    readonly List<CoreBar> _cores = new();
    string _sort = "cpu";
    bool _sortDesc = true;
    int _restraintsToday;
    DateTime _today = DateTime.Today;
    bool _loading;

    public bool AllowClose { get; set; }

    public MainWindow(Engine engine, ActivityLog log)
    {
        _engine = engine; _log = log;
        InitializeComponent();

        MainGraph.Add("CPU", (Color)FindResource("AccentColor"), fill: true);
        MainGraph.Add("Memória", (Color)FindResource("VioletColor"));
        MainGraph.Add("GPU", Color.FromRgb(0x60, 0xA5, 0xFA));

        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            var bar = new CoreBar { Height = 64, Margin = new Thickness(3, 0, 3, 0) };
            var label = new TextBlock { Text = i.ToString(), Style = (Style)FindResource("Muted"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            var sp = new StackPanel(); sp.Children.Add(bar); sp.Children.Add(label);
            CoreList.Items.Add(sp);
            _cores.Add(bar);
        }
        CpuSub.Text = $"{Environment.ProcessorCount} núcleos lógicos";

        ProcList.ItemsSource = _rows;
        RuleList.ItemsSource = _rules;
        LogList.ItemsSource = _logRows;

        LogFilter.ItemsSource = new[] { "Tudo", "ProBalance", "Regras", "Plano de energia", "Erros" };
        LogFilter.SelectedIndex = 0;

        _restraintsToday = log.Snapshot().Count(e => e.Kind == EventKind.Restrained && e.Time.Date == DateTime.Today);
        log.Added += e => Dispatcher.BeginInvoke(() => OnLogAdded(e));

        engine.Updated += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));

        LoadProBalance();
        LoadPerformance();
        LoadRules();
        LoadSettings();

        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        AboutText.Text = $"Prumo {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}  ·  configuração em {AppConfig.FilePath}";
    }

    // ------------------------------------------------------------- window

    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose && _engine.Config.CloseToTray) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
        if (!AllowClose) Application.Current.Shutdown();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Maximized borderless windows overflow the screen by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview is null) return;
        var tag = (string)((FrameworkElement)sender).Tag;
        PageOverview.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        PageProcesses.Visibility = tag == "Processes" ? Visibility.Visible : Visibility.Collapsed;
        PageRules.Visibility = tag == "Rules" ? Visibility.Visible : Visibility.Collapsed;
        PageProBalance.Visibility = tag == "ProBalance" ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;
        PagePerformance.Visibility = tag == "Performance" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Performance") LoadPerformance();
        PageSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Settings") LoadSettings();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------- snapshot

    void OnSnapshot(EngineSnapshot s)
    {
        if (DateTime.Today != _today) { _today = DateTime.Today; _restraintsToday = 0; }

        bool pbOn = _engine.Config.ProBalance.Enabled;
        StatusDot.Fill = (Brush)FindResource(!pbOn ? "MutedBrush" : s.RestrainedCount > 0 ? "Warn" : "Accent");
        StatusText.Text = !pbOn ? "ProBalance desligado" : s.RestrainedCount > 0 ? $"Restringindo {s.RestrainedCount}" : "ProBalance ativo";
        PlanText.Text = s.PowerPlan ?? "—";

        if (!IsVisible || WindowState == WindowState.Minimized) return;

        var sys = s.System;
        double mem = sys?.MemoryLoad ?? 0, gpu = sys?.GpuTotal ?? -1;
        MainGraph.Push(new[] { s.CpuTotal, mem, Math.Max(0, gpu) }, s.RestraintsStarted);

        CpuValue.Text = $"{s.CpuTotal:0}%";
        MemValue.Text = $"{mem:0}%";
        MemSub.Text = sys is null ? "—" : $"{Format.Bytes(sys.MemoryUsedBytes)} de {Format.Bytes(sys.MemoryTotalBytes)}";
        GpuValue.Text = gpu < 0 ? "—" : $"{gpu:0}%";
        PbValue.Text = s.RestrainedCount.ToString();
        PbSub.Text = $"restringindo agora · {_restraintsToday} hoje";
        if (sys is not null)
            for (int i = 0; i < _cores.Count && i < sys.CpuPerCore.Length; i++) _cores[i].Value = sys.CpuPerCore[i];

        var top = s.Processes.Where(p => p.Pid != 0).MaxBy(p => p.Cpu);
        TopProc.Text = top is null ? "" : $"mais ativo: {top.Exe} ({top.Cpu:0.0}%)";
        OverviewSub.Text = $"{s.Processes.Count} processos · {_engine.Config.Rules.Count} regras · plano {s.PowerPlan ?? "—"}";

        if (PageProcesses.Visibility == Visibility.Visible) UpdateProcesses(s);
        if (PageRules.Visibility == Visibility.Visible) UpdateRuleRunning(s);
    }

    // ------------------------------------------------------------ processes

    void UpdateProcesses(EngineSnapshot s)
    {
        var filter = ProcFilter.Text.Trim();
        var seen = new HashSet<int>();
        foreach (var p in s.Processes)
        {
            if (p.Pid == 0) continue;
            if (filter.Length > 0 && !p.Exe.Contains(filter, StringComparison.OrdinalIgnoreCase) && !p.Pid.ToString().StartsWith(filter)) continue;
            seen.Add(p.Pid);
            if (!_rowByPid.TryGetValue(p.Pid, out var row) || row.Exe != p.Exe)
            {
                if (row is not null) _rows.Remove(row);
                row = new ProcessRow(p.Pid, p.Exe);
                _rowByPid[p.Pid] = row;
                _rows.Add(row);
            }
            row.Update(p);
        }
        foreach (var pid in _rowByPid.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(_rowByPid[pid]);
            _rowByPid.Remove(pid);
        }

        // Keep the selection while re-sorting: move items instead of replacing the collection.
        var sorted = Sorted(_rows).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = _rows.IndexOf(sorted[i]);
            if (cur != i) _rows.Move(cur, i);
        }
        int restrained = s.Processes.Count(p => p.Restrained);
        ProcCount.Text = $"{s.Processes.Count - 1} processos · {restrained} restringidos pelo ProBalance";
    }

    IEnumerable<ProcessRow> Sorted(IEnumerable<ProcessRow> rows)
    {
        Func<ProcessRow, IComparable> key = _sort switch
        {
            "name" => r => r.Exe.ToLowerInvariant(),
            "pid" => r => r.Pid,
            "mem" => r => r.Mem,
            "prio" => r => r.Priority,
            _ => r => r.Cpu,
        };
        return _sortDesc ? rows.OrderByDescending(key).ThenBy(r => r.Pid) : rows.OrderBy(key).ThenBy(r => r.Pid);
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        if (_sort == tag) _sortDesc = !_sortDesc;
        else { _sort = tag; _sortDesc = tag is "cpu" or "mem" or "prio"; }
        if (_engine.Last is { } s) UpdateProcesses(s);
    }

    void ProcFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ProcFilterHint.Visibility = ProcFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_engine.Last is { } s) UpdateProcesses(s);
    }

    void ProcList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ProcList.SelectedItem is not ProcessRow row) { e.Handled = true; return; }
        BuildProcessMenu(row);
    }

    void BuildProcessMenu(ProcessRow row)
    {
        ProcMenu.Items.Clear();
        var exe = row.Exe;
        var rule = _engine.Config.RuleFor(exe);

        MenuItem Item(string header, Action? click = null, bool check = false)
        {
            var m = new MenuItem { Header = header, IsChecked = check };
            if (click is not null) m.Click += (_, _) => click();
            return m;
        }

        var now = Item("Prioridade de CPU agora");
        foreach (var p in Enum.GetValues<CpuPriority>())
            now.Items.Add(Item(Names.Of(p), () =>
            {
                if (!_engine.SetPriorityNow(row.Pid, p)) Toast("Sem permissão para mudar a prioridade de " + exe);
            }));
        ProcMenu.Items.Add(now);

        var always = Item("Prioridade de CPU sempre");
        always.Items.Add(Item("Não alterar", () => EditRule(exe, r => r.CpuPriority = null), rule?.CpuPriority is null));
        foreach (var p in Enum.GetValues<CpuPriority>())
            always.Items.Add(Item(Names.Of(p), () => EditRule(exe, r => r.CpuPriority = p), rule?.CpuPriority == p));
        ProcMenu.Items.Add(always);

        var io = Item("Prioridade de I/O sempre");
        io.Items.Add(Item("Não alterar", () => EditRule(exe, r => r.IoPriority = null), rule?.IoPriority is null));
        foreach (var p in Enum.GetValues<IoPriority>())
            io.Items.Add(Item(Names.Of(p), () => EditRule(exe, r => r.IoPriority = p), rule?.IoPriority == p));
        ProcMenu.Items.Add(io);

        var gpu = Item("Prioridade de GPU sempre");
        gpu.Items.Add(Item("Não alterar", () => EditRule(exe, r => r.GpuPriority = null), rule?.GpuPriority is null));
        foreach (var p in Enum.GetValues<GpuPriority>())
            gpu.Items.Add(Item(Names.Of(p), () => EditRule(exe, r => r.GpuPriority = p), rule?.GpuPriority == p));
        ProcMenu.Items.Add(gpu);

        ProcMenu.Items.Add(Item("Afinidade de CPU...", () => AskAffinity(row)));

        var eff = Item("Modo de eficiência sempre");
        eff.Items.Add(Item("Não alterar", () => EditRule(exe, r => r.Efficiency = null), rule?.Efficiency is null));
        eff.Items.Add(Item("Desligado", () => EditRule(exe, r => r.Efficiency = EfficiencyMode.Off), rule?.Efficiency == EfficiencyMode.Off));
        eff.Items.Add(Item("Ligado", () => EditRule(exe, r => r.Efficiency = EfficiencyMode.On), rule?.Efficiency == EfficiencyMode.On));
        ProcMenu.Items.Add(eff);

        var game = Item("Modo jogo (plano de energia)");
        game.Items.Add(Item("Desligado", () => EditRule(exe, r => r.PowerPlan = null), rule?.PowerPlan is null));
        foreach (var plan in PowerPlans.List())
            game.Items.Add(Item(plan.Name, () => EditRule(exe, r => r.PowerPlan = plan.Name),
                string.Equals(rule?.PowerPlan, plan.Name, StringComparison.OrdinalIgnoreCase)));
        ProcMenu.Items.Add(game);

        var mem = Item("Prioridade de memória sempre");
        mem.Items.Add(Item("Não alterar", () => EditRule(exe, r => r.MemoryPriority = null), rule?.MemoryPriority is null));
        foreach (var p in Enum.GetValues<MemoryPriority>().Reverse())
            mem.Items.Add(Item(Names.Of(p), () => EditRule(exe, r => r.MemoryPriority = p), rule?.MemoryPriority == p));
        ProcMenu.Items.Add(mem);

        var pref = Item("GPU preferida");
        foreach (var o in RuleRow.GpuPrefChoices)
            pref.Items.Add(Item(o.Label, () => EditRule(exe, r => r.GpuPreference = o.Value), rule?.GpuPreference == o.Value));
        ProcMenu.Items.Add(pref);

        ProcMenu.Items.Add(Item("Excluir do ProBalance", () => EditRule(exe, r => r.ProBalanceExcluded = !r.ProBalanceExcluded),
            rule?.ProBalanceExcluded == true));

        ProcMenu.Items.Add(Item("Tratar como jogo (regra para a pasta dele)", () =>
        {
            var path = Engine.PathOf(row.Pid);
            if (path is null) { Toast("Não consegui ler a pasta de " + exe); return; }
            var pattern = Path.GetDirectoryName(path) + @"\*";
            EditRule(pattern, ApplyGamePreset);
            _log.Add(EventKind.Info, 0, "prumo.exe", "Regra de jogo criada para " + pattern);
        }));

        ProcMenu.Items.Add(new Separator());
        ProcMenu.Items.Add(Item("Abrir local do arquivo", () =>
        {
            var path = Engine.PathOf(row.Pid);
            if (path is not null) Process.Start("explorer.exe", $"/select,\"{path}\"");
        }));
        ProcMenu.Items.Add(Item("Finalizar processo", () =>
        {
            if (MessageBox.Show($"Finalizar {exe} (PID {row.Pid})?", "Prumo", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes
                && !Engine.Kill(row.Pid))
                Toast("Não foi possível finalizar " + exe);
        }));
    }

    void AskAffinity(ProcessRow row)
    {
        var current = Engine.GetAffinity(row.Pid);
        var rule = _engine.Config.RuleFor(row.Exe);
        var dlg = new AffinityDialog(row.Exe, current ?? Core.Affinity.AllCpus, rule?.Affinity) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (dlg.Always)
            EditRule(row.Exe, r => r.Affinity = dlg.Mask == Core.Affinity.AllCpus ? null : Core.Affinity.Format(dlg.Mask));
        if (!_engine.SetAffinityNow(row.Pid, dlg.Mask)) Toast("Sem permissão para mudar a afinidade de " + row.Exe);
    }

    void EditRule(string exe, Action<ProcessRule> edit)
    {
        var r = _engine.Config.GetOrAddRule(exe);
        edit(r);
        if (r.IsEmpty) _engine.Config.Rules.Remove(r);
        _engine.ConfigChanged();
        LoadRules();
    }

    void Toast(string text) => _log.Add(EventKind.RuleFailed, 0, "prumo.exe", text);

    // ---------------------------------------------------------------- rules

    List<Option<string>> PlanChoices() =>
        new[] { new Option<string>("Desligado", null) }.Concat(PowerPlans.List().Select(p => new Option<string>(p.Name, p.Name))).ToList();

    void LoadRules()
    {
        var plans = PlanChoices();
        _rules.Clear();
        foreach (var r in _engine.Config.Rules.OrderBy(r => r.Exe))
            _rules.Add(new RuleRow(r, plans, RuleEdited));
        RulesEmpty.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_engine.Last is { } s) UpdateRuleRunning(s);
    }

    void RuleEdited() => _engine.ConfigChanged();

    void UpdateRuleRunning(EngineSnapshot s)
    {
        var matcher = new RuleMatcher(_rules.Select(r => r.Rule));
        var counts = new Dictionary<ProcessRule, int>();
        foreach (var p in s.Processes)
            if (matcher.Match(p.Exe, p.Path) is { } m) counts[m] = counts.GetValueOrDefault(m) + 1;
        foreach (var r in _rules)
        {
            int n = counts.GetValueOrDefault(r.Rule);
            r.Running = n == 0 ? "nada rodando agora" : n == 1 ? "1 processo rodando" : $"{n} processos rodando";
        }
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var exe = NewRuleExe.Text.Trim();
        if (exe.Length == 0) { NewRuleExe.Focus(); return; }
        exe = exe.Trim('"');
        // A folder means "every program inside it"; a bare name means an executable.
        if (Directory.Exists(exe)) exe = exe.TrimEnd('\\') + @"\*";
        else if (!exe.Contains('*') && !exe.Contains('\\') && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        _engine.Config.GetOrAddRule(exe);
        _engine.ConfigChanged();
        NewRuleExe.Text = "";
        LoadRules();
    }

    void NewRuleExe_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddRule_Click(sender, e);
        Dispatcher.BeginInvoke(() => NewRuleHint.Visibility = NewRuleExe.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed);
    }

    void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not RuleRow row) return;
        _engine.Config.Rules.Remove(row.Rule);
        _engine.ConfigChanged();
        LoadRules();
    }

    // ----------------------------------------------------------- ProBalance

    void LoadProBalance()
    {
        _loading = true;
        var pb = _engine.Config.ProBalance;
        PbEnabled.IsChecked = pb.Enabled;
        PbTotal.Text = pb.TotalCpuThreshold.ToString(CultureInfo.CurrentCulture);
        PbProc.Text = pb.ProcessCpuThreshold.ToString(CultureInfo.CurrentCulture);
        PbTime.Text = pb.TimeOverQuotaMs.ToString();
        PbRestore.Text = pb.ProcessCpuRestore.ToString(CultureInfo.CurrentCulture);
        PbMin.Text = pb.MinRestraintMs.ToString();
        PbMax.Text = pb.MaxRestraintMs.ToString();
        PbFg.IsChecked = pb.ExcludeForeground;
        PbFgChildren.IsChecked = pb.ExcludeChildrenOfForeground;
        PbServices.IsChecked = pb.ExcludeServices;
        PbOnlyNormal.IsChecked = pb.OnlyNormalPriority;
        PbIdle.IsChecked = pb.LowerToIdle;
        PbIo.IsChecked = pb.LowerIoPriority;
        _loading = false;
    }

    void PbChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var pb = _engine.Config.ProBalance;
        pb.Enabled = PbEnabled.IsChecked == true;
        pb.TotalCpuThreshold = Num(PbTotal, pb.TotalCpuThreshold, 0, 100);
        pb.ProcessCpuThreshold = Num(PbProc, pb.ProcessCpuThreshold, 0.1, 100);
        pb.TimeOverQuotaMs = (int)Num(PbTime, pb.TimeOverQuotaMs, 0, 600_000);
        pb.ProcessCpuRestore = Num(PbRestore, pb.ProcessCpuRestore, 0, 100);
        pb.MinRestraintMs = (int)Num(PbMin, pb.MinRestraintMs, 0, 3_600_000);
        pb.MaxRestraintMs = (int)Num(PbMax, pb.MaxRestraintMs, 0, 86_400_000);
        pb.ExcludeForeground = PbFg.IsChecked == true;
        pb.ExcludeChildrenOfForeground = PbFgChildren.IsChecked == true;
        pb.ExcludeServices = PbServices.IsChecked == true;
        pb.OnlyNormalPriority = PbOnlyNormal.IsChecked == true;
        pb.LowerToIdle = PbIdle.IsChecked == true;
        pb.LowerIoPriority = PbIo.IsChecked == true;
        _engine.ConfigChanged();
        LoadProBalance();
    }

    static double Num(TextBox box, double fallback, double min, double max)
    {
        var t = box.Text.Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;
    }

    // ---------------------------------------------------------- performance

    static readonly (string Label, int Value)[] TimerChoices =
        { ("Padrão do Windows", 0), ("1 ms", 10_000), ("0,5 ms", 5_000) };

    void LoadPerformance()
    {
        _loading = true;
        var c = _engine.Config;
        PerfMode.IsChecked = c.PerformanceMode;
        var plans = PowerPlans.List().Select(p => new Option<string>(p.Name, p.Name)).ToList();
        PerfPlan.ItemsSource = plans;
        PerfPlan.SelectedItem = plans.FirstOrDefault(p => p.Value == c.PerformancePlan)
            ?? plans.FirstOrDefault(p => p.Value!.Contains("Highest", StringComparison.OrdinalIgnoreCase) || p.Value!.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
            ?? plans.FirstOrDefault();

        FgBoost.IsChecked = c.ForegroundBoost.Enabled;
        var prios = new[] { CpuPriority.AboveNormal, CpuPriority.High }.Select(p => new Option<CpuPriority?>(Names.Of(p), p)).ToList();
        FgBoostPriority.ItemsSource = prios;
        FgBoostPriority.SelectedItem = prios.FirstOrDefault(p => p.Value == c.ForegroundBoost.Priority) ?? prios[0];
        FgBoostOnlyNormal.IsChecked = c.ForegroundBoost.OnlyNormal;

        var st = c.SmartTrim;
        TrimOn.IsChecked = st.TrimWorkingSets;
        TrimLoad.Text = st.MinRamLoadPercent.ToString(CultureInfo.CurrentCulture);
        TrimMinMb.Text = st.MinProcessMb.ToString();
        TrimInterval.Text = st.IntervalMinutes.ToString();
        PurgeOn.IsChecked = st.PurgeStandbyList;
        PurgeBelow.Text = st.PurgeBelowFreeMb.ToString();

        TimerRes.ItemsSource = TimerChoices.Select(t => t.Label).ToList();
        int idx = Array.FindIndex(TimerChoices, t => t.Value == c.TimerResolution100ns);
        TimerRes.SelectedIndex = idx < 0 ? 0 : idx;
        _loading = false;
    }

    void PerfPlan_Changed(object sender, SelectionChangedEventArgs e) => PerfChanged(sender, e);

    void PerfChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var c = _engine.Config;
        c.PerformanceMode = PerfMode.IsChecked == true;
        c.PerformancePlan = (PerfPlan.SelectedItem as Option<string>)?.Value;
        c.ForegroundBoost.Enabled = FgBoost.IsChecked == true;
        c.ForegroundBoost.Priority = (FgBoostPriority.SelectedItem as Option<CpuPriority?>)?.Value ?? CpuPriority.AboveNormal;
        c.ForegroundBoost.OnlyNormal = FgBoostOnlyNormal.IsChecked == true;
        var st = c.SmartTrim;
        st.TrimWorkingSets = TrimOn.IsChecked == true;
        st.MinRamLoadPercent = Num(TrimLoad, st.MinRamLoadPercent, 1, 100);
        st.MinProcessMb = (int)Num(TrimMinMb, st.MinProcessMb, 1, 65_536);
        st.IntervalMinutes = (int)Num(TrimInterval, st.IntervalMinutes, 1, 1440);
        st.PurgeStandbyList = PurgeOn.IsChecked == true;
        st.PurgeBelowFreeMb = (int)Num(PurgeBelow, st.PurgeBelowFreeMb, 64, 65_536);
        c.TimerResolution100ns = TimerRes.SelectedIndex is >= 0 and var i && i < TimerChoices.Length ? TimerChoices[i].Value : 0;
        _engine.ConfigChanged();
        LoadPerformance();
    }

    /// <summary>The settings a game folder rule gets: what Process Lasso users usually set for games.</summary>
    public static void ApplyGamePreset(ProcessRule r)
    {
        r.CpuPriority = CpuPriority.High;
        r.IoPriority = IoPriority.High;
        r.GpuPriority = GpuPriority.High;
        r.GpuPreference = GpuPreference.HighPerformance;
        r.Efficiency = EfficiencyMode.Off;
        r.ProBalanceExcluded = true;
        if (r.IsPattern) r.Except ??= ProcessRule.NotGames.ToList();
    }

    void AddGames_Click(object sender, RoutedEventArgs e)
    {
        var libs = GameLibraries.Find();
        var menu = new ContextMenu { PlacementTarget = AddGamesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (libs.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Nenhuma biblioteca de jogos encontrada", IsEnabled = false });
        else
        {
            var all = new MenuItem { Header = $"Todas as bibliotecas ({libs.Count})" };
            all.Click += (_, _) => AddGameRules(libs);
            menu.Items.Add(all);
            menu.Items.Add(new Separator());
            foreach (var lib in libs)
            {
                var exists = _engine.Config.RuleFor(lib.Path + @"\*") is not null;
                var item = new MenuItem { Header = $"{lib.Launcher}  ·  {lib.Path}", IsChecked = exists };
                item.Click += (_, _) => AddGameRules(new[] { lib });
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    void AddGameRules(IEnumerable<GameLibrary> libs)
    {
        int added = 0;
        foreach (var lib in libs)
        {
            var pattern = lib.Path + @"\*";
            if (_engine.Config.RuleFor(pattern) is not null) continue;
            ApplyGamePreset(_engine.Config.GetOrAddRule(pattern));
            added++;
        }
        _engine.ConfigChanged();
        LoadRules();
        _log.Add(EventKind.Info, 0, "prumo.exe", $"{added} regra(s) de biblioteca de jogos criada(s)");
    }

    // ------------------------------------------------------------------ log

    void OnLogAdded(LogEntry e)
    {
        if (e.Kind == EventKind.Restrained) _restraintsToday++;
        AddLogRow(e);
        while (_logRows.Count > 2000) _logRows.RemoveAt(_logRows.Count - 1);
    }

    void AddLogRow(LogEntry e)
    {
        if (!LogMatches(e)) return;
        _logRows.Insert(0, new LogRow(e));
        LogCount.Text = $"{_logRows.Count} eventos";
    }

    bool LogMatches(LogEntry e) => (LogFilter.SelectedIndex) switch
    {
        1 => e.Kind is EventKind.Restrained or EventKind.Restored,
        2 => e.Kind is EventKind.RuleApplied or EventKind.RuleFailed,
        3 => e.Kind == EventKind.PowerPlan,
        4 => e.Kind == EventKind.RuleFailed,
        _ => true,
    };

    void LogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList is null) return;
        _logRows.Clear();
        foreach (var entry in _log.Snapshot()) AddLogRow(entry);
        LogCount.Text = $"{_logRows.Count} eventos";
    }

    void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        _logRows.Clear();
        LogCount.Text = "0 eventos";
    }

    void OpenLogFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", Path.Combine(AppConfig.Dir, "logs"));

    // ------------------------------------------------------------- settings

    void LoadSettings()
    {
        _loading = true;
        SetAutostart.IsChecked = Autostart.IsEnabled();
        SetCloseToTray.IsChecked = _engine.Config.CloseToTray;
        var plans = PowerPlans.List();
        SetPlanNow.Text = "Ativo agora: " + (PowerPlans.ActiveName() ?? "—");
        var choices = new[] { new Option<string>("O plano que estava ativo antes", null) }
            .Concat(plans.Select(p => new Option<string>(p.Name, p.Name))).ToList();
        SetDefaultPlan.ItemsSource = choices;
        SetDefaultPlan.SelectedItem = choices.FirstOrDefault(c => c.Value == _engine.Config.DefaultPowerPlan) ?? choices[0];
        LassoState.Text = Process.GetProcessesByName("ProcessGovernor").Length > 0
            ? "O Process Lasso ainda está rodando: os dois vão brigar pelas mesmas prioridades. Feche-o ou desative a inicialização dele."
            : File.Exists(ProcessLassoImport.IniPath) ? "prolasso.ini encontrado." : "prolasso.ini não encontrado.";
        _loading = false;
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool want = SetAutostart.IsChecked == true;
        var err = want ? Autostart.Enable() : Autostart.Disable();
        if (err is not null) MessageBox.Show(err, "Prumo", MessageBoxButton.OK, MessageBoxImage.Warning);
        LoadSettings();
    }

    void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        _engine.Config.CloseToTray = SetCloseToTray.IsChecked == true;
        _engine.ConfigChanged();
    }

    void DefaultPlan_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SetDefaultPlan.SelectedItem is not Option<string> o) return;
        _engine.Config.DefaultPowerPlan = o.Value;
        _engine.ConfigChanged();
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var imported = ProcessLassoImport.TryImport();
        if (imported is null) { MessageBox.Show("Não encontrei o prolasso.ini.", "Prumo"); return; }
        if (MessageBox.Show($"Importar {imported.Rules.Count} regras e as opções do ProBalance? A configuração atual será substituída.",
                "Prumo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var cfg = _engine.Config;
        cfg.Rules = imported.Rules;
        cfg.ProBalance = imported.ProBalance;
        cfg.ImportedFromProcessLasso = true;
        _engine.ConfigChanged();
        LoadRules();
        LoadProBalance();
        _log.Add(EventKind.Info, 0, "prumo.exe", $"Importado do Process Lasso: {cfg.Rules.Count} regras");
    }
}
