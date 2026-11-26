using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The computer's load for a bar: the rings chosen in Settings (CPU, GPU, RAM, disk, network), each with its name inside and its
/// number in the tip. Reading runs only while a bar shows them (<see cref="Build"/> with <c>active</c>).
/// </summary>
public sealed class LoadMeters : StackPanel
{
    readonly Dictionary<string, Meter> _rings = new();
    string _set = "";

    public LoadMeters()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        SysMeters.Updated += OnReading;
    }

    void OnReading(Dictionary<string, MeterValue> r) => Dispatcher.BeginInvoke(() => Show(r));


    /// <summary>The bar closed: off the readings (and the reading stops when no bar shows rings).</summary>
    public void Detach()
    {
        SysMeters.Updated -= OnReading;
        Build(false, false);
    }

    /// <summary>The bar's main text size: the rings and their numbers follow it (0: the old top bar's fixed sizes).</summary>
    public double TextSize { get; set; }

    /// <summary>The rings without their numbers.</summary>
    public bool Narrow { get; set; }

    /// <summary>The rings of the settings (again when <paramref name="force"/>); reading on while <paramref name="active"/>.</summary>
    public void Build(bool force, bool active)
    {
        var kinds = App.Settings.Meters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(SysMeters.Kinds.Contains).Distinct().ToList();
        var set = string.Join(",", kinds) + TextSize + App.Settings.MeterColors;
        if (force || set != _set)
        {
            _set = set;
            Children.Clear();
            _rings.Clear();
            foreach (var k in kinds)
            {
                // the name inside the ring (a little bigger than a bare ring so three letters fit); the number only in the tip
                var m = new Meter(k, TextSize > 0 ? Math.Round(BarType.Ring(TextSize) * 1.3) : 28);
                _rings[k] = m;
                Children.Add(m.Root);
            }
        }
        // the reading runs while any bar on screen shows rings (the old top bar and the new one may both exist)
        if (active && kinds.Count > 0) Showing.Add(this); else Showing.Remove(this);
        if (Showing.Count > 0) SysMeters.Start(); else SysMeters.Stop();
    }

    static readonly HashSet<LoadMeters> Showing = new();

    void Show(Dictionary<string, MeterValue> values)
    {
        foreach (var (k, ring) in _rings)
            if (values.TryGetValue(k, out var v)) ring.Set(v);
    }

    /// <summary>
    /// A ring that fills with the load, its name in letters inside ("CPU" – the small symbols were hard to tell apart from afar);
    /// the number in the tip.
    /// </summary>
    sealed class Meter
    {
        static readonly Dictionary<string, (Color Color, string Short, string Name)> Kinds = new()
        {
            ["cpu"] = (Color.FromRgb(0xF2, 0x6B, 0x4A), "CPU", "CPU"),
            ["gpu"] = (Color.FromRgb(0x3F, 0xD6, 0x8C), "GPU", "GPU"),
            ["ram"] = (Color.FromRgb(0xF2, 0xD1, 0x3D), "RAM", "RAM"),
            ["disk"] = (Color.FromRgb(0x5B, 0x9B, 0xFF), "SSD", L.T("Dysk")),
            ["net"] = (Color.FromRgb(0xB3, 0x82, 0xFF), "NET", L.T("Sieć")),
        };
        readonly double Size, Thick;
        readonly Color _color;
        readonly string _name;

        public readonly FrameworkElement Root;
        readonly System.Windows.Shapes.Path _arc;
        double _shown = -1;

        public Meter(string kind, double size)
        {
            var (color, shortName, name) = Kinds[kind];
            Size = size;
            Thick = Math.Round(size * 0.105, 1);
            _color = color;
            var ring = new Grid { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ToolTip = name, Margin = new Thickness(8, 0, 0, 0) };
            ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Ui.Brush(Th.Ink(0x2A)), StrokeThickness = Thick });
            _arc = new System.Windows.Shapes.Path { Stroke = Ui.Brush(Calm(0)), StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            ring.Children.Add(_arc);
            // the name inside ("CPU"): one size for all of them, laid out as a whole (not snapped letter by letter, which made "SSD" look smaller)
            ring.Children.Add(new TextBlock
            {
                Text = shortName, FontSize = Math.Round(Size * 0.25, 1), FontWeight = FontWeights.Bold, Foreground = Ui.Res("FgDim"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            ToolTipService.SetShowDuration(ring, 60000);
            Root = ring;
        }

        public void Set(MeterValue v)
        {
            // the number and the details in the tip ("CPU 9 %", the disk's free room …)
            Root.ToolTip = v.Detail;
            double p = Math.Clamp(v.Percent, 0, 100);
            if (Math.Abs(p - _shown) < 0.5) return;
            _shown = p;
            _arc.Stroke = Ui.Brush(Calm(p));
            _arc.Data = Arc(p, Size, Thick);
        }

        /// <summary>
        /// The ring's colour: its own (CPU orange, GPU green …) always, or – Settings → Pasek → Kolory kółek: stopniowo, the
        /// default – from a quiet grey to its own colour as the load grows (more of the colour with every percent from 15 % on,
        /// all of it at 90 %), so five calm rings don't make the bar loud and a busy one is seen.
        /// </summary>
        Color Calm(double percent)
        {
            if (App.Settings.MeterColors == "always") return _color;
            var grey = Th.Ink(0x9A);
            double t = Math.Clamp((percent - 15) / 75, 0, 1);
            t = t * t * (3 - 2 * t); // smooth at both ends
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
            return Color.FromArgb(Mix(grey.A, _color.A), Mix(grey.R, _color.R), Mix(grey.G, _color.G), Mix(grey.B, _color.B));
        }

        /// <summary>The filled part of a ring of this size, clockwise from the top.</summary>
        static Geometry Arc(double percent, double Size, double Thick)
        {
            double r = (Size - Thick) / 2, c = Size / 2;
            if (percent <= 0.5) return Geometry.Empty;
            if (percent >= 99.5) return new EllipseGeometry(new Point(c, c), r, r);
            double a = percent / 100 * 2 * Math.PI;
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c, c - r), false, false);
                ctx.ArcTo(end, new Size(r, r), 0, percent > 50, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
