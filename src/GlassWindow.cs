using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskWall;

/// <summary>
/// Borderless frosted-glass window that lives on the desktop: it stays at the bottom of the
/// z-order (under normal windows), stays out of the taskbar / Alt+Tab, and pops above the
/// desktop when "Show desktop" (Win+D) raises the desktop over it.
/// Layers: blurred wallpaper (Glass) → tint → scaled body, all clipped to rounded corners.
/// </summary>
public class GlassWindow : Window
{
    public static bool DevTopmost { get; } = Environment.GetCommandLineArgs().Contains("--topmost");

    static readonly List<GlassWindow> All = new();
    static Native.WinEventProc? _hookProc; // keep the delegate alive
    static bool _desktopMode, _peek;
    public static event Action? PeekEnded;

    /// <summary>Corner radius in window DIPs.</summary>
    public double Radius { get; set; } = 22;
    public bool SquareTopCorners { get; set; }
    /// <summary>Keep this window directly above another glass window (clock above board).</summary>
    public GlassWindow? KeepAbove { get; set; }

    /// <summary>Rounded panel holding glass + tint + body. Its size may be smaller than the window (clock).</summary>
    protected readonly Grid Frame = new();
    readonly Border _glass = new();
    readonly Border _tint = new();
    readonly Border _bodyHost = new();
    readonly Grid _shell = new();

    public IntPtr Hwnd { get; private set; }
    public FrameworkElement FrameElement => Frame;

    public GlassWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Topmost = DevTopmost;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => All.Remove(this);
        DpiChanged += (_, _) => UpdateRegion();

