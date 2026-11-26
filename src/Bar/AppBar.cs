using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TaskWall;

/// <summary>
/// A window registered with Windows as an application desktop toolbar (like the taskbar): it reserves a strip of the
/// screen – the work area shrinks, maximised windows stop at it – and hears when a full-screen program comes up on its
/// monitor. <see cref="Remove"/> gives the strip back; the guard does the same for a bar whose process died.
/// </summary>
sealed class AppBar
{
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
    const uint ABE_TOP = 1, ABE_BOTTOM = 3;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    const int WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);

    static readonly uint Callback = RegisterWindowMessage("TaskWall.AppBar");

    /// <summary>
    /// When one of TaskWall's own bars last set or gave back its strip: Windows tells every other bar about it (ABN_POSCHANGED), and
    /// with a bar on each monitor they would ask for their strips again and tell each other back – without end. Their own moves are
    /// not news to them.
    /// </summary>
    static long _ownMoveAt;
    /// <summary>TaskWall's bars registered now: with one alone (the usual case) every notice counts, as before.</summary>
    static int _registeredCount;
    static bool OwnMoveJustNow => _registeredCount > 1 && Environment.TickCount64 - _ownMoveAt < 500;

    readonly IntPtr _hwnd;
    bool _registered;
    PxRect? _set; // the strip asked for last (SETPOS again only when it changes)
    bool _setTop;

    /// <summary>Another bar moved (the taskbar resized, a monitor changed): the strip should be asked for again.</summary>
    public event Action? PositionChanged;
    /// <summary>A full-screen program came up on the bar's monitor (true) or went away (false).</summary>
    public event Action<bool>? FullscreenChanged;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    public bool Registered => _registered;

    /// <summary>
    /// Reserves the strip at the top or the bottom of <paramref name="monitor"/> (physical pixels) and returns where the bar
    /// window goes – Windows may move it out of another bar's way (the taskbar at the same edge).
    /// </summary>
    public PxRect Dock(PxRect monitor, bool top, int height)
    {
        var want = BarGeometry.Strip(monitor, top, height);
        if (_registered && _set == want && _setTop == top && _granted is { } same) return same;
        if (!_registered)
        {
            var reg = Data();
            reg.uCallbackMessage = Callback;
            SetRegistered(SHAppBarMessage(ABM_NEW, ref reg) != UIntPtr.Zero);
            if (!_registered) Log.Error("appbar: Windows refused ABM_NEW");
        }
        var d = Data();
        d.uEdge = top ? ABE_TOP : ABE_BOTTOM;
        // propose the whole monitor on that edge: Windows cuts off what other bars (the taskbar) hold
        d.rc = new RECT { Left = monitor.X, Top = top ? monitor.Y : monitor.Bottom - height, Right = monitor.Right, Bottom = top ? monitor.Y + height : monitor.Bottom };
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        var granted = BarGeometry.AfterQuery(new PxRect(d.rc.Left, d.rc.Top, d.rc.Right - d.rc.Left, d.rc.Bottom - d.rc.Top), top, height);
        d.rc = new RECT { Left = granted.X, Top = granted.Y, Right = granted.Right, Bottom = granted.Bottom };
        _ownMoveAt = Environment.TickCount64;
        SHAppBarMessage(ABM_SETPOS, ref d);
        _ownMoveAt = Environment.TickCount64;
        _set = want;
        _setTop = top;
        _granted = new PxRect(d.rc.Left, d.rc.Top, d.rc.Right - d.rc.Left, d.rc.Bottom - d.rc.Top);
        return _granted.Value;
    }

    PxRect? _granted;

    /// <summary>Explorer restarted and forgot every bar: the next <see cref="Dock"/> registers again.</summary>
    public void Forget()
    {
        SetRegistered(false);
        _set = _granted = null;
    }

    void SetRegistered(bool on)
    {
        if (on == _registered) return;
        _registered = on;
        _registeredCount += on ? 1 : -1;
    }

    /// <summary>Gives the strip back (the work area grows again).</summary>
    public void Remove()
    {
        if (!_registered) return;
        _ownMoveAt = Environment.TickCount64;
        Remove(_hwnd);
        _ownMoveAt = Environment.TickCount64;
        SetRegistered(false);
        _set = _granted = null;
    }

    /// <summary>Unregisters a bar by its window handle – also one whose process is gone (the guard after a crash).</summary>
    public static void Remove(IntPtr hwnd)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
        SHAppBarMessage(ABM_REMOVE, ref d);
    }

    APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

    /// <summary>The Windows taskbar's "automatically hide" setting.</summary>
    public static bool TaskbarAutoHide
    {
        get
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            return ((long)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0;
        }
        set
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Native.FindWindow("Shell_TrayWnd", null), lParam = new IntPtr(value ? ABS_AUTOHIDE : ABS_ALWAYSONTOP) };
            SHAppBarMessage(ABM_SETSTATE, ref d);
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_registered) return IntPtr.Zero;
        if (msg == Callback)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED: if (!OwnMoveJustNow) { _set = null; PositionChanged?.Invoke(); } break;
                case ABN_FULLSCREENAPP: FullscreenChanged?.Invoke(lParam != IntPtr.Zero); break;
            }
            handled = true;
        }
        // Windows asks every bar to tell it about activation and moves (the docs' rule for well-behaved bars)
        else if (msg == WM_ACTIVATE) { var d = Data(); SHAppBarMessage(ABM_ACTIVATE, ref d); }
        else if (msg == WM_WINDOWPOSCHANGED) { var d = Data(); SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref d); }
        return IntPtr.Zero;
    }
}
