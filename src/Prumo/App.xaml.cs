using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Prumo.Core;
using Prumo.UI;

namespace Prumo;

public partial class App : Application
{
    const string MutexName = @"Local\Prumo.SingleInstance";
    const string ShowEventName = @"Local\Prumo.Show";
    const string ExitEventName = @"Local\Prumo.Exit";

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
