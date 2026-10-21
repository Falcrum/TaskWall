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
    public static readonly Color InProgressOrange = Color.FromRgb(0xF0, 0x8C, 0x3A);
    static readonly Color Low = Color.FromRgb(0x7E, 0x8C, 0x9C);

    /// <summary>Done green, review violet, else the priority colour (no priority = low, grey).</summary>
    public static Color StateColor(TaskItem t, string? state = null) =>
        (state ?? t.State) switch { "done" => DoneGreen, "review" => ReviewViolet, "started" => InProgressOrange, _ => Low };

    /// <summary>The project's colour, or null for a task without a project.</summary>
    public static Color? ProjectColor(TaskItem t) =>
        t.NotionSource == null ? null
        : App.Settings.Accounts.SelectMany(a => a.Projects).FirstOrDefault(p => string.Equals(p.Name, t.NotionSource, StringComparison.OrdinalIgnoreCase)) is { } p
            ? BoardWindow.ParseColor(p.Color) : Color.FromRgb(0x8A, 0x94, 0xA6);

    public static string StateName(TaskItem t, string? state = null) => (state ?? t.State) switch
    {
        "done" => L.T("zrobione"), "review" => L.T("review"), "started" => L.T("w trakcie"), _ => L.T("do zrobienia"),
    };

    /// <summary>How full the bar is: to do 0, in progress ⅓, review ⅔, done 1.</summary>
    public static double Fill(string state) => state switch { "done" => 1, "review" => 2.0 / 3, "started" => 1.0 / 3, _ => 0 };

    /// <summary>
    /// The state bar. <paramref name="width"/> is the bar; the element is a little wider so it is easy to hit.
    /// Planned days of a multi-day task: a dashed outline.
    /// </summary>
    public static FrameworkElement Status(TaskItem t, double width = 5, Action? click = null, bool dashed = false, double gap = 6, string? state = null)
    {
        state ??= t.State; // a multi-day task's earlier day: the state it had then
        var c = StateColor(t, state);
        double fill = Fill(state);
        var hit = new Grid { Width = width + gap, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
        var bar = new Grid { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1), MinHeight = 8 };
        bar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0.0001, 1 - fill), GridUnitType.Star) });
        bar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(0.0001, fill), GridUnitType.Star) });
        double r = Math.Min(1.6, width / 3);
        var outline = new Rectangle { Stroke = new SolidColorBrush(c), StrokeThickness = 1.2, RadiusX = r, RadiusY = r };
        if (dashed) outline.StrokeDashArray = new DoubleCollection { 2, 1.5 };
        Grid.SetRowSpan(outline, 2);
        if (fill > 0)
        {
            var part = new Rectangle { Fill = new SolidColorBrush(c), RadiusX = r, RadiusY = r, Tag = "fill" };
            Grid.SetRow(part, 1); // from the bottom up
            bar.Children.Add(part);
        }
        bar.Children.Add(outline);
        hit.Children.Add(bar);
        if (click != null)
        {
            hit.Cursor = Cursors.Hand;
            hit.ToolTip = StateName(t) + (t.Priority is { Length: > 0 } pr ? "  ·  " + L.F("Priorytet: {0}", pr) : "") + "\n"
                + (t.Done ? L.T("Kliknij, aby przywrócić") : t.IsReview ? L.T("Kliknij: zrobione") : t.IsStarted ? L.T("Kliknij: review  ·  Shift+klik: zrobione") : L.T("Kliknij: w trakcie  ·  Shift+klik: od razu zrobione"));
            ToolTipService.SetInitialShowDelay(hit, 300);
            hit.MouseEnter += (_, _) => outline.StrokeThickness = 2;
            hit.MouseLeave += (_, _) => outline.StrokeThickness = 1.2;
            hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); };
        }
        return hit;
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
