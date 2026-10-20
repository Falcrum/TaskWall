using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TaskWall;

/// <summary>
/// The two marks every task carries, the same in every view:
/// on the left a narrow bar over the task's whole height – empty before it starts, half filled while in progress, full
/// when done; grey / amber / red for low / medium / high priority, green when done;
/// on the right a thin bar in the colour of its project (FoA, Tuathan …).
/// </summary>
static class TaskMarks
{
    public static readonly Color DoneGreen = Color.FromRgb(0x4C, 0xB8, 0x6C);
    static readonly Color Low = Color.FromRgb(0x7E, 0x8C, 0x9C);

    /// <summary>Done green, else the priority colour (no priority = low, grey).</summary>
    public static Color StateColor(TaskItem t) =>
        t.Done ? DoneGreen : BoardWindow.PriorityLevel(t.Priority) switch { 3 => BoardWindow.PriorityColor("high"), 2 => BoardWindow.PriorityColor("medium"), _ => Low };

    /// <summary>The project's colour, or null for a task without a project.</summary>
    public static Color? ProjectColor(TaskItem t) =>
        t.NotionSource == null ? null
        : App.Settings.Accounts.SelectMany(a => a.Projects).FirstOrDefault(p => string.Equals(p.Name, t.NotionSource, StringComparison.OrdinalIgnoreCase)) is { } p
            ? BoardWindow.ParseColor(p.Color) : Color.FromRgb(0x8A, 0x94, 0xA6);

    public static string StateName(TaskItem t) => t.Done ? L.T("zrobione") : t.IsStarted ? L.T("w trakcie") : L.T("do zrobienia");

    /// <summary>
    /// The state bar. <paramref name="width"/> is the bar; the element is a little wider so it is easy to hit.
    /// Planned days of a multi-day task: a dashed outline.
    /// </summary>
    public static FrameworkElement Status(TaskItem t, double width = 5, Action? click = null, bool dashed = false, double gap = 6)
    {
        var c = StateColor(t);
        var hit = new Grid { Width = width + gap, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
        var bar = new Grid { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1), MinHeight = 8 };
        bar.RowDefinitions.Add(new RowDefinition());
        bar.RowDefinitions.Add(new RowDefinition());
        double r = Math.Min(1.6, width / 3);
        var outline = new Rectangle { Stroke = new SolidColorBrush(c), StrokeThickness = 1.2, RadiusX = r, RadiusY = r };
        if (dashed) outline.StrokeDashArray = new DoubleCollection { 2, 1.5 };
        Grid.SetRowSpan(outline, 2);
        if (t.Done || t.IsStarted)
        {
            var fill = new Rectangle { Fill = new SolidColorBrush(c), RadiusX = r, RadiusY = r };
            if (t.Done) Grid.SetRowSpan(fill, 2); else Grid.SetRow(fill, 1);
            bar.Children.Add(fill);
        }
        bar.Children.Add(outline);
        hit.Children.Add(bar);
        if (click != null)
        {
            hit.Cursor = Cursors.Hand;
            hit.ToolTip = StateName(t) + (t.Priority is { Length: > 0 } pr ? "  ·  " + L.F("Priorytet: {0}", pr) : "") + "\n"
                + (t.Done ? L.T("Kliknij, aby przywrócić") : t.IsStarted ? L.T("Kliknij: zrobione") : L.T("Kliknij: w trakcie  ·  Shift+klik: od razu zrobione"));
            ToolTipService.SetInitialShowDelay(hit, 300);
            hit.MouseEnter += (_, _) => outline.StrokeThickness = 2;
            hit.MouseLeave += (_, _) => outline.StrokeThickness = 1.2;
            hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); };
        }
        return hit;
    }

    /// <summary>The project bar (thin, right edge); null without a project.</summary>
    public static FrameworkElement? Project(TaskItem t, double width = 2.5)
    {
        if (ProjectColor(t) is not { } c) return null;
        var b = new Border
        {
            Width = width, CornerRadius = new CornerRadius(width / 2), Margin = new Thickness(0, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(c), ToolTip = t.NotionSource,
        };
        ToolTipService.SetInitialShowDelay(b, 300);
        return b;
    }
}