        _tint.SetResourceReference(Border.BackgroundProperty, "GlassTint");
        _tint.BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        _tint.BorderThickness = new Thickness(1);
        Frame.Children.Add(_glass);
        Frame.Children.Add(_tint);
        Frame.Children.Add(_bodyHost);
        Frame.SizeChanged += (_, _) => UpdateClip();
        _shell.Children.Add(Frame);
        Content = _shell;
        ApplyCorners();
    }

    /// <summary>Moves XAML-declared content into the glass frame.</summary>
    protected void AdoptXamlContent()
    {
        if (Content is FrameworkElement body && !ReferenceEquals(body, _shell))
        {
            Content = _shell;
            Body = body;
        }
    }

    public FrameworkElement? Body
    {
        get => _bodyHost.Child as FrameworkElement;
        set => _bodyHost.Child = value;
    }

    public void ApplyScale(double scale)
    {
        _bodyHost.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        ApplyCorners();
    }

    void ApplyCorners()
    {
        var r = SquareTopCorners ? new CornerRadius(0, 0, Radius, Radius) : new CornerRadius(Radius);
        _glass.CornerRadius = r;
        _tint.CornerRadius = r;
        _tint.BorderThickness = SquareTopCorners ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        UpdateClip();
    }

    void UpdateClip()
    {
        double w = Frame.ActualWidth, h = Frame.ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (SquareTopCorners)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double r = Math.Min(Radius, Math.Min(w, h) / 2);
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(w, 0), false, false);
                c.LineTo(new Point(w, h - r), false, false);
                c.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(r, h), false, false);
                c.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            }
            g.Freeze();
            _bodyHost.Clip = g;
        }
        else _bodyHost.Clip = new RectangleGeometry(new Rect(0, 0, w, h), Radius, Radius);
    }

    /// <summary>
    /// Opaque windows are rendered by the GPU like any normal window (no per-frame copy of the whole
    /// bitmap that per-pixel transparent windows need) – much cheaper for the big board. The rounded
    /// corners are then cut with a window region and filled with the sharp wallpaper, so they look
    /// exactly like transparency.
    /// </summary>
    public bool Opaque { get; private set; }

    protected void MakeOpaque()
    {
        Opaque = true;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x14));
    }

    static ImageBrush Brush(BitmapSource bmp)
    {
        var b = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        RenderOptions.SetCachingHint(b, CachingHint.Cache);
        b.Freeze();
        return b;
    }

    /// <summary>Frosted glass (null = plain tint), anchored to the window's top-left corner.</summary>
    public void SetGlass(BitmapSource? glass) => _glass.Background = glass != null ? Brush(glass) : null;

    readonly System.Windows.Controls.Image[] _corners = new System.Windows.Controls.Image[4];

    /// <summary>
    /// Opaque windows: sharp wallpaper squares behind the rounded corners (TL, TR, BL, BR), so the
    /// cut-off corners look exactly like the desktop behind them.
    /// </summary>
    public void SetCorners(BitmapSource?[] patches, double sizeDip)
    {
        for (int i = 0; i < 4; i++)
        {
            if (_corners[i] == null)
            {
                _corners[i] = new System.Windows.Controls.Image
                {
                    Stretch = Stretch.Fill,
                    HorizontalAlignment = i % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    VerticalAlignment = i < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                    IsHitTestVisible = false,
                };
                _shell.Children.Insert(0, _corners[i]);
            }
            _corners[i].Source = patches[i];
            _corners[i].Width = _corners[i].Height = sizeDip;
        }
    }

    void UpdateRegion()
    {
        if (!Opaque || Hwnd == IntPtr.Zero) return;
        var r = PhysicalRect;
        if (r.Width <= 0) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int d = (int)Math.Round(Radius * 2 * scale);
        Native.SetWindowRgn(Hwnd, Native.CreateRoundRectRgn(0, 0, r.Width + 1, r.Height + 1, d, d), true);
    }

    public ImageBrush? GlassBrush => _glass.Background as ImageBrush;

    /// <summary>Window rectangle in physical pixels.</summary>
    public Int32Rect PhysicalRect
    {
        get
        {
            if (Hwnd == IntPtr.Zero || !GetWindowRect(Hwnd, out var r)) return Int32Rect.Empty;
            return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>Position/size in physical pixels on a monitor with the given DPI scale.</summary>
    public void PlacePhysical(int x, int y, int w, int h, double scale)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        // 1) move onto the target monitor first, so a DPI change (and WPF's own rescale) happens before sizing
        Native.SetWindowPos(helper.Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
        // 2) then size in DIPs of that monitor and pin the exact physical rectangle
        Width = w / scale;
        Height = h / scale;
        Native.SetWindowPos(helper.Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        UpdateRegion();
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        Hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(Hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(Hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
        HwndSource.FromHwnd(Hwnd)?.AddHook(WndProc);
        All.Add(this);
        EnsureForegroundHook();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING && !Topmost)
        {
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            var target = Native.HWND_BOTTOM;
            bool keep = false;
            if (KeepAbove != null && KeepAbove.Hwnd != IntPtr.Zero)
            {
                var prev = Native.GetWindow(KeepAbove.Hwnd, Native.GW_HWNDPREV);
                if (prev == Hwnd) keep = true;          // already right above
                else target = prev == IntPtr.Zero ? Native.HWND_TOP : prev;
            }
            if (keep) wp.flags |= Native.SWP_NOZORDER;
            else
            {
                wp.hwndInsertAfter = target;
                wp.flags &= ~Native.SWP_NOZORDER;
            }
            Marshal.StructureToPtr(wp, lParam, false);
        }
        return IntPtr.Zero;
    }

    // ---------- "Show desktop" + quick peek ----------

    static void EnsureForegroundHook()
    {
        if (_hookProc != null) return;
        _hookProc = OnForeground;
        Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    static void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (DevTopmost || hwnd == IntPtr.Zero || All.Count == 0) return;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId) return; // our own windows, menus, popups

        if (_peek) { Peek(false); return; }

        var cls = Native.ClassName(hwnd);
        bool isDesktop = cls is "WorkerW" or "Progman";
        if (isDesktop)
        {
            // Plain click on the desktop: desktop stays below us. Win+D: desktop is raised above us.
            if (IsAbove(hwnd, All[0].Hwnd)) SetDesktopMode(true);
        }
        else if (_desktopMode) SetDesktopMode(false);
    }

    static bool IsAbove(IntPtr candidate, IntPtr ours)
    {
        var h = ours;
        for (int i = 0; i < 10000 && h != IntPtr.Zero; i++)
        {
            h = Native.GetWindow(h, Native.GW_HWNDPREV);
            if (h == candidate) return true;
        }
        return false;
    }

    static void SetDesktopMode(bool on)
    {
        _desktopMode = on;
        SetAllTopmost(on);
    }

    static void SetAllTopmost(bool on)
    {
        foreach (var w in All.ToList())
            if (w.IsVisible) w.Topmost = on || DevTopmost; // WndProc pushes it back to the bottom once Topmost is cleared
    }

    /// <summary>Bring the desktop windows above everything (global hotkey) until focus goes elsewhere.</summary>
    public static void Peek(bool on)
    {
        if (_peek == on) return;
        _peek = on;
        SetAllTopmost(on || _desktopMode);
        if (!on) PeekEnded?.Invoke();
    }

    public static bool IsPeeking => _peek;

    /// <summary>Drops the topmost modes before a dialog opens, so the dialog isn't hidden under the board.</summary>
    public static void EndOverlays()
    {
        if (_peek) Peek(false);
        if (_desktopMode) SetDesktopMode(false);
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open " + url, ex); }
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
}
