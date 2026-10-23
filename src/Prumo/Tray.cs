using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Prumo.Core;
using Forms = System.Windows.Forms;

namespace Prumo;

/// <summary>Tray icon that draws the CPU load as a bar, amber while ProBalance is restraining.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon = new();
    readonly Forms.ToolStripMenuItem _pbItem;
    readonly Engine _engine;
    readonly Action _open, _exit;
    Icon? _current;
    int _lastBar = -1;
    bool _lastRestraining;

    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    public Tray(Engine engine, Action open, Action exit)
    {
        _engine = engine; _open = open; _exit = exit;
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Renderer = new DarkRenderer() };
        menu.Items.Add("Abrir o Prumo", null, (_, _) => _open());
        _pbItem = new Forms.ToolStripMenuItem("ProBalance ligado", null, (_, _) => ToggleProBalance()) { Checked = engine.Config.ProBalance.Enabled };
        menu.Items.Add(_pbItem);
        var perfItem = new Forms.ToolStripMenuItem("Modo desempenho", null, (_, _) => TogglePerformance());
        menu.Items.Add(perfItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => _exit());
        menu.Opening += (_, _) =>
        {
            _pbItem.Checked = _engine.Config.ProBalance.Enabled;
            perfItem.Checked = _engine.Config.PerformanceMode;
            foreach (Forms.ToolStripItem i in menu.Items) i.ForeColor = UI.Themes.Gdi("Text");
        };
        foreach (Forms.ToolStripItem i in menu.Items) i.Padding = new Forms.Padding(4, 3, 4, 3);

        _icon.ContextMenuStrip = menu;
        _icon.Text = "Prumo";
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _open(); };
        Update(0, false);
        _icon.Visible = true;
    }

    void ToggleProBalance()
    {
        _engine.Config.ProBalance.Enabled = !_engine.Config.ProBalance.Enabled;
        _engine.ConfigChanged();
    }

    void TogglePerformance()
    {
        var c = _engine.Config;
        c.PerformanceMode = !c.PerformanceMode;
        c.PerformancePlan ??= PowerPlans.List().FirstOrDefault(p => p.Name.Contains("Highest", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("High performance", StringComparison.OrdinalIgnoreCase))?.Name;
        _engine.ConfigChanged();
    }

    public void Update(EngineSnapshot s)
    {
        Update(s.CpuTotal, s.RestrainedCount > 0);
        var text = $"Prumo · CPU {s.CpuTotal:0}%";
        if (s.RestrainedCount > 0) text += $" · restringindo {s.RestrainedCount}";
        if (s.PowerPlan is not null) text += $"\n{s.PowerPlan}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    void Update(double cpu, bool restraining)
    {
        int bar = (int)Math.Round(Math.Clamp(cpu, 0, 100) / 100 * 12);
        if (bar == _lastBar && restraining == _lastRestraining) return;
        _lastBar = bar; _lastRestraining = restraining;

        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = RoundRect(new RectangleF(0.5f, 0.5f, 15, 15), 3.5f);
            using var bgBrush = new SolidBrush(Color.FromArgb(0x0F, 0x17, 0x2A));
            g.FillPath(bgBrush, bg);
            using var track = new SolidBrush(Color.FromArgb(0x22, 0x30, 0x4D));
            g.FillRectangle(track, 4, 2, 8, 12);
            if (bar > 0)
            {
                var top = Color.FromArgb(restraining ? 0xF5 : 0x22, restraining ? 0x9E : 0xD3, restraining ? 0x0B : 0xEE);
                var bottom = Color.FromArgb(restraining ? 0xF4 : 0x2D, restraining ? 0x3F : 0xD4, restraining ? 0x5E : 0xBF);
                using var fill = new LinearGradientBrush(new Rectangle(4, 2, 8, 12), top, bottom, LinearGradientMode.Vertical);
                g.FillRectangle(fill, 4, 14 - bar, 8, bar);
            }
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        _icon.Icon = icon;
        _current?.Dispose();
        _current = icon;
    }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }

    sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            using var b = new SolidBrush(UI.Themes.Gdi("Accent"));
            e.Graphics.FillEllipse(b, e.ImageRectangle.X + 4, e.ImageRectangle.Y + 4, 8, 8);
        }
    }

    sealed class DarkColors : Forms.ProfessionalColorTable
    {
        static Color Bg => UI.Themes.Gdi("Surface2");
        static Color Hover => UI.Themes.Gdi("Hover");
        static Color Line => UI.Themes.Gdi("Border");
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
    }
}
