using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Prumo.Core;

namespace Prumo.UI;

/// <summary>A disk cleanup category on the Limpeza page.</summary>
public sealed class DiskRow : INotifyPropertyChanged
{
    public required CleanTarget Target { get; init; }
    public string Title => Target.Title;
    public string Description => Target.Description;
    public bool Selected { get; set; }
    long? _size;
    public long? Size { get => _size; set { _size = value; PropertyChanged?.Invoke(this, new(nameof(SizeText))); } }
    public string SizeText => _size is { } s ? Format.Bytes((ulong)s) : "—";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    List<DiskRow>? _diskRows;
    bool _diskBusy;

    void LoadCleanup()
    {
        _loading = true;
        var m = _engine.Config.MemoryClean;
        McWorkingSets.IsChecked = m.WorkingSets;
        McFileCache.IsChecked = m.SystemFileCache;
        McModified.IsChecked = m.ModifiedList;
        McLowStandby.IsChecked = m.LowPriorityStandby;
        McWholeStandby.IsChecked = m.WholeStandby;
        McCombine.IsChecked = m.CombinePages;
        McRegistry.IsChecked = m.RegistryCache;
        McAuto.IsChecked = m.AutoAbovePercent > 0;
        McAutoPercent.Text = (m.AutoAbovePercent > 0 ? m.AutoAbovePercent : 90).ToString();
        McHotkey.IsChecked = m.Hotkey;
        _loading = false;
        UpdateMemStatus();

        if (_diskRows is null)
        {
            var chosen = _engine.Config.CleanupCategories;
            _diskRows = DiskCleaner.Targets.Select(t => new DiskRow
            {
                Target = t,
                Selected = chosen is null ? t.DefaultOn : chosen.Contains(t.Category.ToString()),
            }).ToList();
            DiskList.ItemsSource = _diskRows;
        }
        LoadStartup();
    }

    void UpdateMemStatus()
    {
        ulong avail = MemoryTools.AvailableBytes(), standby = MemoryTools.StandbyBytes();
        var sys = _engine.Last?.System;
        string total = sys is { MemoryTotalBytes: > 0 } ? $" de {Format.Bytes(sys.MemoryTotalBytes)}" : "";
        MemStatus.Text = $"Disponível agora: {Format.Bytes((ulong)avail)}{total} · lista de espera (cache): {Format.Bytes((ulong)standby)}";
    }

    void MemCleanChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var m = _engine.Config.MemoryClean;
        m.WorkingSets = McWorkingSets.IsChecked == true;
        m.SystemFileCache = McFileCache.IsChecked == true;
        m.ModifiedList = McModified.IsChecked == true;
        m.LowPriorityStandby = McLowStandby.IsChecked == true;
        m.WholeStandby = McWholeStandby.IsChecked == true;
        m.CombinePages = McCombine.IsChecked == true;
        m.RegistryCache = McRegistry.IsChecked == true;
        int pct = int.TryParse(McAutoPercent.Text, out var v) ? Math.Clamp(v, 50, 99) : 90;
        McAutoPercent.Text = pct.ToString();
        m.AutoAbovePercent = McAuto.IsChecked == true ? pct : 0;
        m.Hotkey = McHotkey.IsChecked == true;
        _engine.ConfigChanged();
    }

    async void CleanMemory_Click(object sender, RoutedEventArgs e)
    {
        CleanMemButton.IsEnabled = false;
        CleanMemButton.Content = "Limpando…";
        try
        {
            var r = await _engine.CleanMemoryAsync(_engine.Config.MemoryClean);
            MemResult.Text = $"Liberados {Format.Bytes((ulong)r.FreedBytes)} ({r.Trimmed} programas em segundo plano). Disponível: {Format.Bytes((ulong)r.AvailableBefore)} → {Format.Bytes((ulong)r.AvailableAfter)}"
                + (r.Failed.Count > 0 ? $". Não consegui limpar: {string.Join(", ", r.Failed)}." : ".");
            MemResult.Visibility = Visibility.Visible;
            UpdateMemStatus();
        }
        finally
        {
            CleanMemButton.Content = "Limpar memória agora";
            CleanMemButton.IsEnabled = true;
        }
    }

    // ---- disk ----

    void DiskSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_diskRows is null) return;
        _engine.Config.CleanupCategories = _diskRows.Where(r => r.Selected).Select(r => r.Target.Category.ToString()).ToList();
        _engine.ConfigChanged();
        CleanDiskButton.IsEnabled = !_diskBusy && _diskRows.Any(r => r.Selected && r.Size > 0);
    }

    async void ScanDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_diskRows is null || _diskBusy) return;
        SetDiskBusy(true, "Analisando…");
        try
        {
            foreach (var row in _diskRows)
            {
                var c = row.Target.Category;
                row.Size = await Task.Run(() => DiskCleaner.Measure(c));
            }
            long sel = _diskRows.Where(r => r.Selected).Sum(r => r.Size ?? 0);
            DiskStatus.Text = $"Dá para liberar {Format.Bytes((ulong)sel)} com o que está marcado";
        }
        finally { SetDiskBusy(false, null); }
    }

    async void CleanDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_diskRows is null || _diskBusy) return;
        var chosen = _diskRows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0) return;
        SetDiskBusy(true, "Limpando…");
        long freed = 0; int skipped = 0;
        try
        {
            foreach (var row in chosen)
            {
                var c = row.Target.Category;
                var (f, s) = await Task.Run(() => DiskCleaner.Clean(c));
                freed += f; skipped += s;
                row.Size = await Task.Run(() => DiskCleaner.Measure(c));
            }
            DiskStatus.Text = $"Liberados {Format.Bytes((ulong)freed)}" + (skipped > 0 ? $" ({skipped} arquivos em uso foram pulados)" : "");
            _log.Add(EventKind.Info, 0, "prumo.exe", "Limpeza de arquivos: " + DiskStatus.Text);
        }
        finally { SetDiskBusy(false, null); }
    }

    void SetDiskBusy(bool busy, string? text)
    {
        _diskBusy = busy;
        ScanButton.IsEnabled = !busy;
        if (text is not null) DiskStatus.Text = text;
        CleanDiskButton.IsEnabled = !busy && _diskRows!.Any(r => r.Selected && r.Size > 0);
    }

    // ---- startup ----

    void LoadStartup() => StartupList.ItemsSource = StartupManager.List();

    void RefreshStartup_Click(object sender, RoutedEventArgs e) => LoadStartup();

    void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupEntry entry } box) return;
        bool on = box.IsChecked == true;
        if (StartupManager.SetEnabled(entry, on))
            _log.Add(EventKind.Info, 0, "prumo.exe", $"Inicialização: {entry.Name} {(on ? "ligado" : "desligado")}");
        else
            MessageBox.Show($"Não consegui {(on ? "ligar" : "desligar")} {entry.Name}.", "Prumo", MessageBoxButton.OK, MessageBoxImage.Warning);
        LoadStartup();
    }

    // ---- network ----

    void FlushDns_Click(object sender, RoutedEventArgs e) =>
        DnsStatus.Text = DiskCleaner.FlushDns() ? $"Cache de DNS limpo às {DateTime.Now:HH:mm}" : "Não consegui limpar o cache de DNS";
}
