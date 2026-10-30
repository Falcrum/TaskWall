using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TaskWall;

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

/// <summary>
/// Two children: the year's grid on top gets the height the second one leaves, the second one (the picked week and month)
/// is centred in the space left under the grid. When the window is too low for both, the second one isn't shown.
/// </summary>
sealed class TopAndRest : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count < 2) return new Size(0, 0);
        UIElement top = InternalChildren[0], rest = InternalChildren[1];
        rest.Measure(new Size(available.Width, double.PositiveInfinity));
        double restH = Fits(rest, available.Height) ? rest.DesiredSize.Height : 0;
        top.Measure(new Size(available.Width, double.IsInfinity(available.Height) ? double.PositiveInfinity : available.Height - restH));
        return new Size(double.IsInfinity(available.Width) ? Math.Max(top.DesiredSize.Width, rest.DesiredSize.Width) : available.Width,
            double.IsInfinity(available.Height) ? top.DesiredSize.Height + restH : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count < 2) return final;
        UIElement top = InternalChildren[0], rest = InternalChildren[1];
        double topH = Math.Min(final.Height, top.DesiredSize.Height);
        top.Arrange(new Rect(0, 0, final.Width, topH));
        if (Fits(rest, final.Height)) rest.Arrange(new Rect(0, topH, final.Width, Math.Max(rest.DesiredSize.Height, final.Height - topH)));
        else rest.Arrange(new Rect(0, 0, 0, 0)); // clipped away
        return final;
    }

    /// <summary>The grid keeps at least half of the height.</summary>
    static bool Fits(UIElement rest, double height) => double.IsInfinity(height) || rest.DesiredSize.Height <= height * 0.5;
}

/// <summary>Year at a glance: one rounded square per day in one continuous grid of week columns; month names above the column where each month starts.</summary>
static class YearView
{
    const double LabelW = 1.8, MonthH = 2.0; // month name + HO/BŚU counts
    const double WeekH = 0.9; // the row of week numbers under the grid (a click picks the week of the middle part)

    /// <summary>The week (its Monday) and the month (its 1st) the middle part shows – kept while the view is rebuilt.</summary>
    static DateTime _week, _month;

    /// <summary>Accent strength for 1, 2, 3, 4, 5+ done tasks on a day.</summary>
    static readonly byte[] Levels = { 0x48, 0x70, 0x98, 0xC4, 0xFF };

