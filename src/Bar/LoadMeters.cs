using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The computer's load for a bar: the rings chosen in Settings (CPU, GPU, RAM, disk, network), each with its number
/// unless <see cref="Narrow"/>. Reading runs only while a bar shows them (<see cref="Build"/> with <c>active</c>).
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
        var set = string.Join(",", kinds) + Narrow + TextSize + App.Settings.MeterColors;
        if (force || set != _set)
        {
            _set = set;
            Children.Clear();
            _rings.Clear();
            foreach (var k in kinds)
            {
                var m = TextSize > 0 ? new Meter(k, !Narrow, BarType.Ring(TextSize), BarType.Secondary(TextSize)) : new Meter(k, !Narrow, 22, 10.5);
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

    /// <summary>A ring that fills with the load, the symbol of what it measures inside, the number next to it.</summary>
    sealed class Meter
    {
        static readonly Dictionary<string, (Color Color, string Name)> Kinds = new()
        {
            ["cpu"] = (Color.FromRgb(0xF2, 0x6B, 0x4A), "CPU"),
            ["gpu"] = (Color.FromRgb(0x3F, 0xD6, 0x8C), "GPU"),
            ["ram"] = (Color.FromRgb(0xF2, 0xD1, 0x3D), "RAM"),
            ["disk"] = (Color.FromRgb(0x5B, 0x9B, 0xFF), L.T("Dysk")),
            ["net"] = (Color.FromRgb(0xB3, 0x82, 0xFF), L.T("Sieć")),
        };
        readonly double Size, Thick;
        readonly Color _color;

        public readonly FrameworkElement Root;
        readonly System.Windows.Shapes.Path _arc;
        readonly TextBlock _text;
        double _shown = -1;

        public Meter(string kind, bool label, double size, double font)
        {
            var (color, name) = Kinds[kind];
            Size = size;
            Thick = Math.Round(size * 0.105, 1);
            _color = color;
            var ring = new Grid { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center };
            ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Ui.Brush(Th.Ink(0x2A)), StrokeThickness = Thick });
            _arc = new System.Windows.Shapes.Path { Stroke = Ui.Brush(Calm(0)), StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            ring.Children.Add(_arc);
            var icon = Icons.Meter(kind, Ui.Res("Fg"), Math.Round(size * 0.45)); // a quiet white symbol: only the ring is in colour
            icon.Opacity = 0.75;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            ring.Children.Add(icon);
            _text = new TextBlock { Text = "–", FontSize = font, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), MinWidth = font * 2.5, Foreground = Ui.Res("FgDim") };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, ToolTip = name };
            row.Children.Add(ring);
            if (label) row.Children.Add(_text);
            else row.Margin = new Thickness(5, 0, 0, 0);
            Root = row;
        }

        public void Set(MeterValue v)
        {
            Root.ToolTip = v.Detail;
            double p = Math.Clamp(v.Percent, 0, 100);
            _text.Text = $"{Math.Round(p):0}%";
            if (Math.Abs(p - _shown) < 0.5) return;
            _shown = p;
            _arc.Stroke = Ui.Brush(Calm(p));
            _arc.Data = Arc(p);
        }

        /// <summary>
        /// The ring's colour: its own (CPU orange, GPU green …) always, or – Settings → Pasek → Kolory kółek: przy dużym obciążeniu,
        /// the default – a quiet light grey until the load reaches 80 %, so five rings don't make the bar loud.
        /// </summary>
        Color Calm(double percent) => App.Settings.MeterColors == "always" || percent >= 80 ? _color : Th.Ink(0x9A);

        /// <summary>The filled part of the ring, clockwise from the top.</summary>
        Geometry Arc(double percent)
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
