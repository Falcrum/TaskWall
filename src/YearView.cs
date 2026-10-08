using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskWall;

/// <summary>
/// Places children on a grid measured in abstract "units" (one day square = 1×1) and scales the
/// unit so the whole year fits the available space.
/// </summary>
sealed class UnitPanel : Panel
{
    public static readonly DependencyProperty BoxProperty =
        DependencyProperty.RegisterAttached("Box", typeof(Rect), typeof(UnitPanel), new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static void SetBox(UIElement e, Rect r) => e.SetValue(BoxProperty, r);
    static Rect GetBox(UIElement e) => (Rect)e.GetValue(BoxProperty);

    public double UnitsWide { get; set; }
    public double UnitsHigh { get; set; }
    public double Unit { get; private set; }

    protected override Size MeasureOverride(Size available)
    {
        if (UnitsWide <= 0 || double.IsInfinity(available.Width) || double.IsInfinity(available.Height)) return new Size(0, 0);
        Unit = Math.Floor(Math.Min(available.Width / UnitsWide, available.Height / UnitsHigh) * 2) / 2;
        // day numbers / labels scale with the squares
        SetValue(TextElement.FontSizeProperty, Math.Max(7, Unit * 0.40));
        foreach (UIElement c in InternalChildren)
        {
            var b = GetBox(c);
            c.Measure(new Size(b.Width * Unit, b.Height * Unit));
        }
        return new Size(UnitsWide * Unit, UnitsHigh * Unit);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement c in InternalChildren)
        {
            var b = GetBox(c);
            c.Arrange(new Rect(b.X * Unit, b.Y * Unit, b.Width * Unit, b.Height * Unit));
        }
        return final;
    }
}

/// <summary>Year at a glance: one rounded square per day in one continuous grid of week columns; month names above the column where each month starts.</summary>
static class YearView
{
    const double LabelW = 1.8, MonthH = 2.0; // month name + HO/BŚU counts

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static FrameworkElement Build(int year, BoardStore store, Action<DateTime> pick, bool animate = true)
    {
        var data = store.Data;
        var types = store.MarkTypes;
        var accent = ((SolidColorBrush)Ui.Res("AccentBrush")).Color;
        var today = DateTime.Today;
        int days = DateTime.IsLeapYear(year) ? 366 : 365;
        var weekendRed = Color.FromRgb(0xF2, 0x6D, 0x6D);

        var byDay = data.Tasks.Where(t => t.Day != null && t.Day.StartsWith(year.ToString()))
            .GroupBy(t => t.Day!).ToDictionary(g => g.Key, g => g.ToList());

        var dock = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };

