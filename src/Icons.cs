using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TaskWall;

/// <summary>
/// Vector icons of the top bar (24×24 paths in a Viewbox – sharp at any scale / DPI). Lines and fills take the
/// Foreground of the button they sit in, so hover / active states recolour them.
/// </summary>
static class Icons
{
    const double Stroke = 1.6;

    // calendar sheet shared by the view icons: body, header line, two rings
    const string Sheet = "M5.5,5 H18.5 A2.5,2.5 0 0 1 21,7.5 V18.5 A2.5,2.5 0 0 1 18.5,21 H5.5 A2.5,2.5 0 0 1 3,18.5 V7.5 A2.5,2.5 0 0 1 5.5,5 Z M3,9.5 H21 M8,3 V6.5 M16,3 V6.5";

    static Binding Fg() => new("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) };

    static Path Line(string data, double width = Stroke)
    {
        var p = new Path { Data = Geometry.Parse(data), StrokeThickness = width, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        p.SetBinding(Shape.StrokeProperty, Fg());
        return p;
    }

    static Path Fill(string data)
    {
        var p = new Path { Data = Geometry.Parse(data) };
        p.SetBinding(Shape.FillProperty, Fg());
        return p;
    }

    static FrameworkElement Box(double size, params UIElement[] parts)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var p in parts) canvas.Children.Add(p);
        return new Viewbox { Width = size, Height = size, Child = canvas, SnapsToDevicePixels = true };
    }

