using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TaskWall;

/// <summary>
/// The marks every task carries, the same in every view: on the left the state bar over the task's whole height
/// (grey empty to do, orange a third in progress, violet two thirds review, green full done); on the right two thin
/// bars side by side – the priority (red / amber / slate) and the project's colour (FoA, Tuathan …).
/// </summary>
static class TaskMarks
{
    public static readonly Color DoneGreen = Color.FromRgb(0x4C, 0xB8, 0x6C);
    /// <summary>Review: a soft violet (its own colour, whatever the priority).</summary>
    public static readonly Color ReviewViolet = Color.FromRgb(0x8C, 0x8C, 0xF7);
    /// <summary>Tests: a calm teal between review's violet and done's green.</summary>
    public static readonly Color TestsTeal = Color.FromRgb(0x3F, 0xB6, 0xC4);
    public static readonly Color InProgressOrange = Color.FromRgb(0xF0, 0x8C, 0x3A);
    static readonly Color Low = Color.FromRgb(0x7E, 0x8C, 0x9C);
    /// <summary>Hold: a raspberry red, apart from the work states and from the priority bar on the other edge; striped.</summary>
    public static readonly Color HoldRed = Color.FromRgb(0xE0, 0x5A, 0x7A);

    /// <summary>Done green, review violet, else the priority colour (no priority = low, grey).</summary>
    public static Color StateColor(TaskItem t, string? state = null) =>
        (state ?? t.State) switch { "done" => DoneGreen, "hold" => HoldRed, "review" => ReviewViolet, "tests" => TestsTeal, "started" => InProgressOrange, _ => Low };

