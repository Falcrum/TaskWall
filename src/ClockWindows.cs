using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskWall;

static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];

    /// <summary>A solid brush of a colour, frozen and shared (thousands of rows ask for the same few colours). Never animate one.</summary>
    public static SolidColorBrush Brush(Color c)
    {
        lock (Brushes)
        {
            if (Brushes.TryGetValue(c, out var b)) return b;
            b = new SolidColorBrush(c);
            b.Freeze();
            if (Brushes.Count > 4000) Brushes.Clear(); // a theme or accent change makes new colours: keep it bounded
            return Brushes[c] = b;
        }
    }

    static readonly System.Collections.Generic.Dictionary<Color, SolidColorBrush> Brushes = new();

    /// <summary>"#RRGGBB" → a colour, parsed once per text.</summary>
    public static Color Parse(string hex, Color fallback)
    {
        lock (Colours)
        {
            if (Colours.TryGetValue(hex, out var c)) return c;
            try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = fallback; }
            return Colours[hex] = c;
        }
    }

    static readonly System.Collections.Generic.Dictionary<string, Color> Colours = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The top bar, hanging from the top edge of the screen as wide as the board: the weather now on the left, the time
/// and date in one line in the middle (a click opens the month calendar under them), the computer's load on the right.
/// </summary>
public sealed class ClockWindow : GlassWindow
{
    public const double BarHeight = 40;

