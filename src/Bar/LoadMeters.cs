using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The computer's load for a bar: the rings chosen in Settings (CPU, GPU, RAM, disk, network), each with its name and number
/// (only the name when <see cref="Narrow"/>), and – optional – Claude's and ChatGPT's usage limits as two rings in one (the
/// 5-hour window outside, the week inside, <see cref="AiLimits"/>). Reading runs only while a bar shows them (<see cref="Build"/>
/// with <c>active</c>).
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
        AiLimits.Changed += OnAi;
    }

    void OnReading(Dictionary<string, MeterValue> r) => Dispatcher.BeginInvoke(() => Show(r));
    void OnAi() => Dispatcher.BeginInvoke(() => { foreach (var (k, ring) in _rings) if (AiLimits.Now.TryGetValue(k, out var u)) ring.SetAi(u); });
    bool _aiOn;

    /// <summary>The bar closed: off the readings (and the reading stops when no bar shows rings).</summary>
    public void Detach()
    {
        SysMeters.Updated -= OnReading;
        AiLimits.Changed -= OnAi;
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
        var ai = App.Settings.AiMeters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => k is "claude" or "chatgpt").Distinct().ToList();
        var set = string.Join(",", kinds.Concat(ai)) + Narrow + TextSize + App.Settings.MeterColors;
        if (force || set != _set)
        {
            _set = set;
            Children.Clear();
            _rings.Clear();
            foreach (var k in kinds.Concat(ai))
            {
                var m = TextSize > 0 ? new Meter(k, !Narrow, BarType.Ring(TextSize), BarType.Secondary(TextSize)) : new Meter(k, !Narrow, 22, 10.5);
                _rings[k] = m;
                Children.Add(m.Root);
                if (AiLimits.Now.TryGetValue(k, out var known)) m.SetAi(known);
            }
        }
        // the AI limits are read while a bar shows their rings (every 3 minutes)
        bool aiOn = active && ai.Count > 0;
        if (aiOn != _aiOn) { _aiOn = aiOn; if (aiOn) AiLimits.Start(); else AiLimits.Stop(); }
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
    /// A ring that fills with the load and its name in letters next to it ("CPU 9%" – the symbols inside the small rings were
    /// hard to tell apart from afar); for the AI limits a second, thinner ring inside: the week.
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
            ["claude"] = (Color.FromRgb(0xD9, 0x77, 0x57), "CLAUDE", "Claude"),
            ["chatgpt"] = (Color.FromRgb(0x2E, 0xC4, 0x9C), "GPT", "ChatGPT"),
        };
        readonly double Size, Thick;
        readonly Color _color;
        readonly string _name;

        public readonly FrameworkElement Root;
        readonly System.Windows.Shapes.Path _arc, _inner;
        readonly TextBlock _text;
        double _shown = -1;

        public Meter(string kind, bool number, double size, double font)
        {
            var (color, shortName, name) = Kinds[kind];
            _name = name;
            Size = size;
            Thick = Math.Round(size * 0.105, 1);
            _color = color;
            bool ai = kind is "claude" or "chatgpt";
            var ring = new Grid { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center };
            ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Ui.Brush(Th.Ink(0x2A)), StrokeThickness = Thick });
            _arc = new System.Windows.Shapes.Path { Stroke = Ui.Brush(Calm(0)), StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            ring.Children.Add(_arc);
            // the week: a thinner ring inside the 5-hour one
            _inner = new System.Windows.Shapes.Path { Stroke = Ui.Brush(Calm(0)), StrokeThickness = Math.Max(1.2, Thick * 0.7), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            if (ai)
            {
                double gap = Thick * 2.1;
                ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Ui.Brush(Th.Ink(0x1E)), StrokeThickness = _inner.StrokeThickness, Margin = new Thickness(gap) });
                ring.Children.Add(_inner);
            }
            var label = new TextBlock { Text = shortName, FontSize = font * 0.78, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 1, 0, 0), Foreground = Ui.Res("FgFaint") };
            _text = new TextBlock { Text = "–", FontSize = font, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), MinWidth = font * 2.5, Foreground = Ui.Res("FgDim") };
            // "CPU ◯ 9%": the name before its ring (after it, it read as the next ring's name), a wider gap to the next meter
            label.Margin = new Thickness(0, 1, 4, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), Background = Brushes.Transparent, ToolTip = name };
            row.Children.Add(label);
            row.Children.Add(ring);
            if (number) row.Children.Add(_text);
            else row.Margin = new Thickness(12, 0, 0, 0);
            ToolTipService.SetShowDuration(row, 60000);
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
            _arc.Data = Arc(p, Size, Thick);
        }

        /// <summary>An AI limit: the 5-hour window on the ring (and its number), the week on the inner ring; the details in the tip.</summary>
        public void SetAi(AiLimits.Usage u)
        {
            Root.ToolTip = AiLimits.Describe(_name, u);
            double five = Math.Clamp(u.FiveHour ?? 0, 0, 100), week = Math.Clamp(u.Week ?? 0, 0, 100);
            _text.Text = u.FiveHour is { } f ? $"{Math.Round(f):0}%" : "–";
            _arc.Stroke = Ui.Brush(Calm(five));
            _arc.Data = Arc(five, Size, Thick);
            double gap = Thick * 2.1, inner = Size - 2 * gap;
            _inner.Stroke = Ui.Brush(Calm(week));
            var g = Arc(week, inner, _inner.StrokeThickness);
            if (!g.IsEmpty() && g is not EllipseGeometry) { g = g.Clone(); g.Transform = new TranslateTransform(gap, gap); }
            else if (g is EllipseGeometry e) g = new EllipseGeometry(new Point(Size / 2, Size / 2), e.RadiusX, e.RadiusY);
            _inner.Data = g;
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
