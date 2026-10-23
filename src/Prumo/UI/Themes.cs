using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Prumo.UI;

public enum AppTheme { Azul, Claro, Preto }

/// <summary>
/// Swaps the palette at run time. Every themed brush in the XAML is a DynamicResource, so writing new
/// brushes into the application resources repaints open windows without reopening them.
/// </summary>
public static class Themes
{
    sealed record Palette(bool Dark, string Bg, string Surface, string Surface2, string Border, string Text, string Muted,
        string Accent, string Accent2, string Warn, string Danger, string Violet,
        string Hover, string AccentSoft, string WarnSoft, string Sidebar, string OnAccent, string ScrollThumb);

    static readonly Dictionary<AppTheme, Palette> Palettes = new()
    {
        [AppTheme.Azul] = new(true, "#0B1120", "#111A2E", "#17223A", "#22304D", "#E6EDF7", "#8A9BB8",
            "#2DD4BF", "#22D3EE", "#F59E0B", "#F43F5E", "#A78BFA",
            "#1D2A47", "#1A2DD4BF", "#22F59E0B", "#0A1020", "#06231F", "#33476B"),
        [AppTheme.Claro] = new(false, "#F3F5F9", "#FFFFFF", "#EDF1F6", "#DCE3ED", "#0F172A", "#5B6B85",
            "#0D9488", "#0891B2", "#D97706", "#E11D48", "#7C3AED",
            "#E5EBF3", "#1F0D9488", "#26D97706", "#E9EEF5", "#FFFFFF", "#C3CDDC"),
        [AppTheme.Preto] = new(true, "#000000", "#0A0A0A", "#141414", "#1F1F1F", "#EDEDED", "#8C8C8C",
            "#2DD4BF", "#22D3EE", "#F59E0B", "#F43F5E", "#A78BFA",
            "#1A1A1A", "#1A2DD4BF", "#22F59E0B", "#000000", "#06231F", "#2E2E2E"),
    };

    public static AppTheme Current { get; private set; } = AppTheme.Azul;
    public static bool IsDark => Palettes[Current].Dark;
    public static event Action? Changed;

    public static AppTheme Parse(string? name) => Enum.TryParse<AppTheme>(name, true, out var t) ? t : AppTheme.Azul;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var p = Palettes[theme];
        var res = Application.Current.Resources;

        void Set(string key, string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            res[key + "Color"] = c;
            res[key] = Frozen(new SolidColorBrush(c));
        }
        Set("Bg", p.Bg); Set("Surface", p.Surface); Set("Surface2", p.Surface2); Set("Border", p.Border);
        Set("Text", p.Text); Set("Accent", p.Accent); Set("Accent2", p.Accent2);
        Set("Warn", p.Warn); Set("Danger", p.Danger); Set("Violet", p.Violet);
        Set("Hover", p.Hover); Set("AccentSoft", p.AccentSoft); Set("WarnSoft", p.WarnSoft);
        Set("Sidebar", p.Sidebar); Set("OnAccent", p.OnAccent); Set("ScrollThumbBrush", p.ScrollThumb);
        var muted = (Color)ColorConverter.ConvertFromString(p.Muted);
        res["MutedColor"] = muted;
        res["MutedBrush"] = Frozen(new SolidColorBrush(muted));
        res["AccentGradient"] = Frozen(new LinearGradientBrush((Color)res["AccentColor"], (Color)res["Accent2Color"], 45));

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    public static Color Color(string key) => (Color)Application.Current.Resources[key + "Color"];
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Same colour as a System.Drawing colour, for the WinForms tray menu.</summary>
    public static System.Drawing.Color Gdi(string key)
    {
        var c = Color(key);
        return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>Dark or light window frame to match the theme, plus Windows 11 rounded corners.</summary>
    public static void ApplyTitleBar(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(h, 20, ref dark, 4);
        int round = 2;
        DwmSetWindowAttribute(h, 33, ref round, 4);
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