    readonly TextBlock _time = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _date = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 2, 0, 0) };
    readonly TextBlock _chevron = new() { Text = "▾", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 2, 0, 0) };
    readonly StackPanel _weather = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel _meters = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    readonly Dictionary<string, Meter> _rings = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly CalendarWindow _calendar = new();
    string _shown = "", _meterSet = "";
    bool _narrow;

    public double Scale { get; private set; } = 1;
    public CalendarWindow Calendar => _calendar;

    public ClockWindow()
    {
        SquareTopCorners = true;
        Radius = 14;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        Frame.VerticalAlignment = VerticalAlignment.Top;
        _calendar.KeepAbove = this;
        _date.Foreground = Ui.Res("FgDim");
        _chevron.Foreground = Ui.Res("FgFaint");

        // the middle: "19:43  PONIEDZIAŁEK 26 PAŹDZIERNIK ▾" – a click opens the month under it
        var clock = new StackPanel { Orientation = Orientation.Horizontal };
        clock.Children.Add(_time);
        clock.Children.Add(_date);
        clock.Children.Add(_chevron);
        var middle = new Border { Child = clock, Padding = new Thickness(12, 0, 10, 0), Margin = new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand, ToolTip = L.T("Kalendarz") };
        Anim.HoverBackground(middle, Colors.Transparent, Th.Ink(0x18));
        // a click on the clock while the calendar is open: pressing the mouse already closed it (it lost the focus) – don't open it again
        middle.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!_calendar.JustClosed) _calendar.Toggle(); };
        Grid.SetColumn(middle, 1);

        var grid = new Grid { Height = BarHeight, Margin = new Thickness(16, 0, 14, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(_weather);
        grid.Children.Add(middle);
        Grid.SetColumn(_meters, 2);
        grid.Children.Add(_meters);
        grid.MouseRightButtonUp += (_, e) => { e.Handled = true; App.Instance.ShowSettings(); };
        grid.SizeChanged += (_, e) => { bool narrow = e.NewSize.Width < 820; if (narrow != _narrow) { _narrow = narrow; UpdateWeather(); BuildMeters(true); } };
        // Poświata: the board's light from its top goes on up into the bar (from its bottom edge), so the two read as one
        _glow.Child = grid;
        Body = _glow;

        _timer.Tick += (_, _) => UpdateText();
        _timer.Start();
        SysMeters.Updated += r => Dispatcher.BeginInvoke(() => ShowMeters(r));
        WeatherService.Changed += UpdateWeather;
        IsVisibleChanged += (_, _) => BuildMeters(false);
        ApplyGlow();
        UpdateText();
        UpdateWeather();
    }

    public void SetScale(double scale)
    {
        Scale = scale;
        ApplyScale(scale);
        Frame.Height = BarHeight * scale;
        _calendar.SetScale(scale);
        UpdateWeather();
        BuildMeters(true);
    }

    public void UpdateText()
    {
        var now = DateTime.Now;
        var time = App.Settings.Use24h ? now.ToString("HH:mm") : now.ToString("h:mm", CultureInfo.InvariantCulture);
        // PONIEDZIAŁEK 26 PAŹDZIERNIK / MONDAY 26 OCTOBER (month in the nominative, like a wall calendar)
        var date = L.LongDay(now);
        if (time + date == _shown) return;
        bool newMinute = _shown.Length > 0;
        _shown = time + date;
        _time.Text = time;
        _date.Text = App.Settings.Use24h ? date : $"{date} · {now.ToString("tt", CultureInfo.InvariantCulture)}";
        if (newMinute && now.Minute % 10 == 0) UpdateWeather(); // the "now" reading gets old
    }

    public void Open() => _calendar.Toggle(true);

    // ---------- weather now ----------

    void UpdateWeather()
    {
        _weather.Children.Clear();
        if (!App.Settings.BarWeather || WeatherService.Current is not { } w) return;
        var (symbol, desc) = WeatherService.Describe(w.Code);
        if (!w.Day && w.Code <= 2) symbol = "☾"; // clear or a few clouds at night
        _weather.Children.Add(new TextBlock { Text = symbol, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 17, Foreground = Ui.Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _weather.Children.Add(new TextBlock { Text = $"{Math.Round(w.Temp):0}°", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var line = new TextBlock { FontSize = 11, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        line.Inlines.Add(new Run(desc));
        if (!_narrow)
        {
            if (w.Feels is { } f && Math.Abs(f - w.Temp) >= 1) line.Inlines.Add(new Run("  ·  " + L.F("odczuwalna {0}°", Math.Round(f))) { Foreground = Ui.Res("FgFaint") });
            var today = WeatherService.On(DateTime.Today);
            // like the day headers: today's max / min, the chance of rain and how much, the wind now
            if (today != null)
                line.Inlines.Add(new Run($"  ·  {Math.Round(today.Max):0}°/{Math.Round(today.Min):0}°"
                    + (today.Rain is { } r && r > 0 ? $"  ☂ {r}%" + (today.Mm is >= 0.1 and var mm ? " · " + mm.ToString(mm < 10 ? "0.#" : "0", L.Culture) + " mm" : "") : "")) { Foreground = Ui.Res("FgFaint") });
            if (w.Wind is { } wn)
            {
                line.Inlines.Add(new Run("  ·  ") { Foreground = Ui.Res("FgFaint") });
                line.Inlines.Add(new InlineUIContainer(Icons.Wind(Ui.Res("FgFaint"), 12)) { BaselineAlignment = BaselineAlignment.Center });
                line.Inlines.Add(new Run($" {Math.Round(wn):0} km/h") { Foreground = Ui.Res("FgFaint") });
            }
        }
        _weather.Children.Add(line);
        var day = WeatherService.On(DateTime.Today);
        _weather.ToolTip = $"{App.Settings.Weather.City}\n{desc}, {Math.Round(w.Temp):0}°"
            + (w.Feels is { } ff ? "\n" + L.F("odczuwalna {0}°", Math.Round(ff)) : "")
            + (w.Wind is { } wi ? "\n" + L.F("wiatr {0} km/h", Math.Round(wi)) : "")
            + (day?.Rain is { } dr ? "\n" + L.F("szansa opadów {0}%", dr) : "")
            + (day?.Mm is { } dmm ? "\n" + L.F("opady: {0} mm", dmm.ToString("0.#", L.Culture)) : "")
            + (w.Humidity is { } h ? "\n" + L.F("wilgotność {0}%", h) : "")
            + "\n" + L.F("pomiar z {0}", w.At.ToString("HH:mm"));
    }

    // ---------- the computer's load ----------

    /// <summary>The rings chosen in Settings; reading runs only while the bar is on screen.</summary>
    void BuildMeters(bool force)
    {
        var kinds = App.Settings.Meters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(SysMeters.Kinds.Contains).Distinct().ToList();
        var set = string.Join(",", kinds) + _narrow;
        if (force || set != _meterSet)
        {
            _meterSet = set;
            _meters.Children.Clear();
            _rings.Clear();
            foreach (var k in kinds)
            {
                var m = new Meter(k, !_narrow);
                _rings[k] = m;
                _meters.Children.Add(m.Root);
            }
        }
        if (IsVisible && kinds.Count > 0) SysMeters.Start(); else SysMeters.Stop();
    }

    readonly Border _glow = new();

    void ApplyGlow() => _glow.Background = Glow.Enabled ? Glow.Bottom() : null;

    public void ApplySettings() { ApplyGlow(); BuildMeters(false); UpdateWeather(); _shown = ""; UpdateText(); }

    void ShowMeters(Dictionary<string, MeterValue> values)
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
        const double Size = 22, Thick = 2.3;

        public readonly FrameworkElement Root;
        readonly System.Windows.Shapes.Path _arc;
        readonly TextBlock _text;
        double _shown = -1;

        public Meter(string kind, bool label)
        {
            var (color, name) = Kinds[kind];
            var ring = new Grid { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center };
            ring.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Ui.Brush(Th.Ink(0x2A)), StrokeThickness = Thick });
            _arc = new System.Windows.Shapes.Path { Stroke = Ui.Brush(color), StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            ring.Children.Add(_arc);
            var icon = Icons.Meter(kind, Ui.Res("Fg"), 10); // a quiet white symbol: only the ring is in colour
            icon.Opacity = 0.75;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            ring.Children.Add(icon);
            _text = new TextBlock { Text = "–", FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Width = 26, Foreground = Ui.Res("FgDim") };
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
            _arc.Data = Arc(p);
        }

        /// <summary>The filled part of the ring, clockwise from the top.</summary>
        static Geometry Arc(double percent)
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

/// <summary>The month calendar that opens under the top bar's date (Todowall-style), on its own glass.</summary>
public sealed class CalendarWindow : GlassWindow
{
    public const double TabWidth = 250, CalendarHeight = 330; // the window; the glass inside grows to the month shown

    readonly Grid _calendar = new() { Margin = new Thickness(14, 12, 14, 12) };
    DateTime _month;
    bool _open;
    double _calHeight = CalendarHeight; // measured: 4–6 week rows, no empty space under "DZIŚ"

    public double Scale { get; private set; } = 1;
    public bool IsOpen => _open;

    public CalendarWindow()
    {
        Radius = 14;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        Frame.VerticalAlignment = VerticalAlignment.Top;
        Frame.Height = 0;
        Body = new StackPanel { Width = TabWidth, Children = { _calendar } };
        Deactivated += (_, _) => Toggle(false);
    }

    public void SetScale(double scale)
    {
        Scale = scale;
        ApplyScale(scale);
        Frame.BeginAnimation(HeightProperty, null);
        Frame.Height = _open ? _calHeight * scale : 0;
    }

    public void Toggle() => Toggle(!_open);

    DateTime _closedAt;

    /// <summary>Closed a moment ago (the click that closed it is still on its way).</summary>
    public bool JustClosed => (DateTime.Now - _closedAt).TotalMilliseconds < 400;

    public void Toggle(bool open)
    {
        if (_open == open) return;
        _open = open;
        if (open)
        {
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            FillCalendar();
            if (!IsVisible) Show();
            Activate();
            Anim.Enter(_calendar, 0, -10, 300, 60);
            Anim.Height(Frame, _calHeight * Scale, 320);
        }
        else
        {
            _closedAt = DateTime.Now;
            Anim.Height(Frame, 0, 220);
        }
    }

    void FillCalendar()
    {
        _calendar.Children.Clear();
        _calendar.RowDefinitions.Clear();
        _calendar.ColumnDefinitions.Clear();
        var weekendRed = new SolidColorBrush(Color.FromRgb(0xF2, 0x6D, 0x6D));

        // header: ‹  PAŹDZIERNIK 2026  ›
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        var prev = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = "", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 9 };
        var next = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = "", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 9 };
        prev.Click += (_, _) => { _month = _month.AddMonths(-1); FillCalendar(); };
        next.Click += (_, _) => { _month = _month.AddMonths(1); FillCalendar(); };
        head.Children.Add(prev);
        head.Children.Add(new TextBlock
        {
            Text = _month.ToString("MMMM yyyy", BoardWindow.Pl).ToUpper(BoardWindow.Pl),
            FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(next);

        // 8 columns (week no. + 7 days) × 7 rows (names + 6 weeks); the weeks shown on the board get one rounded frame
        var grid = new Grid();
        for (int c = 0; c < 8; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        var start = BoardWindow.Monday(_month);
        int weeks = (int)Math.Ceiling(((_month.AddMonths(1) - start).TotalDays) / 7); // 4–6 rows, no empty row
        for (int r = 0; r < weeks; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        void Put(UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }

        Put(new TextBlock { Text = L.En ? "wk" : "tc", FontSize = 9, Foreground = Ui.Res("FgFaint"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Tydzień roku") }, 0, 0);
        string[] names = L.En ? new[] { "M", "T", "W", "T", "F", "S", "S" } : new[] { "P", "W", "Ś", "C", "P", "S", "N" };
        for (int i = 0; i < 7; i++)
            Put(new TextBlock { Text = names[i], FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = i >= 5 ? weekendRed : Ui.Res("FgFaint"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 0, i + 1);

        var accent = ((SolidColorBrush)Ui.Res("AccentBrush")).Color;
        bool showRange = App.Board?.View is not (ViewMode.Year or ViewMode.Timeline);
        var shownFrom = App.Board?.FirstMonday ?? BoardWindow.Monday(DateTime.Today);
        var shownTo = shownFrom.AddDays(7 * (App.Board?.WeekCount ?? 2) - 1);
        int r0 = -1, r1 = -1;
        for (int w = 0; w < weeks; w++)
        {
            var monday = start.AddDays(w * 7);
            bool inRange = showRange && monday >= shownFrom && monday <= shownTo;
            if (inRange) { if (r0 < 0) r0 = w; r1 = w; }
            Put(new TextBlock
            {
                Text = BoardWindow.WeekNo(monday).ToString(), FontSize = 9.5,
                Foreground = inRange ? new SolidColorBrush(accent) : Ui.Res("FgFaint"), FontWeight = inRange ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }, w + 1, 0);
            for (int i = 0; i < 7; i++)
            {
                var d = monday.AddDays(i);
                bool inMonth = d.Month == _month.Month;
                bool today = d == DateTime.Today;
                bool red = i >= 5 || DaysOff.Name(d) != null;
                var label = new TextBlock
                {
                    Text = d.Day.ToString(),
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = today ? Brushes.White : red ? weekendRed : Ui.Res("Fg"),
                    Opacity = inMonth ? 1 : 0.35,
                };
                var cell = new Border
                {
                    Width = 25, Height = 25,
                    CornerRadius = new CornerRadius(12.5),
                    Cursor = Cursors.Hand,
                    Background = today ? Ui.Res("AccentBrush") : Brushes.Transparent,
                    Child = label,
                    ToolTip = DaysOff.Name(d),
                };
                if (!today) Anim.HoverBackground(cell, Colors.Transparent, Th.Ink(0x30));
                cell.MouseLeftButtonUp += (_, _) => Pick(d);
                Put(cell, w + 1, i + 1);
            }
        }
        if (r0 >= 0)
        {
            var frame = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.3),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, accent.R, accent.G, accent.B)),
                Background = new SolidColorBrush(Color.FromArgb(0x16, accent.R, accent.G, accent.B)),
                Margin = new Thickness(-1, 1, -1, 1),
                IsHitTestVisible = false,
                ToolTip = L.T("Okres widoczny na tablicy"),
            };
            Grid.SetRow(frame, r0 + 1);
            Grid.SetRowSpan(frame, r1 - r0 + 1);
            Grid.SetColumnSpan(frame, 8);
            grid.Children.Insert(0, frame); // behind the numbers
        }

        var footer = new Button { Style = (Style)Application.Current.Resources["LinkButton"], Content = L.T("DZIŚ"), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        footer.Click += (_, _) => Pick(DateTime.Today);

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(head);
        dock.Children.Add(footer);
        dock.Children.Add(grid);
        _calendar.Children.Add(dock);
        _calendar.Measure(new Size(TabWidth, double.PositiveInfinity));
        var h = _calendar.DesiredSize.Height + _calendar.Margin.Top + _calendar.Margin.Bottom;
        if (h > 0 && Math.Abs(h - _calHeight) > 0.5)
        {
            _calHeight = h;
            if (_open && Frame.Height > 0) Anim.Height(Frame, _calHeight * Scale, 200); // a month with more / fewer weeks
        }
    }

    void Pick(DateTime d)
    {
        Toggle(false);
        App.Board?.JumpTo(d);
    }
}
