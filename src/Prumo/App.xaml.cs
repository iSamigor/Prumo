using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Prumo.Core;
using Prumo.UI;

namespace Prumo;

public partial class App : Application
{
    // A test copy (PRUMO_HOME set) gets its own names, so it never talks to the installed instance.
    static readonly string Suffix = Environment.GetEnvironmentVariable("PRUMO_HOME") is { Length: > 0 } h
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(h.ToLowerInvariant())))[..12]
        : "";
    static readonly string MutexName = @"Local\Prumo.SingleInstance" + Suffix;
    static readonly string ShowEventName = @"Local\Prumo.Show" + Suffix;
    static readonly string ExitEventName = @"Local\Prumo.Exit" + Suffix;

    Mutex? _mutex;
    Engine? _engine;
    Tray? _tray;
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool exitRequest = e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase);

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first || exitRequest)
        {
            // Already running: ask that instance to show its window (or to exit cleanly with --exit).
            try { EventWaitHandle.OpenExisting(exitRequest ? ExitEventName : ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        Listen(ShowEventName, ShowWindow);
        Listen(ExitEventName, ExitApp);

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "Prumo: erro", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var log = new ActivityLog();
        var config = AppConfig.Load();
        if (config.ImportedFromProcessLasso && !File.Exists(Path.Combine(AppConfig.Dir, ".welcomed")))
        {
            log.Add(EventKind.Info, 0, "prumo.exe", $"Configuração importada do Process Lasso: {config.Rules.Count} regras e ProBalance");
            File.WriteAllText(Path.Combine(AppConfig.Dir, ".welcomed"), "");
        }

        if (e.Args.Contains("--enable-game-boost", StringComparer.OrdinalIgnoreCase))
            EnableGameBoost(config, log);

        ISystemSampler? sampler = null;
        try { sampler = new SystemSampler(); } catch { }
        _engine = new Engine(config, log, sampler);
        _window = new MainWindow(_engine, log);
        _tray = new Tray(_engine, ShowWindow, ExitApp);
        _engine.Updated += s => Dispatcher.BeginInvoke(() => _tray?.Update(s));
        _engine.Start();

        // Used by the installer script: register the logon task from the installed copy.
        if (e.Args.Contains("--enable-autostart", StringComparer.OrdinalIgnoreCase) && Autostart.Enable() is { } err)
            log.Add(EventKind.RuleFailed, 0, "prumo.exe", err);

        bool toTray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!toTray) ShowWindow();
        else EnableDarkTitle(_window);
    }

    /// <summary>Turns on every setting that helps games and adds a rule per game library (installer option).</summary>
    static void EnableGameBoost(AppConfig config, ActivityLog log)
    {
        int added = 0;
        void Add(string folder)
        {
            var pattern = folder.TrimEnd('\\') + @"\*";
            if (config.RuleFor(pattern) is not null) return;
            UI.MainWindow.ApplyGamePreset(config.GetOrAddRule(pattern));
            added++;
        }
        foreach (var lib in GameLibraries.Find())
        {
            // The Riot folder also holds the Riot Client launcher: only the game folders get the game preset.
            if (lib.Launcher == "Riot")
            {
                foreach (var dir in Directory.EnumerateDirectories(lib.Path))
                    if (!Path.GetFileName(dir).Equals("Riot Client", StringComparison.OrdinalIgnoreCase)) Add(dir);
            }
            else Add(lib.Path);
        }
        // Games with their own rules keep them, plus the dedicated GPU and no EcoQoS throttling.
        foreach (var r in config.Rules.Where(r => r.CpuPriority == CpuPriority.High && !r.IsPattern))
        {
            r.GpuPreference ??= GpuPreference.HighPerformance;
            r.Efficiency ??= EfficiencyMode.Off;
        }

        config.PerformanceMode = true;
        config.PerformancePlan ??= PowerPlans.List().FirstOrDefault(p => p.Name.Contains("Highest", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase))?.Name;
        config.ForegroundBoost.Enabled = true;
        config.SmartTrim.TrimWorkingSets = true;
        config.SmartTrim.PurgeStandbyList = true;
        config.TimerResolution100ns = 5_000;
        config.Save();
        log.Add(EventKind.Info, 0, "prumo.exe", $"Otimizações para jogos ligadas ({added} regras de bibliotecas de jogos)");
    }

    void Listen(string name, Action action)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() => { while (ev.WaitOne()) Dispatcher.BeginInvoke(action); }) { IsBackground = true }.Start();
    }

    void ShowWindow()
    {
        if (_window is null) return;
        EnableDarkTitle(_window);
        _window.ShowFromTray();
    }

    void ExitApp()
    {
        if (_window is not null) { _window.AllowClose = true; _window.Close(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _engine?.Dispose();   // restores restrained processes and the power plan
        _mutex?.Dispose();
        base.OnExit(e);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    static void EnableDarkTitle(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int on = 1;
        DwmSetWindowAttribute(h, 20, ref on, 4);          // immersive dark mode (rounded corners/borders)
        int round = 2;
        DwmSetWindowAttribute(h, 33, ref round, 4);       // Windows 11 rounded corners
    }
}