    /// <summary>
    /// Under the year's grid: done tasks per month as small columns (this month in the accent), the year in numbers
    /// (done, hours, the longest streak of working days with something done, the best month) and the share of each project.
    /// </summary>
    static FrameworkElement Summary(int year, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<TaskItem>> byDay, bool animate, System.Collections.Generic.List<MarkType> types, Func<string, (int Used, int Planned)> markCount, Func<MarkType, int> carry)
    {
        var today = DateTime.Today;
        var green = TaskMarks.DoneGreen;
        var accent = ((SolidColorBrush)Ui.Res("AccentBrush")).Color;
        var done = byDay.Values.SelectMany(l => l).Where(t => t.Done && t.Day != null).ToList();
        TextBlock Caption(string s) => new() { Text = L.Up(s), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(0, 0, 0, 6) };

        var root = new Grid { Margin = new Thickness(6, 16, 6, 4), MaxHeight = 200 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        // ----- done per month -----
        var perMonth = Enumerable.Range(1, 12).Select(m => done.Count(t => t.Day!.Length >= 7 && t.Day.Substring(5, 2) == m.ToString("00"))).ToArray();
        int max = Math.Max(1, perMonth.Max());
        var monthsBox = new DockPanel();
        var mc = Caption(L.T("Zrobione w miesiącach"));
        DockPanel.SetDock(mc, Dock.Top);
        monthsBox.Children.Add(mc);
        var months = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 12 };
        for (int m = 1; m <= 12; m++)
        {
            bool now = year == today.Year && m == today.Month;
            var col = new DockPanel { Margin = new Thickness(3, 0, 3, 0) };
            var name = new TextBlock { Text = BoardWindow.Pl.TextInfo.ToTitleCase(BoardWindow.Pl.DateTimeFormat.GetAbbreviatedMonthName(m).TrimEnd('.')), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Foreground = now ? Ui.Res("AccentBrush") : Ui.Res("FgFaint"), Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(name, Dock.Bottom);
            col.Children.Add(name);
            var count = new TextBlock { Text = perMonth[m - 1] > 0 ? perMonth[m - 1].ToString() : "", FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Res("FgDim") };
            DockPanel.SetDock(count, Dock.Top);
            var holder = new Grid();
            var bar = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Height = 4 + 56.0 * perMonth[m - 1] / max,
                Background = new SolidColorBrush(Color.FromArgb(perMonth[m - 1] == 0 ? (byte)0x18 : (byte)(0x60 + 0x9F * perMonth[m - 1] / max), green.R, green.G, green.B)),
                BorderBrush = now ? new SolidColorBrush(accent) : null, BorderThickness = new Thickness(now ? 1.5 : 0),
                ToolTip = L.F("{0}: zrobione {1}", BoardWindow.Pl.DateTimeFormat.GetMonthName(m), perMonth[m - 1]),
            };
            holder.Children.Add(bar);
            var stack = new DockPanel();
            stack.Children.Add(count);
            stack.Children.Add(holder);
            col.Children.Add(stack);
            months.Children.Add(col);
            if (animate && Anim.On)
            {
                bar.RenderTransformOrigin = new Point(0.5, 1);
                var s = new ScaleTransform(1, 0);
                bar.RenderTransform = s;
                s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(500 + m * 45), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        monthsBox.Children.Add(months);
        root.Children.Add(monthsBox);

        // ----- the year in numbers -----
        var stats = new StackPanel();
        Grid.SetColumn(stats, 2);
        stats.Children.Add(Caption(L.T("Rok w liczbach")));
        double hours = done.Sum(t => t.Estimate ?? 0);
        void Stat(string value, string label)
        {
            var tb = new TextBlock { Margin = new Thickness(0, 0, 0, 3), FontSize = 12 };
            tb.Inlines.Add(new Run(value + "  ") { FontWeight = FontWeights.Bold, FontSize = 15, Foreground = Ui.Res("Fg") });
            tb.Inlines.Add(new Run(label) { Foreground = Ui.Res("FgDim") });
            stats.Children.Add(tb);
        }
        Stat(done.Count.ToString(), L.T("zadań zrobionych"));
        if (hours > 0) Stat(BoardWindow.Hours(Math.Round(hours, 1)), L.T("zrobionej estymacji"));
        root.Children.Add(stats);

        // ----- days off: what is used and what is left of each kind with a yearly allowance (BŚU, sick leave …) -----
        var leave = new StackPanel();
        Grid.SetColumn(leave, 4);
        leave.Children.Add(Caption(L.T("Dni wolne")));
        foreach (var m in types.Where(m => m.Allowance is > 0 || (!m.Ring && markCount(m.Code).Used + markCount(m.Code).Planned > 0)))
        {
            // working days only (a BŚU series over a weekend doesn't use Saturday); taken = up to today, planned = after
            var (used, planned) = markCount(m.Code);
            int carried = carry(m); // left from earlier years
            int all = (m.Allowance ?? 0) + carried, left = Math.Max(0, all - used - planned);
            var c = BoardWindow.ParseColor(m.Color);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var ring = Ring(all > 0 ? (double)left / all : 0, c, all > 0 ? left.ToString() : (used + planned).ToString());
            DockPanel.SetDock(ring, Dock.Left);
            row.Children.Add(ring);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = MarkType.ShowLabel(m.Code, m.Label.Length > 0 ? m.Label : m.Code) + "  (" + MarkType.Show(m.Code) + ")", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Fg"), TextTrimming = TextTrimming.CharacterEllipsis });
            var line = new TextBlock { FontSize = 11, Foreground = Ui.Res("FgDim") };
            if (all > 0)
            {
                line.Inlines.Add(new Run(L.F("{0}: dostępne ", year)));
                line.Inlines.Add(new Run(left.ToString()) { FontWeight = FontWeights.Bold, Foreground = Ui.Res("Fg") });
                line.Inlines.Add(new Run(" " + L.F("z {0} dni", all) + (carried > 0 ? " " + L.F("(w tym {0} zaległych)", carried) : "") + "  ·  " + L.F("wykorzystane {0}", used) + (planned > 0 ? "  ·  " + L.F("zaplanowane {0}", planned) : "")));
            }
            else line.Inlines.Add(new Run(L.F("{0}: {1} dni", year, used + planned)));
            text.Children.Add(line);
            row.Children.Add(text);
            leave.Children.Add(row);
        }
        if (leave.Children.Count == 1) leave.Children.Add(new TextBlock { Text = L.T("Ustaw liczbę dni w roku przy oznaczeniach (Ustawienia → Konta).") , FontSize = 11, Foreground = Ui.Res("FgFaint"), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(leave);

        // ----- projects -----
        var proj = new StackPanel();
        Grid.SetColumn(proj, 6);
        proj.Children.Add(Caption(L.T("Projekty")));
        var groups = done.GroupBy(t => t.NotionSource ?? "").Select(g => (Name: g.Key, Count: g.Count(), Color: TaskMarks.ProjectColor(g.First()) ?? Color.FromRgb(0x7E, 0x8C, 0x9C)))
            .OrderByDescending(g => g.Count).ToList();
        if (groups.Count > 0)
        {
            var barRow = new Grid { Height = 10, Margin = new Thickness(0, 2, 0, 8), ClipToBounds = true };
            foreach (var g in groups) barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(g.Count, GridUnitType.Star) });
            for (int i = 0; i < groups.Count; i++)
            {
                var seg = new Border { Background = new SolidColorBrush(groups[i].Color), Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0), CornerRadius = new CornerRadius(i == 0 ? 3 : 0, i == groups.Count - 1 ? 3 : 0, i == groups.Count - 1 ? 3 : 0, i == 0 ? 3 : 0), ToolTip = $"{(groups[i].Name.Length > 0 ? groups[i].Name : L.T("bez projektu"))}: {groups[i].Count}" };
                Grid.SetColumn(seg, i);
                barRow.Children.Add(seg);
            }
            proj.Children.Add(barRow);
            foreach (var g in groups.Take(4))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(g.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = $"{(g.Name.Length > 0 ? g.Name : L.T("bez projektu"))}  {g.Count}  ·  {Math.Round(100.0 * g.Count / done.Count)}%", FontSize = 11, Foreground = Ui.Res("FgDim") });
                proj.Children.Add(row);
            }
        }
        else proj.Children.Add(new TextBlock { Text = L.T("Brak zrobionych zadań w tym roku."), FontSize = 11, Foreground = Ui.Res("FgFaint") });
        root.Children.Add(proj);
        return root;
    }

    /// <summary>A ring like a gauge: the coloured arc is the share left (from the top, clockwise), the number in the middle.</summary>
    static FrameworkElement Ring(double fraction, Color c, string center)
    {
        const double size = 42, thick = 4.5;
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(Th.Ink(0x1C)), StrokeThickness = thick });
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction >= 0.999) g.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = new SolidColorBrush(c), StrokeThickness = thick });
        else if (fraction > 0)
        {
            double r = (size - thick) / 2, cx = size / 2, cy = size / 2, a = fraction * 2 * Math.PI;
            var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = new Point(cx, cy - r) };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            g.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { fig }), Stroke = new SolidColorBrush(c), StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        g.Children.Add(new TextBlock { Text = center, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Fg"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }

    /// <summary>A filled day mark (BŚU, leave): hatched at 45° in its colour, like on the timeline.</summary>
    static Brush Hatch(Color c)
    {
        if (Hatches.TryGetValue(c, out var cached)) return cached; // one frozen brush per colour (not one per marked day)
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)), null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        g.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0xD0, c.R, c.G, c.B)), 1.4), Geometry.Parse("M0,6 L6,0 M-1.5,1.5 L1.5,-1.5 M4.5,7.5 L7.5,4.5")));
        var brush = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze();
        return Hatches[c] = brush;
    }

    static readonly System.Collections.Generic.Dictionary<Color, Brush> Hatches = new();

    public static FrameworkElement Build(int year, BoardStore store, Action<DateTime> pick, bool animate = true)
    {
        var data = store.Data;
        var types = store.MarkTypes;
        var accent = ((SolidColorBrush)Ui.Res("AccentBrush")).Color;
        var today = DateTime.Today;
        int days = DateTime.IsLeapYear(year) ? 366 : 365;
        var weekendRed = Color.FromRgb(0xF2, 0x6D, 0x6D);
        var green = TaskMarks.DoneGreen; // done tasks: green, like the state bar of a done task

        var byDay = data.Tasks.Where(t => t.Day != null && t.Day.StartsWith(year.ToString(), StringComparison.Ordinal))
            .GroupBy(t => t.Day!).ToDictionary(g => g.Key, g => g.ToList());
        // unfinished tasks that moved on: the day was worked on, not finished
        var continued = data.Tasks.Where(t => t.WorkedDays != null).SelectMany(t => t.WorkedDays!).Where(d => d.StartsWith(year.ToString(), StringComparison.Ordinal))
            .GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());

        var dock = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };

        // ----- header: "Dzień 280 / 365" + legend -----
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        int dayNo = year < today.Year ? days : year > today.Year ? 0 : today.DayOfYear;
        var title = new TextBlock { FontSize = 30, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom };
        title.Inlines.Add(new Run(L.T("Dzień") + " ") { Foreground = new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B)) });
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
                Text = $"{MarkType.Show(code)} {MarkCount(code)}", FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 14, 0),
                Foreground = new SolidColorBrush(BoardWindow.ParseColor(color)), ToolTip = L.F("{0}: dni w {1}", label, year),
            });
        header.Children.Add(totals);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) };
        void Swatch(Border b) { b.Width = 11; b.Height = 11; b.CornerRadius = new CornerRadius(3); b.VerticalAlignment = VerticalAlignment.Center; legend.Children.Add(b); }
        void Text(string label, double left = 6) => legend.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 14, 0) });
        // intensity scale: less → more done tasks (like a contribution graph)
        Text(L.T("mniej"), 0);
        foreach (var a in Levels) { var b = new Border { Background = new SolidColorBrush(Color.FromArgb(a, green.R, green.G, green.B)), Margin = new Thickness(0, 0, 3, 0) }; Swatch(b); }
        Text(L.T("więcej zrobionych"), 3);
        Swatch(new Border { BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(1.5) });
        Text(L.T("zaplanowane"));
        foreach (var m in types)
        {
            var c = BoardWindow.ParseColor(m.Color);
            if (m.Ring) Swatch(new Border { BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(1.5) });
            else Swatch(new Border { Background = Hatch(c) });
            Text(MarkType.Show(m.Code));
        }
        header.Children.Add(legend);
        dock.Children.Add(header);

        // ----- layout: months side by side, each month = its own week columns -----
        var panel = new UnitPanel();
        string[] dayLabels = L.En ? new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" } : new[] { "Pn", "Wt", "Śr", "Cz", "Pt", "So", "Nd" };
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

        // past days are a bit darker than the ones ahead (never lighter)
        var empty = Th.Ink(0x0E);
        var future = Th.Ink(0x1A);
        var marks = types.GroupBy(m => m.Code).ToDictionary(g => g.Key, g => BoardWindow.ParseColor(g.First().Color));
        var ringMarks = types.Where(m => m.Ring).Select(m => m.Code).ToHashSet();
        var yearMonday = BoardWindow.Monday(new DateTime(year, 1, 1));
        int Col(DateTime d) => (int)((BoardWindow.Monday(d) - yearMonday).TotalDays / 7);
        int totalCols = Col(new DateTime(year, 12, 31)) + 1;
        double x = LabelW;
        int index = 0;
        var monthNames = new List<(int Month, Border Box)>();
        Action<bool>? showPeriods = null; // set below, once the grid is there
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
            };
            // a click on the name: this month in the middle part
            int thisMonth = month;
            var nameBox = new Border
            {
                Child = name, Padding = new Thickness(3, 0, 4, 0), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand,
                ToolTip = L.T("Pokaż statystyki tego miesiąca"),
            };
            nameBox.MouseLeftButtonUp += (_, _) => { _month = new DateTime(year, thisMonth, 1); showPeriods?.Invoke(false); };
            UnitPanel.SetBox(nameBox, new Rect(x + startCol - 0.05, 0, 4, 1));
            panel.Children.Add(nameBox);
            monthNames.Add((month, nameBox));
            var counts = types.Select(m => (m.Code, m.Color, n: MarkCount(m.Code, month))).Where(x => x.n > 0).ToList();
            if (counts.Count > 0)
            {
                var line = new TextBlock { VerticalAlignment = VerticalAlignment.Top, RenderTransform = new ScaleTransform(0.82, 0.82) };
                foreach (var (code, color, n) in counts)
                    line.Inlines.Add(new Run($"{MarkType.Show(code)} {n}  ") { Foreground = new SolidColorBrush(BoardWindow.ParseColor(color)), FontWeight = FontWeights.SemiBold });
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
                int total = (tasks?.Count ?? 0) + (continued.TryGetValue(key, out var cont) ? cont : 0), done = tasks?.Count(t => t.Done) ?? 0; // continued = worked on, not finished that day
                var mark = markOf.TryGetValue(key, out var mk2) ? mk2 : null;
                bool past = date <= today;
                var dayOff = DaysOff.Name(date);

                Color fill;
                Color? ring = null;
                bool markRing = mark != null && ringMarks.Contains(mark); // e.g. HO: a frame, the day still shows its tasks
                if (mark != null && !markRing && marks.TryGetValue(mark, out var mc)) fill = mc;
                // intensity by the number of done tasks; worked on but nothing finished = a faint tint
                else if (past && done > 0) fill = Color.FromArgb(Levels[Math.Min(done, Levels.Length) - 1], green.R, green.G, green.B);
                else if (date < today && total > 0) fill = Color.FromArgb(0x26, green.R, green.G, green.B); // worked on, nothing done (today is still going)
                else if (!past && total > 0) { fill = future; ring = Color.FromArgb(0xB0, accent.R, accent.G, accent.B); }
                // weekends and holidays: tinted, like their cells on the board
                else fill = row >= 5 || dayOff != null ? Color.FromArgb(past ? (byte)0x1A : (byte)0x24, weekendRed.R, weekendRed.G, weekendRed.B) : past ? empty : future;
                // HO and the like: a frame in their colour (the day still shows its tasks); today: the accent frame
                if (markRing && marks.TryGetValue(mark!, out var rcol)) ring = rcol;
                if (date == today) ring = accent;
                Brush? hatch = mark != null && !markRing && marks.TryGetValue(mark, out var hc) ? Hatch(hc) : null; // BŚU, leave: hatched in their colour

                bool strong = fill.A > 0x90;
                var cell = new Border
                {
                    Margin = new Thickness(1.6),
                    Background = hatch ?? new SolidColorBrush(fill),
                    BorderBrush = ring is { } rc ? new SolidColorBrush(rc) : null,
                    BorderThickness = new Thickness(ring == null ? 0 : date == today ? 2 : 1.5),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = date.Day.ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = strong ? Brushes.White : hatch != null ? Ui.Res("Fg") : Ui.Res("FgFaint"),
                        FontWeight = date == today || strong ? FontWeights.Bold : FontWeights.Normal,
                    },
                };
                cell.SizeChanged += (_, e) => cell.CornerRadius = new CornerRadius(Math.Min(4, e.NewSize.Width * 0.16));
                var tip = date.ToString("dddd, d MMMM yyyy", BoardWindow.Pl) + "  ·  " + L.F("tydz. {0}", BoardWindow.WeekNo(date));
                if (dayOff != null) tip += "\n" + L.F("{0} (dzień wolny)", dayOff);
                if (total > 0) tip += "\n" + L.F("zadania: {0}, zrobione: {1}", total, done);
                if (mark != null) tip += "\n" + MarkType.Show(mark);
                cell.ToolTip = tip;
                cell.MouseEnter += (_, _) => cell.Opacity = 0.7;
                cell.MouseLeave += (_, _) => cell.Opacity = 1;
                cell.MouseLeftButtonUp += (_, _) => pick(date);
                UnitPanel.SetBox(cell, new Rect(x + col, MonthH + row, 1, 1));
                panel.Children.Add(cell);

                if (animate) Anim.Enter(cell, 0, 0, 220, (int)(index++ * 1.5)); // "fills itself in"
                if (date == today && Anim.On && cell.BorderBrush is SolidColorBrush pulseBrush)
                {
                    // today's frame breathes gently (stopped when the view goes away)
                    var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(1100))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                        BeginTime = TimeSpan.FromMilliseconds(animate ? days * 1.5 + 300 : 0),
                    };
                    pulseBrush.BeginAnimation(SolidColorBrush.OpacityProperty, pulse);
                    cell.Unloaded += (_, _) => pulseBrush.BeginAnimation(SolidColorBrush.OpacityProperty, null);
                }
            }
        }
        // ----- week numbers under the grid: a click shows that week in the middle part -----
        if (_week.DayOfWeek != DayOfWeek.Monday || Col(_week) < 0 || Col(_week) >= totalCols)
            _week = year == today.Year ? BoardWindow.Monday(today) : year < today.Year ? BoardWindow.Monday(new DateTime(year, 12, 31)) : yearMonday;
        if (_month.Year != year) _month = new DateTime(year, year == today.Year ? today.Month : year < today.Year ? 12 : 1, 1);
        var weekLabels = new List<(DateTime Monday, TextBlock Text)>();
        var weekCaption = new TextBlock { Text = L.T("tydz."), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgFaint"), RenderTransform = new ScaleTransform(0.8, 0.8), RenderTransformOrigin = new Point(0, 0.5) };
        UnitPanel.SetBox(weekCaption, new Rect(0, MonthH + 7, LabelW, WeekH));
        panel.Children.Add(weekCaption);
        for (int c = 0; c < totalCols; c++)
        {
            var monday = yearMonday.AddDays(7 * c);
            var text = new TextBlock { Text = BoardWindow.WeekNo(monday).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new ScaleTransform(0.82, 0.82), RenderTransformOrigin = new Point(0.5, 0.5) };
            var hit = new Border { Child = text, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L.F("Tydzień {0} – pokaż jego statystyki", BoardWindow.WeekNo(monday)) };
            hit.MouseEnter += (_, _) => hit.Opacity = 0.6;
            hit.MouseLeave += (_, _) => hit.Opacity = 1;
            hit.MouseLeftButtonUp += (_, _) => { _week = monday; showPeriods?.Invoke(false); };
            UnitPanel.SetBox(hit, new Rect(x + c, MonthH + 7, 1, WeekH));
            panel.Children.Add(hit);
            weekLabels.Add((monday, text));
        }
        // the picked week: a frame round its column (in the gaps between the squares)
        var weekFrame = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B)), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
        panel.Children.Add(weekFrame);

        panel.UnitsWide = x + totalCols;
        panel.UnitsHigh = MonthH + 7 + WeekH;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Top;

        // ----- the middle part: the picked week and month in numbers -----
        var middle = new Grid { Margin = new Thickness(6, 12, 6, 0), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var monthTint = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
        void StepWeek(int dir) { var w = _week.AddDays(7 * dir); if (Col(w) >= 0 && Col(w) < totalCols) { _week = w; showPeriods!(false); } }
        void StepMonth(int dir) { var m = _month.AddMonths(dir); if (m.Year == year) { _month = m; showPeriods!(false); } }
        showPeriods = anim =>
        {
            UnitPanel.SetBox(weekFrame, new Rect(x + Col(_week), MonthH - 0.06, 1, 7 + WeekH + 0.06));
            foreach (var (monday, text) in weekLabels)
            {
                bool on = monday == _week;
                text.Foreground = on ? Ui.Res("AccentBrush") : Ui.Res("FgFaint");
                text.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
            }
            foreach (var (m, box) in monthNames) box.Background = m == _month.Month ? monthTint : Brushes.Transparent;
            middle.Children.Clear();
            var week = PeriodCard(store, _week, false, StepWeek, Col(_week) > 0, Col(_week) < totalCols - 1, pick, anim, 0);
            var monthCard = PeriodCard(store, _month, true, StepMonth, _month.Month > 1, _month.Month < 12, pick, anim, 1);
            Grid.SetColumn(monthCard, 2);
            middle.Children.Add(week);
            middle.Children.Add(monthCard);
        };
        showPeriods(animate);

        // the year in numbers under the grid: done tasks per month, a few totals, the share of each project
        var summary = Summary(year, byDay, animate, types, code => store.LeaveDays(code, year), m => store.CarryInto(m, year));
        DockPanel.SetDock(summary, Dock.Bottom);
        dock.Children.Add(summary);
        var body = new TopAndRest();
        body.Children.Add(panel);
        body.Children.Add(middle);
        dock.Children.Add(body);
        return dock;
    }

    /// <summary>
    /// One period of the middle part (the picked week or month): done of planned (with the change against the period before),
    /// the estimate done, measured time, meetings, overtime, breaks and the days worked; the days as columns (planned in grey,
    /// done in green – a click opens the day), the day marks and the projects of what was done.
    /// </summary>
    static FrameworkElement PeriodCard(BoardStore store, DateTime from, bool month, Action<int> step, bool canBack, bool canNext, Action<DateTime> pick, bool animate, int order)
    {
        var to = month ? from.AddMonths(1).AddDays(-1) : from.AddDays(6);
        var r = Report.Build(store, App.Settings.Layer, from, to, month, insights: false);
        var today = DateTime.Today;
        var green = TaskMarks.DoneGreen;
        var weekendRed = Color.FromRgb(0xF2, 0x6D, 0x6D);
        var pl = BoardWindow.Pl;

        var dock = new DockPanel();
        var card = new Border
        {
            Background = new SolidColorBrush(Th.Ink(0x08)), BorderBrush = new SolidColorBrush(Th.Ink(0x14)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 14, 10), Child = dock,
        };

        // ----- header: ‹ TYDZIEŃ 44 ›  26 paź – 1 lis  ·········  Raport… -----
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(head, Dock.Top);
        var open = new TextBlock { Text = L.T("Raport…"), FontSize = 11, Foreground = Ui.Res("AccentBrush"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = month ? L.T("Pełny raport tego miesiąca") : L.T("Pełny raport tego tygodnia") };
        open.MouseLeftButtonUp += (_, _) => ReportWindow.Show(store, App.Settings.Layer, from, month);
        DockPanel.SetDock(open, Dock.Right);
        head.Children.Add(open);
        TextBlock Arrow(string s, int dir, bool on)
        {
            var a = new TextBlock
            {
                Text = s, FontSize = 16, FontWeight = FontWeights.Bold, Padding = new Thickness(4, 0, 4, 1), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
                Foreground = on ? Ui.Res("FgDim") : new SolidColorBrush(Th.Ink(0x24)), Cursor = on ? Cursors.Hand : null,
                ToolTip = on ? (dir < 0 ? (month ? L.T("Poprzedni miesiąc") : L.T("Poprzedni tydzień")) : (month ? L.T("Następny miesiąc") : L.T("Następny tydzień"))) : null,
            };
            if (on) a.MouseLeftButtonUp += (_, e) => { e.Handled = true; step(dir); };
            return a;
        }
        head.Children.Add(Arrow("‹", -1, canBack));
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new Run(L.Up(month ? pl.DateTimeFormat.GetMonthName(from.Month) : L.F("Tydzień {0}", ISOWeek.GetWeekOfYear(from)))) { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Fg") });
        head.Children.Add(title);
        head.Children.Add(Arrow("›", 1, canNext));
        head.Children.Add(new TextBlock
        {
            Text = month ? from.Year.ToString() : $"{from.ToString("d MMM", pl)} – {to.ToString("d MMM", pl)}", FontSize = 11, Foreground = Ui.Res("FgFaint"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
        });
        dock.Children.Add(head);

        // ----- numbers -----
        var stats = new WrapPanel { Margin = new Thickness(4, 0, 0, 8) };
        DockPanel.SetDock(stats, Dock.Top);
        void Stat(string value, string label, string? extra = null, Brush? extraBrush = null, string? tip = null)
        {
            var tb = new TextBlock { Margin = new Thickness(0, 0, 18, 2), FontSize = 11.5, ToolTip = tip };
            tb.Inlines.Add(new Run(value + " ") { FontWeight = FontWeights.Bold, FontSize = 15, Foreground = Ui.Res("Fg") });
            tb.Inlines.Add(new Run(label) { Foreground = Ui.Res("FgDim") });
            if (extra != null) tb.Inlines.Add(new Run("  " + extra) { FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = extraBrush });
            stats.Children.Add(tb);
        }
        static string H(double h) => BoardWindow.Hours(Math.Round(h, 1));
        if (from > today) Stat(r.Tasks.ToString(), L.T("zaplanowanych"));
        else
        {
            // the period before, counted the same way (done tasks on its days)
            var (pf, pt) = month ? (from.AddMonths(-1), from.AddDays(-1)) : (from.AddDays(-7), from.AddDays(-1));
            string a = BoardWindow.DayKey(pf), b = BoardWindow.DayKey(pt);
            int before = store.Data.Tasks.Count(t => t.Done && t.Day != null && t.Text.Length > 0 && string.CompareOrdinal(t.Day, a) >= 0 && string.CompareOrdinal(t.Day, b) <= 0);
            int delta = r.Done - before;
            Stat(r.Tasks > r.Done ? $"{r.Done} / {r.Tasks}" : r.Done.ToString(), r.Tasks > r.Done ? L.T("zrobionych z zaplanowanych") : L.T("zrobionych"),
                delta == 0 ? null : (delta > 0 ? "▲ " : "▼ ") + Math.Abs(delta), new SolidColorBrush(delta > 0 ? green : weekendRed),
                month ? L.F("W poprzednim miesiącu zrobione: {0}", before) : L.F("W poprzednim tygodniu zrobione: {0}", before));
        }
        if (r.DoneHours > 0) Stat(H(r.DoneHours), L.T("zrobionej estymacji"));
        if (r.Tracked >= 0.05) Stat(H(r.Tracked), L.T("zmierzone"));
        if (r.Meetings > 0) Stat(H(r.Meetings), L.T("spotkań"));
        if (r.Overtime > 0) Stat(H(r.Overtime), L.T("nadgodzin"));
        if (r.Afk > 0) Stat(H(r.Afk), L.T("przerw"));
        int worked = r.Days.Count(d => d.Date <= today && (d.Done > 0 || d.Continued > 0));
        if (worked > 0) Stat(worked.ToString(), L.T("dni z pracą"));
        dock.Children.Add(stats);

        // ----- day marks and the projects of what was done -----
        var foot = new WrapPanel { Margin = new Thickness(4, 6, 0, 0) };
        DockPanel.SetDock(foot, Dock.Bottom);
        foreach (var kv in r.Marks.OrderByDescending(kv => kv.Value.Days))
            foot.Children.Add(new TextBlock
            {
                Text = $"{MarkType.Show(kv.Key)} {kv.Value.Days}", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 12, 0),
                Foreground = new SolidColorBrush(BoardWindow.ParseColor(kv.Value.Color)), ToolTip = L.F("{0}: {1} dni", kv.Value.Label, kv.Value.Days),
            });
        var done = r.All.Where(x => x.Task.Done).Select(x => x.Task).Distinct().ToList();
        foreach (var g in done.GroupBy(t => t.NotionSource ?? "").Select(g => (Name: g.Key, Count: g.Count(), Color: TaskMarks.ProjectColor(g.First()) ?? Color.FromRgb(0x7E, 0x8C, 0x9C))).OrderByDescending(g => g.Count).Take(4))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(g.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{(g.Name.Length > 0 ? g.Name : L.T("bez projektu"))}  {g.Count}", FontSize = 11, Foreground = Ui.Res("FgDim") });
            foot.Children.Add(row);
        }
        if (foot.Children.Count > 0) dock.Children.Add(foot);

        // ----- the days: planned (grey) and done (green) -----
        string[] dayLabels = L.En ? new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" } : new[] { "Pn", "Wt", "Śr", "Cz", "Pt", "So", "Nd" };
        int max = Math.Max(1, r.Days.Max(d => Math.Max(d.Tasks, d.Done)));
        const double barsH = 44;
        var bars = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = r.Days.Count };
        int i = 0;
        foreach (var d in r.Days)
        {
            bool off = d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || DaysOff.Name(d.Date) != null;
            var col = new DockPanel { Margin = new Thickness(month ? 1.5 : 5, 0, month ? 1.5 : 5, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var label = new TextBlock
            {
                Text = month ? d.Date.Day.ToString() : dayLabels[((int)d.Date.DayOfWeek + 6) % 7], FontSize = month ? 9 : 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
                Foreground = d.Date == today ? Ui.Res("AccentBrush") : off ? new SolidColorBrush(Color.FromArgb(0xB0, weekendRed.R, weekendRed.G, weekendRed.B)) : Ui.Res("FgFaint"),
                FontWeight = d.Date == today ? FontWeights.Bold : FontWeights.Normal,
            };
            DockPanel.SetDock(label, Dock.Bottom);
            col.Children.Add(label);
            if (!month)
            {
                var count = new TextBlock { Text = d.Done > 0 ? d.Done.ToString() : "", FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Res("FgDim") };
                DockPanel.SetDock(count, Dock.Top);
                col.Children.Add(count);
            }
            var holder = new Grid { Height = barsH, VerticalAlignment = VerticalAlignment.Bottom };
            double BarH(int n) => 3 + (barsH - 3) * n / max;
            holder.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Height = d.Tasks > 0 ? BarH(d.Tasks) : 3, Background = new SolidColorBrush(Th.Ink(d.Tasks > 0 ? (byte)0x1C : (byte)0x0E)) });
            Border? doneBar = null;
            if (d.Done > 0)
            {
                doneBar = new Border { VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Height = BarH(d.Done), Background = new SolidColorBrush(Color.FromArgb(0xD0, green.R, green.G, green.B)) };
                holder.Children.Add(doneBar);
            }
            // a day mark (BŚU, HO …): a strip in its colour under the column
            if (d.Mark != null && store.MarkTypeFor(d.Mark) is { } mt)
                holder.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 3, CornerRadius = new CornerRadius(1.5), Background = mt.Ring ? new SolidColorBrush(BoardWindow.ParseColor(mt.Color)) : Hatch(BoardWindow.ParseColor(mt.Color)), Margin = new Thickness(0, 0, 0, -5) });
            col.Children.Add(holder);
            var tip = d.Date.ToString("dddd, d MMMM", pl);
            if (DaysOff.Name(d.Date) is { } name) tip += "\n" + L.F("{0} (dzień wolny)", name);
            if (d.Tasks > 0) tip += "\n" + L.F("zadania: {0}, zrobione: {1}", d.Tasks, d.Done);
            if (d.Mark != null) tip += "\n" + MarkType.Show(d.Mark);
            col.ToolTip = tip;
            var date = d.Date;
            col.MouseEnter += (_, _) => col.Opacity = 0.7;
            col.MouseLeave += (_, _) => col.Opacity = 1;
            col.MouseLeftButtonUp += (_, _) => pick(date);
            bars.Children.Add(col);
            if (animate && Anim.On && doneBar != null)
            {
                doneBar.RenderTransformOrigin = new Point(0.5, 1);
                var s = new ScaleTransform(1, 0);
                doneBar.RenderTransform = s;
                s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = TimeSpan.FromMilliseconds(450 + order * 120 + i * (month ? 12 : 40)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            i++;
        }
        dock.Children.Add(bars);
        if (animate) Anim.Enter(card, 0, 10, 300, 300 + order * 80);
        return card;
    }
}