    /// <summary>Small filled cells: <paramref name="rows"/> × <paramref name="cols"/> inside the sheet.</summary>
    static string Cells(int rows, int cols, double top, double cellH, double gapY)
    {
        double left = 5.4, width = 13.2, gap = 0.7;
        double w = (width - gap * (cols - 1)) / cols;
        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double x = left + c * (w + gap), y = top + r * (cellH + gapY);
                sb.Append(System.FormattableString.Invariant($"M{x:0.##},{y:0.##} h{w:0.##} v{cellH:0.##} h{-w:0.##} Z "));
            }
        return sb.ToString();
    }

    /// <summary>Calendar sheet with a pointer arrow in the middle ("go to today") – readable from a distance.</summary>
    public static FrameworkElement Today(double s = 19) => Box(s, Line(Sheet),
        Fill("M9.8,11.3 L9.8,18.3 L11.6,16.6 L13,19.4 L14.4,18.7 L13,15.9 L15.6,15.7 Z")); // equal gap above and below

    /// <summary>Alarm clock (alarms and meetings).</summary>
    public static FrameworkElement Alarm(double s = 19) => Box(s,
        Line("M12,6 a7.2,7.2 0 1 0 0.01,0 Z M12,9.2 V13.4 L14.8,15 M3.6,6.4 L6.4,3.6 M20.4,6.4 L17.6,3.6 M7.2,19.6 L5.6,21.2 M16.8,19.6 L18.4,21.2"));

    public static FrameworkElement Day(double s = 19) => Box(s, Line(Sheet), Fill("M8,12.5 H16 V18 H8 Z"));
    public static FrameworkElement Week(double s = 19) => Box(s, Line(Sheet), Fill(Cells(1, 5, 13.6, 2.6, 0)));
    public static FrameworkElement TwoWeeks(double s = 19) => Box(s, Line(Sheet), Fill(Cells(2, 5, 12.3, 2.3, 1.0)));
    public static FrameworkElement Month(double s = 19) => Box(s, Line(Sheet), Fill(Cells(4, 5, 11.2, 1.55, 0.6)));

    public static FrameworkElement Year(double s = 19)
    {
        var text = new TextBlock { Text = "365", FontSize = 7.6, FontWeight = FontWeights.Bold, Width = 18, TextAlignment = TextAlignment.Center };
        text.SetBinding(TextBlock.ForegroundProperty, Fg());
        Canvas.SetLeft(text, 3);
        Canvas.SetTop(text, 10.4);
        return Box(s, Line(Sheet), text);
    }

    /// <summary>‹ and › of the top bar.</summary>
    public static FrameworkElement Chevron(bool right, double s = 14) => Box(s, Line(right ? "M9.5,5 L16.5,12 L9.5,19" : "M14.5,5 L7.5,12 L14.5,19", 2));

    /// <summary>"More": three dots.</summary>
    public static FrameworkElement More(double s = 18) => Box(s, Fill("M4,10.4 a1.6,1.6 0 1 0 0.01,0 Z M12,10.4 a1.6,1.6 0 1 0 0.01,0 Z M20,10.4 a1.6,1.6 0 1 0 0.01,0 Z"));

    /// <summary>The board over the whole screen (a frame) / back to its size (two frames).</summary>
    public static FrameworkElement Maximize(bool on, double s = 15) =>
        Box(s, Line(on ? "M8.5,8.5 H19.5 V19.5 H8.5 Z M5,15.5 V4.5 H16" : "M4.5,4.5 H19.5 V19.5 H4.5 Z", 1.7));

    /// <summary>The top bar's load rings: what is measured, drawn in the ring's colour.</summary>
    public static FrameworkElement Meter(string kind, Brush brush, double s = 12)
    {
        var data = kind switch
        {
            "cpu" => "M7,7 H17 V17 H7 Z M10.3,10.3 H13.7 V13.7 H10.3 Z M9.7,3.5 V7 M14.3,3.5 V7 M9.7,17 V20.5 M14.3,17 V20.5 M3.5,9.7 H7 M3.5,14.3 H7 M17,9.7 H20.5 M17,14.3 H20.5",
            "gpu" => "M2.5,6.5 H21.5 V16.5 H2.5 Z M6,11.5 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M15,9.5 H18.5 M15,13.5 H18.5 M5.5,16.5 V19.5 M10,16.5 V19.5",
            "ram" => "M2.5,7 H21.5 V15.5 H2.5 Z M6.5,9.8 V12.7 M10.5,9.8 V12.7 M14.5,9.8 V12.7 M18,9.8 V12.7 M5,15.5 V18.5 M19,15.5 V18.5",
            "disk" => "M4,5.5 H20 V18.5 H4 Z M4,13.5 H20 M16.3,16 h0.01",
            _ => "M8,19 V5 M4.5,8.5 L8,5 L11.5,8.5 M16,5 V19 M12.5,15.5 L16,19 L19.5,15.5",
        };
        var p = new Path { Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(p);
        return new Viewbox { Width = s, Height = s, Child = canvas, SnapsToDevicePixels = true, IsHitTestVisible = false };
    }

    /// <summary>Hide the board: a line at the bottom (minimise).</summary>
    public static FrameworkElement Minimize(double s = 16) => Box(s, Line("M5,17 H19", 1.9));

    /// <summary>Zoom in / out (the timeline): a magnifier with + or −.</summary>
    public static FrameworkElement Zoom(bool zoomIn, double s = 16) => Box(s, Line("M10.5,4 a6.5,6.5 0 1 0 0.01,0 Z M15.3,15.3 L21,21 M7.5,10.5 H13.5" + (zoomIn ? " M10.5,7.5 V13.5" : ""), 1.8));

    /// <summary>Timeline: bars of different lengths, staggered, like a Gantt chart.</summary>
    public static FrameworkElement Timeline(double s = 19) => Box(s,
        Line("M3.5,4.5 V19.5 M3.5,19.5 H21", 1.4),
        Fill("M6,6 H13.5 A1.2,1.2 0 0 1 13.5,8.4 H6 A1.2,1.2 0 0 1 6,6 Z M9.5,10.4 H19 A1.2,1.2 0 0 1 19,12.8 H9.5 A1.2,1.2 0 0 1 9.5,10.4 Z M7.5,14.8 H14.5 A1.2,1.2 0 0 1 14.5,17.2 H7.5 A1.2,1.2 0 0 1 7.5,14.8 Z"));

    public static FrameworkElement Search(double s = 18) => Box(s, Line("M10.5,4 a6.5,6.5 0 1 0 0.01,0 Z M15.3,15.3 L21,21", 1.9));

    /// <summary>Eraser over a line (like the reference).</summary>
    public static FrameworkElement Clear(double s = 19) => Box(s,
        Line("M14.6,3.6 L20.4,9.4 L11.3,18.5 H7.1 L3.6,15 Z M9.4,8.8 L15.2,14.6 M11.3,18.5 H20.5"));

    /// <summary>Stack of sheets with a "back" arrow (the backlog).</summary>
    public static FrameworkElement Backlog(double s = 19) => Box(s,
        Line("M11,3.5 L18.5,7.2 L11,10.9 L3.5,7.2 Z M3.5,11 L11,14.7 L14,13.2 M3.5,14.8 L11,18.5 L12.4,17.8"),
        Line("M21,17.2 a3.2,3.2 0 1 1 -3.2,-3.2 H16.6 M18.4,12.4 L16.6,14 L18.4,15.6"));

    /// <summary>Box with a lid and an arrow down (the archive).</summary>
    public static FrameworkElement Archive(double s = 19) => Box(s,
        Line("M3,4.5 H21 V8.5 H3 Z M4.5,8.5 V19 A1.5,1.5 0 0 0 6,20.5 H18 A1.5,1.5 0 0 0 19.5,19 V8.5 M12,11 V17.2 M9.4,14.8 L12,17.4 L14.6,14.8"));

    /// <summary>Layers (accounts and, later, more layers): two overlapping sheets, the front one tinted.</summary>
    public static FrameworkElement Layers(double s = 19)
    {
        var front = new Path { Data = Geometry.Parse("M10.5,9.5 H19 A1.8,1.8 0 0 1 20.8,11.3 V19 A1.8,1.8 0 0 1 19,20.8 H10.5 A1.8,1.8 0 0 1 8.7,19 V11.3 A1.8,1.8 0 0 1 10.5,9.5 Z") };
        front.SetBinding(Shape.FillProperty, Fg());
        front.Opacity = 0.35;
        return Box(s,
            Line("M5,3.2 H13.5 A1.8,1.8 0 0 1 15.3,5 V9.5 M3.2,12.7 V5 A1.8,1.8 0 0 1 5,3.2 M3.2,12.7 A1.8,1.8 0 0 0 5,14.5 H8.7"),
            front,
            Line("M10.5,9.5 H19 A1.8,1.8 0 0 1 20.8,11.3 V19 A1.8,1.8 0 0 1 19,20.8 H10.5 A1.8,1.8 0 0 1 8.7,19 V11.3 A1.8,1.8 0 0 1 10.5,9.5 Z"));
    }

    /// <summary>Two arrows in a circle (sync); <paramref name="spinner"/> is what rotates while syncing.</summary>
    public static FrameworkElement Sync(out FrameworkElement spinner, double s = 18)
    {
        var canvas = new Canvas { Width = 24, Height = 24, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform() };
        canvas.Children.Add(Line("M19.5,10 A7.8,7.8 0 0 0 5.6,7.4 M4.5,14 A7.8,7.8 0 0 0 18.4,16.6"));
        canvas.Children.Add(Fill("M20.6,4.6 L20.4,10.8 L14.6,9.4 Z"));
        canvas.Children.Add(Fill("M3.4,19.4 L3.6,13.2 L9.4,14.6 Z"));
        spinner = canvas;
        return new Viewbox { Width = s, Height = s, Child = canvas, SnapsToDevicePixels = true };
    }

    /// <summary>Wind: three gusts (for the weather line; plain brush, used inside text).</summary>
    public static FrameworkElement Wind(Brush brush, double s = 11)
    {
        var p = new Path
        {
            Data = Geometry.Parse("M3,8.5 H14.5 A2.8,2.8 0 1 0 11.7,5.7 M3,12.5 H18.2 A2.8,2.8 0 1 1 15.4,15.3 M3,16.5 H10"),
            Stroke = brush, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(p);
        return new Viewbox { Width = s, Height = s, Child = canvas };
    }

    /// <summary>Icon with a round counter badge in the top-right corner (backlog / archive).</summary>
    public static FrameworkElement WithBadge(FrameworkElement icon, out TextBlock count)
    {
        var grid = new Grid();
        grid.Children.Add(new Border { Child = icon, Padding = new Thickness(0, 3, 7, 0) });
        count = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };
        count.SetBinding(TextBlock.ForegroundProperty, Fg());
        var badge = new Border
        {
            MinWidth = 13, Height = 13, CornerRadius = new CornerRadius(6.5), BorderThickness = new Thickness(1.1), Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Child = count,
            Background = new SolidColorBrush(Th.Panel(0xE0)),
        };
        badge.SetBinding(Border.BorderBrushProperty, Fg());
        grid.Children.Add(badge);
        return grid;
    }

    /// <summary>Icon + optional caption next to it (Settings → Wygląd → captions on the bar).</summary>
    public static FrameworkElement Labeled(FrameworkElement icon, string caption, bool showCaption)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        if (showCaption)
            sp.Children.Add(new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        return sp;
    }
}
