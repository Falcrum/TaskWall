using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskWall;

/// <summary>
/// Notification-area icon on plain Shell_NotifyIcon (no WinForms – saves ~18 MB).
/// Left click → <see cref="Clicked"/>, right click → dark WPF menu from <see cref="BuildMenu"/>.
/// </summary>
sealed class TrayIcon : IDisposable
{
    const int WM_TRAY = 0x8000 + 0x21, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[]? bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);

    readonly HwndSource _window;
    readonly IntPtr _icon;
    readonly int _taskbarCreated;
    NOTIFYICONDATA _data;

    public event Action? Clicked;
    public Func<ContextMenu>? BuildMenu { get; set; }

    public TrayIcon(string tooltip)
    {
        _window = new HwndSource(new HwndSourceParameters("TaskWallTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(Proc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = MakeIcon();
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
            szTip = tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
        Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public void Balloon(string title, string text)
    {
        var d = _data;
        d.uFlags = NIF_INFO;
        d.szInfoTitle = title;
        d.szInfo = text;
        d.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    IntPtr Proc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev == WM_LBUTTONUP) Clicked?.Invoke();
            else if (ev == WM_RBUTTONUP && BuildMenu != null)
                // after the click has fully finished – otherwise the same mouse-up closes the fresh menu
                Application.Current.Dispatcher.BeginInvoke(ShowMenu, System.Windows.Threading.DispatcherPriority.Input);
            handled = true;
        }
        else if (msg == _taskbarCreated) Shell_NotifyIcon(NIM_ADD, ref _data); // Explorer restarted
        return IntPtr.Zero;
    }

    Window? _anchor;
    ContextMenu? _menu;

    /// <summary>
    /// The menu hangs on an invisible 1×1 window that takes the foreground: the menu then stays open
    /// until a click elsewhere (a menu without an active owner window closes right away).
    /// </summary>
    void ShowMenu()
    {
        if (_menu?.IsOpen == true) { _menu.IsOpen = false; return; }
        _anchor ??= new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            Topmost = true, Width = 1, Height = 1, ResizeMode = ResizeMode.NoResize, ShowActivated = true,
        };
        GetCursorPos(out var p);
        _anchor.Show();
        // cursor pixels → this window's DIPs (the tray's monitor may have another scale than the board's)
        var m = PresentationSource.FromVisual(_anchor)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var pt = m.Transform(new Point(p.X, p.Y));
        _anchor.Left = pt.X;
        _anchor.Top = pt.Y;
        var hwnd = new WindowInteropHelper(_anchor).Handle;
        SetForegroundWindow(hwnd);
        _anchor.Activate();

        _menu = BuildMenu!();
        _menu.PlacementTarget = _anchor;
        _menu.Placement = PlacementMode.Top;
        _menu.Closed += (_, _) => _anchor?.Hide();
        _menu.IsOpen = true;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    /// <summary>32×32 icon: rounded accent square with a 2×3 grid, drawn with WPF.</summary>
    static IntPtr MakeIcon()
    {
        const int size = 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF)), null, new Rect(1, 1, 30, 30), 8, 8);
            var fg = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 3; col++)
                    dc.DrawRectangle(fg, null, new Rect(7 + col * 7, 9 + row * 8, 5, 6));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[size * size * 4];
        rtb.CopyPixels(px, size * 4, 0);
        var color = CreateBitmap(size, size, 1, 32, px);
        var mask = CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        DestroyIcon(_icon);
        _window.Dispose();
    }
}
