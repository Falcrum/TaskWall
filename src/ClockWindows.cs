using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskWall;

static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}

/// <summary>
/// Clock tab hanging from the top edge of the screen. Clicking the little dash grows the same
/// glass tab downwards into a month calendar (Todowall-style).
/// </summary>
public sealed class ClockWindow : GlassWindow
{
    public const double TabWidth = 230, HeaderHeight = 86, CalendarHeight = 290;

    readonly TextBlock _time = new() { FontSize = 34, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _date = new() { FontSize = 10.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Border _dash = new() { Width = 20, Height = 2.5, CornerRadius = new CornerRadius(1.25), Background = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) };
    readonly TextBlock _chevron = new() { Text = "", FontSize = 10, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Grid _calendar = new() { Margin = new Thickness(14, 0, 14, 12) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _month;
    string _shown = "";
    bool _open;
    double _calHeight = CalendarHeight; // measured: 4–6 week rows, no empty space under "DZIŚ"

    public double Scale { get; private set; } = 1;

    public ClockWindow()
    {
        SquareTopCorners = true;
        Radius = 18;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        Frame.VerticalAlignment = VerticalAlignment.Top;

        _date.Foreground = Ui.Res("FgDim");
        _chevron.FontFamily = Ui.Font("IconFont");
        _chevron.Foreground = Ui.Res("Fg");

        var handle = new Grid { Width = 56, Height = 16, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 3, 0, 0), ToolTip = L.T("Kalendarz") };
        _dash.HorizontalAlignment = HorizontalAlignment.Center;
        _dash.VerticalAlignment = VerticalAlignment.Center;
        handle.Children.Add(_dash);
        handle.Children.Add(_chevron);
        handle.MouseEnter += (_, _) => { _dash.Visibility = Visibility.Collapsed; _chevron.Visibility = Visibility.Visible; };
        handle.MouseLeave += (_, _) => { _dash.Visibility = Visibility.Visible; _chevron.Visibility = Visibility.Collapsed; };
        handle.MouseLeftButtonUp += (_, e) => { e.Handled = true; Toggle(!_open); };

        var header = new StackPanel { Height = HeaderHeight, Margin = new Thickness(0, 4, 0, 0) };
        header.Children.Add(_time);
        header.Children.Add(_date);
        header.Children.Add(handle);

        var stack = new StackPanel { Width = TabWidth };
        stack.Children.Add(header);
        stack.Children.Add(_calendar);
        stack.MouseRightButtonUp += (_, e) => { e.Handled = true; App.Instance.ShowSettings(); };
        Body = stack;

        Deactivated += (_, _) => Toggle(false);
        _timer.Tick += (_, _) => UpdateText();
        _timer.Start();
        UpdateText();
    }

    public void SetScale(double scale)
    {
        Scale = scale;
        ApplyScale(scale);
        Frame.BeginAnimation(HeightProperty, null);
        Frame.Height = (_open ? HeaderHeight + _calHeight : HeaderHeight) * scale;
    }

    public void UpdateText()
    {
        var now = DateTime.Now;
        var time = App.Settings.Use24h ? now.ToString("HH:mm") : now.ToString("h:mm", CultureInfo.InvariantCulture);
        // CZWARTEK 8 PAŹDZIERNIK / THURSDAY 8 OCTOBER (month in the nominative, like a wall calendar)
        var date = L.LongDay(now);
        if (time + date == _shown) return;
        _shown = time + date;
        _time.Text = time;
        _date.Text = App.Settings.Use24h ? date : $"{date} · {now.ToString("tt", CultureInfo.InvariantCulture)}";
    }

    public void Open() => Toggle(true);

    void Toggle(bool open)
    {
        if (_open == open) return;
        _open = open;
        if (open)
        {
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            FillCalendar();
            Activate();
            Anim.Enter(_calendar, 0, -10, 300, 60);
        }
        Anim.Height(Frame, (open ? HeaderHeight + _calHeight : HeaderHeight) * Scale, open ? 320 : 240);
    }

    void FillCalendar()
    {
        _calendar.Children.Clear();
        _calendar.RowDefinitions.Clear();
        _calendar.ColumnDefinitions.Clear();
        var weekendRed = new SolidColorBrush(Color.FromRgb(0xF2, 0x6D, 0x6D));

        // header: ‹  PAŹDZIERNIK 2026  ›
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        var prev = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = "", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 9 };
        var next = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = "", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 9 };
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
        bool showRange = App.Board?.View != ViewMode.Year;
        var shownFrom = App.Board?.FirstMonday ?? BoardWindow.Monday(DateTime.Today);
        var shownTo = shownFrom.AddDays(7 * (App.Board?.WeekCount ?? 2) - 1);
        // (start computed above)
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
                bool red = i >= 5 || PolishHolidays.DayOff(d) != null;
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
                    ToolTip = PolishHolidays.DayOff(d),
                };
                if (!today) Anim.HoverBackground(cell, Colors.Transparent, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
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
        var h = _calendar.DesiredSize.Height;
        if (h > 0 && Math.Abs(h - _calHeight) > 0.5)
        {
            _calHeight = h;
            if (_open && Frame.Height > 0) Anim.Height(Frame, (HeaderHeight + _calHeight) * Scale, 200); // month with more / fewer weeks
        }
    }

    void Pick(DateTime d)
    {
        Toggle(false);
        App.Board?.JumpTo(d);
    }
}
