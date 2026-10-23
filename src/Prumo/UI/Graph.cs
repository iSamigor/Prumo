using System.Windows;
using System.Windows.Media;

namespace Prumo.UI;

public sealed class GraphSeries
{
    public string Name = "";
    public Color Color;
    public bool Fill;
    public readonly double[] Values;
    public GraphSeries(int capacity) => Values = new double[capacity];
}

/// <summary>Scrolling 0-100 % line graph with ProBalance markers, drawn directly in OnRender.</summary>
public sealed class Graph : FrameworkElement
{
    public const int Capacity = 120;

    public List<GraphSeries> Series { get; } = new();
    readonly int[] _markers = new int[Capacity];
    int _count, _head;

    static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x8A, 0x9B, 0xB8)), 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) });
    static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xF5, 0x9E, 0x0B)));

    public GraphSeries Add(string name, Color color, bool fill = false)
    {
        var s = new GraphSeries(Capacity) { Name = name, Color = color, Fill = fill };
        Series.Add(s);
        return s;
    }

    /// <summary>Appends one point per series (same order as <see cref="Series"/>), plus a marker count.</summary>
    public void Push(double[] values, int markers)
    {
        for (int i = 0; i < Series.Count && i < values.Length; i++) Series[i].Values[_head] = values[i];
        _markers[_head] = markers;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        foreach (var f in new[] { 0.25, 0.5, 0.75 })
            dc.DrawLine(GridPen, new Point(0, h * f), new Point(w, h * f));

        double step = w / (Capacity - 1);
        // Oldest point at the left, newest at the right edge.
        double X(int age) => w - age * step;
        int Idx(int age) => ((_head - 1 - age) % Capacity + Capacity) % Capacity;

        for (int age = 0; age < _count; age++)
            if (_markers[Idx(age)] > 0)
                dc.DrawRoundedRectangle(MarkerBrush, null, new Rect(X(age) - 1.5, h - 8, 3, 8), 1.5, 1.5);

        foreach (var s in Series)
        {
            if (_count < 2) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(_count - 1), Y(s.Values[Idx(_count - 1)], h)), false, false);
                for (int age = _count - 2; age >= 0; age--)
                    g.LineTo(new Point(X(age), Y(s.Values[Idx(age)], h)), true, true);
            }
            geo.Freeze();

            if (s.Fill)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(X(_count - 1), h), true, true);
                    for (int age = _count - 1; age >= 0; age--)
                        g.LineTo(new Point(X(age), Y(s.Values[Idx(age)], h)), false, true);
                    g.LineTo(new Point(X(0), h), false, false);
                }
                area.Freeze();
                var fill = new LinearGradientBrush(Color.FromArgb(0x55, s.Color.R, s.Color.G, s.Color.B),
                                                   Color.FromArgb(0x00, s.Color.R, s.Color.G, s.Color.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }

            var pen = new Pen(new SolidColorBrush(s.Color), s.Fill ? 2 : 1.5) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }

    static double Y(double v, double h) => h - Math.Clamp(v, 0, 100) / 100 * (h - 2) - 1;

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

/// <summary>Small vertical bar for one logical CPU.</summary>
public sealed class CoreBar : FrameworkElement
{
    double _value;
    public double Value { get => _value; set { _value = value; InvalidateVisual(); } }

    static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x22, 0x3A)));
    static readonly Brush Low = Freeze(new LinearGradientBrush(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x2D, 0xD4, 0xBF), 90));
    static readonly Brush High = Freeze(new LinearGradientBrush(Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xF4, 0x3F, 0x5E), 90));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 4, 4);
        double fh = Math.Clamp(_value, 0, 100) / 100 * h;
        if (fh > 0.5) dc.DrawRoundedRectangle(_value > 85 ? High : Low, null, new Rect(0, h - fh, w, fh), 4, 4);
    }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