        // ----- header: "Dzień 280 / 365" + legend -----
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        int dayNo = year < today.Year ? days : year > today.Year ? 0 : today.DayOfYear;
        var title = new TextBlock { FontSize = 30, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom };
        title.Inlines.Add(new Run("Dzień ") { Foreground = new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B)) });
        title.Inlines.Add(new Run(dayNo.ToString()) { Foreground = Ui.Res("Fg") });
        title.Inlines.Add(new Run($"  / {days}") { Foreground = Ui.Res("FgFaint"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        header.Children.Add(title);

        // year totals of day marks
        // effective marks (by hand + repeating series) of the whole year
        var markOf = new System.Collections.Generic.Dictionary<string, string>();
        for (var d0 = new DateTime(year, 1, 1); d0.Year == year; d0 = d0.AddDays(1))
            if (store.DayMark(BoardWindow.DayKey(d0)) is { } mk) markOf[BoardWindow.DayKey(d0)] = mk;
        int MarkCount(string code, int? month = null) => markOf.Count(kv => kv.Value == code
            && (month == null || kv.Key.Substring(5, 2) == month.Value.ToString("00")));
        var totals = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18, 0, 0, 6) };
        foreach (var (code, label, color) in types.Select(m => (m.Code, m.Label, m.Color)))
            totals.Children.Add(new TextBlock
            {
                Text = $"{code} {MarkCount(code)}", FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 14, 0),
                Foreground = new SolidColorBrush(BoardWindow.ParseColor(color)), ToolTip = $"{label}: dni w {year}",
            });
        header.Children.Add(totals);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) };
        void Legend(Color fill, string label, bool ring = false)
        {
            legend.Children.Add(new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = ring ? Brushes.Transparent : new SolidColorBrush(fill),
                BorderBrush = ring ? new SolidColorBrush(fill) : null, BorderThickness = new Thickness(ring ? 1.5 : 0),
            });
            legend.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center });
        }
        Legend(accent, "zrobione");
        Legend(Color.FromArgb(0x70, accent.R, accent.G, accent.B), "niedokończone");
        Legend(accent, "zaplanowane", ring: true);
        foreach (var m in types) Legend(BoardWindow.ParseColor(m.Color), m.Code);
        header.Children.Add(legend);
        dock.Children.Add(header);

        // ----- layout: months side by side, each month = its own week columns -----
        var panel = new UnitPanel();
        string[] dayLabels = { "Pn", "Wt", "Śr", "Cz", "Pt", "So", "Nd" };
        for (int r = 0; r < 7; r++)
        {
            var l = new TextBlock
            {
                Text = dayLabels[r], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                Foreground = r >= 5 ? new SolidColorBrush(weekendRed) : Ui.Res("FgFaint"), FontWeight = FontWeights.SemiBold,
            };
            UnitPanel.SetBox(l, new Rect(0, MonthH + r, LabelW, 1));
            panel.Children.Add(l);
        }

        var empty = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
        var future = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
        var marks = types.GroupBy(m => m.Code).ToDictionary(g => g.Key, g => BoardWindow.ParseColor(g.First().Color));
        var yearMonday = BoardWindow.Monday(new DateTime(year, 1, 1));
        int Col(DateTime d) => (int)((BoardWindow.Monday(d) - yearMonday).TotalDays / 7);
        int totalCols = Col(new DateTime(year, 12, 31)) + 1;
        double x = LabelW;
        int index = 0;
        for (int month = 1; month <= 12; month++)
        {
            var first = new DateTime(year, month, 1);
            int dim = DateTime.DaysInMonth(year, month);
            int startCol = Col(first);

            var name = new TextBlock
            {
                Text = BoardWindow.Pl.TextInfo.ToTitleCase(BoardWindow.Pl.DateTimeFormat.GetAbbreviatedMonthName(month).TrimEnd('.')),
                FontWeight = FontWeights.Bold,
                Foreground = year == today.Year && month == today.Month ? Ui.Res("AccentBrush") : Ui.Res("FgDim"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            UnitPanel.SetBox(name, new Rect(x + startCol + 0.1, 0, 4, 1));
            panel.Children.Add(name);
            var counts = types.Select(m => (m.Code, m.Color, n: MarkCount(m.Code, month))).Where(x => x.n > 0).ToList();
            if (counts.Count > 0)
            {
                var line = new TextBlock { VerticalAlignment = VerticalAlignment.Top, RenderTransform = new ScaleTransform(0.82, 0.82) };
                foreach (var (code, color, n) in counts)
                    line.Inlines.Add(new Run($"{code} {n}  ") { Foreground = new SolidColorBrush(BoardWindow.ParseColor(color)), FontWeight = FontWeights.SemiBold });
                UnitPanel.SetBox(line, new Rect(x + startCol + 0.1, 0.95, 5, 1));
                panel.Children.Add(line);
            }

            for (int d = 0; d < dim; d++)
            {
                var date = first.AddDays(d);
                int col = Col(date);
                int row = ((int)date.DayOfWeek + 6) % 7;
                var key = BoardWindow.DayKey(date);
                byDay.TryGetValue(key, out var tasks);
                int total = tasks?.Count ?? 0, done = tasks?.Count(t => t.Done) ?? 0;
                var mark = markOf.TryGetValue(key, out var mk2) ? mk2 : null;
                bool past = date <= today;
                var dayOff = PolishHolidays.DayOff(date);

                Color fill;
                Color? ring = null;
                if (mark != null && marks.TryGetValue(mark, out var mc)) fill = mc;
                else if (past && done > 0) fill = done == total ? accent : Color.FromArgb(0xC8, accent.R, accent.G, accent.B);
                else if (past && total > 0) fill = Color.FromArgb(0x70, accent.R, accent.G, accent.B);
                else if (!past && total > 0) { fill = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF); ring = accent; }
                else fill = past ? empty : future;
                if (date == today) ring = Colors.White;

                bool strong = fill.A > 0x90;
                var cell = new Border
                {
                    Margin = new Thickness(1.6),
                    Background = new SolidColorBrush(fill),
                    BorderBrush = ring is { } rc ? new SolidColorBrush(rc) : null,
                    BorderThickness = new Thickness(ring != null ? 1.6 : 0),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = date.Day.ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = strong ? Brushes.White : row >= 5 || dayOff != null ? new SolidColorBrush(Color.FromArgb(0xC0, weekendRed.R, weekendRed.G, weekendRed.B)) : Ui.Res("FgFaint"),
                        FontWeight = date == today || strong ? FontWeights.Bold : FontWeights.Normal,
                    },
                };
                cell.SizeChanged += (_, e) => cell.CornerRadius = new CornerRadius(e.NewSize.Width * 0.24);

                var tip = date.ToString("dddd, d MMMM yyyy", BoardWindow.Pl) + $"  ·  tydz. {BoardWindow.WeekNo(date)}";
                if (dayOff != null) tip += $"\n{dayOff} (dzień wolny)";
                if (total > 0) tip += $"\nzadania: {total}, zrobione: {done}";
                if (mark != null) tip += $"\n{mark}";
                cell.ToolTip = tip;
                cell.MouseEnter += (_, _) => cell.Opacity = 0.7;
                cell.MouseLeave += (_, _) => cell.Opacity = 1;
                cell.MouseLeftButtonUp += (_, _) => pick(date);
                UnitPanel.SetBox(cell, new Rect(x + col, MonthH + row, 1, 1));
                panel.Children.Add(cell);

                if (animate) Anim.Enter(cell, 0, 0, 220, (int)(index++ * 1.5)); // "fills itself in"
                if (date == today && Anim.On)
                {
                    var pulse = new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(900))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(animate ? days * 1.5 + 300 : 0) };
                    cell.BorderBrush.BeginAnimation(SolidColorBrush.OpacityProperty, pulse);
                    cell.Unloaded += (_, _) => cell.BorderBrush?.BeginAnimation(SolidColorBrush.OpacityProperty, null); // stop the endless clock
                }
            }
        }
        panel.UnitsWide = x + totalCols;
        panel.UnitsHigh = MonthH + 7;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Top;
        dock.Children.Add(panel);
        return dock;
    }
}
