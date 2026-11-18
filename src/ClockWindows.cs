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

    readonly ClockFace _clock = new();
    readonly WeatherBadge _weather = new();
    readonly LoadMeters _meters = new();
    readonly CalendarWindow _calendar = new();
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

        // the middle: "19:43  PONIEDZIAŁEK 26 PAŹDZIERNIK ▾" – a click opens the month under it
        // a click on the clock while the calendar is open: pressing the mouse already closed it (it lost the focus) – don't open it again
        _clock.Clicked += () => { if (!_calendar.JustClosed) _calendar.Toggle(); };
        _clock.TenMinutes += _weather.Update; // the "now" reading gets old
        Grid.SetColumn(_clock, 1);

        var grid = new Grid { Height = BarHeight, Margin = new Thickness(16, 0, 14, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(_weather);
        grid.Children.Add(_clock);
        Grid.SetColumn(_meters, 2);
        grid.Children.Add(_meters);
        grid.MouseRightButtonUp += (_, e) => { e.Handled = true; App.Instance.ShowSettings(); };
        grid.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 820;
            if (narrow == _narrow) return;
            _narrow = _weather.Narrow = _meters.Narrow = narrow;
            _weather.Update();
            _meters.Build(true, IsVisible);
        };
        // Poświata: the board's light from its top goes on up into the bar (from its bottom edge), so the two read as one
        _glow.Child = grid;
        Body = _glow;

        IsVisibleChanged += (_, _) => _meters.Build(false, IsVisible);
        ApplyGlow();
        _weather.Update();
    }

    public void SetScale(double scale)
    {
        Scale = scale;
        ApplyScale(scale);
        Frame.Height = BarHeight * scale;
        _calendar.SetScale(scale);
        _weather.Update();
        _meters.Build(true, IsVisible);
    }

    public void Open() => _calendar.Toggle(true);

    readonly Border _glow = new();

    public void ApplyGlow() => _glow.Background = Glow.Enabled ? Glow.Bottom() : null;

    public void ApplySettings() { ApplyGlow(); _meters.Build(false, IsVisible); _weather.Update(); _clock.Refresh(); }
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

    /// <summary>Grows upwards from the bottom of its window (the full-width bar at the bottom of the screen).</summary>
    public bool OpensUp { set => Frame.VerticalAlignment = value ? VerticalAlignment.Bottom : VerticalAlignment.Top; }

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
        bool showRange = App.Board?.View is not (ViewMode.Year or ViewMode.Timeline or ViewMode.Report);
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