    /// <summary>The project's colour, or null for a task without a project.</summary>
    public static Color? ProjectColor(TaskItem t)
    {
        if (t.NotionSource == null) return null;
        // every row asks: the projects of all accounts as a map, made again on each board rebuild (settings may change)
        _projects ??= App.Settings.Accounts.SelectMany(a => a.Projects).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => BoardWindow.ParseColor(g.First().Color), StringComparer.OrdinalIgnoreCase);
        return _projects.TryGetValue(t.NotionSource, out var c) ? c : Color.FromRgb(0x8A, 0x94, 0xA6);
    }

    static System.Collections.Generic.Dictionary<string, Color>? _projects;

    /// <summary>Forget the project colours (the board calls it on every rebuild).</summary>
    public static void ResetCaches() => _projects = null;

    public static string StateName(TaskItem t, string? state = null) => (state ?? t.State) switch
    {
        "done" => L.T("zrobione"), "hold" => "hold", "review" => L.T("review"), "tests" => L.T("testy"), "started" => L.T("w trakcie"), _ => L.T("do zrobienia"),
    };

    /// <summary>Hold's fill: raspberry stripes at 45°.</summary>
    static readonly Brush Stripes = MakeStripes();

    static Brush MakeStripes()
    {
        var pen = new Pen(new SolidColorBrush(HoldRed), 1.6);
        var g = new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 4), new Point(4, 0)));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 3, 3), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        b.Freeze();
        return b;
    }

    /// <summary>How full the bar is: to do 0, in progress ¼, review ½, tests ¾, done 1.</summary>
    public static double Fill(string state) => state switch { "done" => 1, "tests" => 0.75, "review" => 0.5, "started" => 0.25, "hold" => 0.5, _ => 0 };

    /// <summary>
    /// The state bar. <paramref name="width"/> is the bar; the element is a little wider so it is easy to hit.
    /// Planned days of a multi-day task: a dashed outline.
    /// </summary>
    public static FrameworkElement Status(TaskItem t, double width = 5, Action? click = null, bool dashed = false, double gap = 6, string? state = null, bool middle = false)
    {
        state ??= t.State; // a multi-day task's earlier day: the state it had then
        var c = StateColor(t, state);
        bool held = state == "hold";
        // held: as full as the state under it (what it goes back to), the whole bar striped – no work is going on
        double fill = held && state == t.State ? Math.Max(0.25, Fill(t.WorkState)) : Fill(state);
        bool round = App.Settings.StateIcon == "circle";
        var hit = new Grid { Width = (round ? CircleSize : width) + gap, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
        Shape outline;
        if (round) outline = Circle(hit, state, c, held, dashed, middle);
        else
        {
            var bar = new Grid { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1), MinHeight = 8 };
            bar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0.0001, 1 - fill), GridUnitType.Star) });
            bar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0.0001, fill), GridUnitType.Star) });
            double r = Math.Min(1.6, width / 3);
            outline = new Rectangle { Stroke = new SolidColorBrush(c), StrokeThickness = 1.2, RadiusX = r, RadiusY = r };
            if (dashed) outline.StrokeDashArray = new DoubleCollection { 2, 1.5 };
            Grid.SetRowSpan(outline, 2);
            if (fill > 0)
            {
                var part = new Rectangle { Fill = held ? Stripes : new SolidColorBrush(c), RadiusX = r, RadiusY = r, Tag = "fill" };
                Grid.SetRow(part, 1); // from the bottom up
                bar.Children.Add(part);
            }
            bar.Children.Add(outline);
            hit.Children.Add(bar);
        }
        if (click != null)
        {
            hit.Cursor = Cursors.Hand;
            hit.ToolTip = StateName(t) + (t.Priority is { Length: > 0 } pr ? "  ·  " + L.F("Priorytet: {0}", pr) : "") + "\n"
                + (t.Done ? L.T("Kliknij, aby przywrócić") : t.IsHold ? L.F("Wstrzymane (hold) – kliknij, żeby wznowić: {0}", StateName(t, t.WorkState)) : t.IsTesting ? L.T("Kliknij: zrobione") : t.IsReview ? L.T("Kliknij: testy  ·  Shift+klik: zrobione") : t.IsStarted ? L.T("Kliknij: review  ·  Shift+klik: zrobione") : L.T("Kliknij: w trakcie  ·  Shift+klik: od razu zrobione"));
            ToolTipService.SetInitialShowDelay(hit, 300);
            double normal = outline.StrokeThickness;
            hit.MouseEnter += (_, _) => outline.StrokeThickness = normal + 0.8;
            hit.MouseLeave += (_, _) => outline.StrokeThickness = normal;
            hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); };
        }
        return hit;
    }

    /// <summary>The round icon (Settings → Wygląd): its diameter.</summary>
    public const double CircleSize = 12;

    /// <summary>How wide the state mark is with the chosen icon (a bar <paramref name="width"/>, a circle wider): text next to it starts after it.</summary>
    public static double MarkWidth(double width = BarWidth) => App.Settings.StateIcon == "circle" ? CircleSize : width;

    /// <summary>
    /// The state as a round icon (like Linear / Jira): an empty ring to do, a quarter / half / three quarters of the disc in
    /// progress / review / tests, a full disc with a tick done, a ring with ‖ on hold. The shape reads without the colours.
    /// Returns the ring (thicker on hover).
    /// </summary>
    static Shape Circle(Grid host, string state, Color c, bool held, bool dashed, bool middle)
    {
        double d = CircleSize, mid = d / 2;
        // by the first line of text (rows, blocks); in the middle of a one-line bar (the timeline)
        var box = new Grid { Width = d, Height = d, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = middle ? VerticalAlignment.Center : VerticalAlignment.Top, Margin = new Thickness(0, middle ? 0 : 2.5, 0, 0) };
        var brush = new SolidColorBrush(c);
        var ring = new Ellipse { Stroke = brush, StrokeThickness = 1.4 };
        if (dashed) ring.StrokeDashArray = new DoubleCollection { 1.6, 1.4 };
        if (state == "done")
        {
            ring.Fill = brush;
            box.Children.Add(ring);
            box.Children.Add(new Path { Data = Geometry.Parse("M3.4,6.2 L5.2,8 L8.8,4.2"), Stroke = Brushes.White, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Tag = "fill" });
        }
        else if (held)
        {
            box.Children.Add(ring);
            box.Children.Add(new Path { Data = Geometry.Parse("M4.6,3.8 V8.2 M7.4,3.8 V8.2"), Stroke = brush, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Tag = "fill" });
        }
        else
        {
            box.Children.Add(ring);
            double f = Fill(state);
            if (f > 0)
            {
                // a slice of the disc inside the ring, clockwise from the top
                double r = mid - 2.6, a = f * 2 * Math.PI;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(mid, mid), true, true);
                    ctx.LineTo(new Point(mid, mid - r), false, false);
                    ctx.ArcTo(new Point(mid + r * Math.Sin(a), mid - r * Math.Cos(a)), new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, false, false);
                }
                g.Freeze();
                box.Children.Add(new Path { Data = g, Fill = brush, Tag = "fill" });
            }
        }
        host.Children.Add(box);
        return ring;
    }

    /// <summary>
    /// The marks on the right edge, side by side: the priority (red high, amber medium, slate low; none = no bar) and the
    /// project's colour (outermost). Null when the task has neither.
    /// </summary>
    /// <summary>One width for all the bars (state, priority, project): they read as one set.</summary>
    public const double BarWidth = 4;

    public static FrameworkElement? Right(TaskItem t, double width = BarWidth)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch };
        Border Bar(Color c, string tip)
        {
            var b = new Border { Width = width, CornerRadius = new CornerRadius(Math.Min(1.6, width / 3)), Margin = new Thickness(2, 1, 0, 1), Background = new SolidColorBrush(c), ToolTip = tip };
            ToolTipService.SetInitialShowDelay(b, 300);
            return b;
        }
        if (!string.IsNullOrEmpty(t.Priority)) panel.Children.Add(Bar(BoardWindow.PriorityColor(t.Priority), L.F("Priorytet: {0}", t.Priority)));
        if (ProjectColor(t) is { } pc) panel.Children.Add(Bar(pc, t.NotionSource!));
        return panel.Children.Count > 0 ? panel : null;
    }
}
