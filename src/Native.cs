using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskWall;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const uint GW_HWNDPREV = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ----- dark title bar for normal windows -----
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = Th.Light ? 0 : 1; // the window frame follows the app theme
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    /// <summary>Windows 11: title bar and frame in the dialog's own colour (no separate grey bar), rounded corners.</summary>
    public static void TintFrame(IntPtr hwnd, System.Windows.Media.Color c)
    {
        int rgb = c.R | (c.G << 8) | (c.B << 16);
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref rgb, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref rgb, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    // ----- foreground tracking (for "show desktop") -----
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
}
